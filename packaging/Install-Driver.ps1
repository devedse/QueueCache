#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$package = Split-Path $PSScriptRoot -Parent
$controller = Join-Path $package 'controller\qcache.exe'
$stateRoot = Join-Path $env:ProgramData 'QueueCache'
New-Item -ItemType Directory -Path "$stateRoot\Logs" -Force | Out-Null
$log = "$stateRoot\Logs\Setup-$(Get-Date -Format yyyyMMdd-HHmmss)-$PID.log"
Start-Transcript -Path $log | Out-Null
function Native([string]$Program, [string[]]$Arguments)
{
    & $Program @Arguments
    if ($LASTEXITCODE)
    {
        throw "$Program failed: $LASTEXITCODE"
    }
}
# The desktop app needs administrator rights, which Windows' Run key silently skips; a scheduled
# task can start it elevated at sign-in. It starts in the notification area (--tray). Settings
# turns the task on or off; an update keeps that choice.
$signInTask = 'QueueCache-SignIn'
function Register-SignInTask
{
    $existing = Get-ScheduledTask -TaskName $signInTask -ErrorAction SilentlyContinue
    $enabled = -not $existing -or $existing.Settings.Enabled
    $desktop = Join-Path $package 'desktop\QueueCache.Desktop.exe'
    $action = New-ScheduledTaskAction -Execute $desktop -Argument '--tray'
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    # Any administrator who signs in, elevated without a prompt (S-1-5-32-544 = Administrators).
    $principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-544' -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -Priority 4
    $settings.Enabled = $enabled
    Register-ScheduledTask -TaskName $signInTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'Starts QueueCache in the notification area when you sign in. Turn off in QueueCache Settings.' -Force | Out-Null
    Write-Output "Sign-in task registered ($(if ($enabled) { 'on' } else { 'off, as chosen before' }))."
}
function UpdatePath([bool]$Remove)
{
    $entry = Join-Path $package 'controller'
    $parts = @([Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ine $entry.TrimEnd('\') })
    if (-not $Remove)
    {
        $parts += $entry
    }
    [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'Machine')
}
$volumeClassPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}'
function Get-QueueCacheClassFilters([string[]]$Existing)
{
    # QueueCache filters volumes. UpperFilters are attached in list order (last
    # entry is highest): QueueCache is last, directly below the file system, so it
    # sees file-system requests (and its own management requests on a volume handle)
    # first, and its write-back passes through snapshots (volsnap) and the Volume
    # class lower filters (BitLocker, ReadyBoost) below it. Move only our entry;
    # preserve the relative order of every other filter.
    $others = @($Existing | Where-Object { $_ -and $_ -ine 'qcachelab' })
    return [string[]]($others + 'qcachelab')
}
function Remove-QueueCacheClassFilter([string]$Path)
{
    # Remove only our entry; keep every other filter.
    $filters = @((Get-ItemProperty $Path -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ -and $_ -ine 'qcachelab' })
    if ($filters.Count)
    {
        New-ItemProperty $Path -Name UpperFilters -PropertyType MultiString -Value ([string[]]$filters) -Force | Out-Null
    }
    else
    {
        Remove-ItemProperty $Path -Name UpperFilters -ErrorAction SilentlyContinue
    }
}
function Assert-RegistryMultiString([string]$Path, [string[]]$Expected)
{
    $key = Get-Item -LiteralPath $Path -ErrorAction Stop
    $kind = $key.GetValueKind('UpperFilters')
    # Get-ItemPropertyValue emits REG_MULTI_SZ as one array object in Windows
    # PowerShell 5.1. Wrapping its output in @() produces a nested array whose
    # -join representation is "System.String[]", not the actual filter names.
    [string[]]$actual = (Get-ItemProperty -LiteralPath $Path -Name UpperFilters -ErrorAction Stop).UpperFilters
    Write-Output ("Class filters: expected={0}; actual={1}; type={2}" -f
        (ConvertTo-Json -InputObject @($Expected) -Compress),
        (ConvertTo-Json -InputObject @($actual) -Compress), $kind)
    if ($kind -ne [Microsoft.Win32.RegistryValueKind]::MultiString -or $actual.Count -ne $Expected.Count)
    {
        throw 'Class filter verification failed: registry type or entry count differs.'
    }
    for ($i = 0; $i -lt $Expected.Count; $i++)
    {
        if (-not [string]::Equals($actual[$i], $Expected[$i], [StringComparison]::OrdinalIgnoreCase))
        {
            throw "Class filter verification failed: entry $i differs (including ordering)."
        }
    }
}
# Restores the registration recorded before this setup changed it, for the next boot, with the
# semantics of Recover-Registration.ps1. A driver that is loaded now keeps running until then; the
# registry only takes effect at the next boot. Never point the boot filter at a missing file.
function Restore-PreviousRegistration([string[]]$Filters, $Service, [string]$BackupPath)
{
    $servicePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\qcachelab'
    if ($null -ne $Service)
    {
        $previousDriver = Join-Path $env:windir (([string]$Service.ImagePath) -replace '^\\SystemRoot\\', '')
        if (-not (Test-Path -LiteralPath $previousDriver))
        {
            throw "the previous driver $($Service.ImagePath) no longer exists, so the new registration was kept. Rerun setup, or restore it with Recover-Registration.ps1 -BackupFile $BackupPath."
        }
    }
    [string[]]$previous = @($Filters | Where-Object { $_ })
    if ($previous.Count)
    {
        New-ItemProperty $volumeClassPath -Name UpperFilters -PropertyType MultiString -Value $previous -Force | Out-Null
        Assert-RegistryMultiString -Path $volumeClassPath -Expected $previous
    }
    else
    {
        Remove-ItemProperty $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue
    }
    if ($null -eq $Service)
    {
        if (Test-Path $servicePath) { Native sc.exe @('delete', 'qcachelab') }
    }
    else
    {
        foreach ($name in @('ImagePath', 'Group', 'LabAllowedDriverKey'))
        {
            $value = $Service.$name
            if ($null -eq $value) { Remove-ItemProperty -LiteralPath $servicePath -Name $name -ErrorAction SilentlyContinue }
            else { New-ItemProperty -LiteralPath $servicePath -Name $name -PropertyType $(if ($name -eq 'ImagePath') { 'ExpandString' } else { 'String' }) -Value ([string]$value) -Force | Out-Null }
        }
        foreach ($name in @('Start', 'Type', 'ErrorControl', 'ClassCoverage'))
        {
            $value = $Service.$name
            if ($null -eq $value) { Remove-ItemProperty -LiteralPath $servicePath -Name $name -ErrorAction SilentlyContinue }
            else { New-ItemProperty -LiteralPath $servicePath -Name $name -PropertyType DWord -Value ([int]$value) -Force | Out-Null }
        }
    }
    $systemHive = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM')
    try { $systemHive.Flush() } finally { $systemHive.Dispose() }
}
$activating = $false
try
{
    if (-not [Environment]::Is64BitProcess)
    {
        throw '64-bit setup is required.'
    }
    Native $controller @('--managed-update-preflight')
    if ($Uninstall)
    {
        Native $controller @('--managed-service-remove')
        Native $controller @('--managed-provider-remove')
        $volumeInstalled = @((Get-ItemProperty $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters) -contains 'qcachelab'
        if ($volumeInstalled)
        {
            # Driver remains loaded until reboot. Do not remove its service or binary.
            # Only lettered volumes can have caching enabled; drain and disable each one
            # with a cache. Exit code 4 means no filter answers there (not loaded yet), so
            # that volume has no cache; any other failure stops uninstall.
            # DriveInfo, not Get-Volume: the Storage WMI provider does not list every
            # system's volumes (some machines report no MSFT_Volume for C:).
            foreach ($drive in [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' })
            {
                $letter = $drive.Name.Substring(0, 2)
                $json = & $controller cache-status $letter --json
                if ($LASTEXITCODE -eq 4)
                {
                    Write-Output "$letter has no QueueCache filter loaded; nothing to drain."
                    continue
                }
                if ($LASTEXITCODE)
                {
                    throw "Cannot read the cache state of $letter; retaining QueueCache."
                }
                if ([uint64](($json | Out-String) | ConvertFrom-Json).BudgetBytes -gt 0)
                {
                    Native $controller @('disable', $letter)
                    Write-Output "$letter cache drained and disabled."
                }
            }
        }
        Remove-QueueCacheClassFilter $volumeClassPath
        # Nothing references the filter any more; keep the service and binary for recovery
        # but stop loading it at boot. Installation sets boot start again.
        if (Get-Service qcachelab -ErrorAction SilentlyContinue)
        {
            Native sc.exe @('config', 'qcachelab', 'start=', 'demand')
        }
        UpdatePath $true
        Unregister-ScheduledTask -TaskName 'QueueCache-Restore' -Confirm:$false -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $signInTask -Confirm:$false -ErrorAction SilentlyContinue
        Write-Output 'Filters removed. Reboot to unload; driver binaries/service retained for recovery.'
        exit 3010
    }
    $metadata = Get-Content "$package\build-info.json" -Raw | ConvertFrom-Json
    if (-not $metadata.driverSigned -or -not $metadata.labWriteCache)
    {
        throw 'A signed QueueCache write-cache package is required.'
    }
    # Check active Code Integrity, not merely a pending BCD setting. Never
    # alter Secure Boot/test mode automatically as part of application setup.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class QueueCacheSetupNative {
    [StructLayout(LayoutKind.Sequential)] public struct Info { public uint Length; public uint Options; }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ref Info info, uint size, out uint required);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FlushFileBuffers(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static bool TestSigningActive() { var i = new Info { Length = 8 }; uint n;
        int s = NtQuerySystemInformation(103, ref i, 8, out n);
        if (s < 0) throw new InvalidOperationException("Cannot query active Code Integrity: " + s);
        return (i.Options & 2) != 0;
    }
    // What Write-VolumeCache does, without the Storage WMI provider (which may list no volume for C:).
    public static void FlushVolume(string letter) {
        IntPtr h = CreateFileW(@"\\.\" + letter + ":", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open volume " + letter + ": to flush it");
        try { if (!FlushFileBuffers(h)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot flush volume " + letter + ":"); }
        finally { CloseHandle(h); }
    }
}
'@
    if (-not [QueueCacheSetupNative]::TestSigningActive())
    {
        throw 'This driver is test-signed. Enable Windows test-signing with Secure Boot disabled, reboot, then rerun setup. No security settings were changed.'
    }
    if ($metadata.version -notmatch '^\d+\.\d+\.\d+\.\d+$')
    {
        throw 'Invalid driver version.'
    }
    foreach ($line in Get-Content "$package\SHA256SUMS.txt")
    {
        $parts = $line -split '  ', 2
        if ($parts.Count -ne 2)
        {
            throw 'Invalid checksum manifest.'
        }
        $path = [IO.Path]::GetFullPath((Join-Path $package $parts[1]))
        if (-not $path.StartsWith([IO.Path]::GetFullPath($package).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))
        {
            throw 'Invalid checksum path.'
        }
        if ((Get-FileHash -LiteralPath $path).Hash -ne $parts[0])
        {
            throw "Checksum mismatch: $($parts[1])"
        }
    }
    $source = "$package\driver\qcachelab.sys"
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $metadata.testCertificateThumbprint -or
        $signature.Status -notin @('Valid', 'UnknownError', 'NotTrusted'))
    {
        throw 'Driver signature mismatch.'
    }
    # Capture recovery data BEFORE changing the service or any registration.
    # Keep it outside the installation directory so uninstall cannot remove it.
    $servicePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\qcachelab'
    $volumeFilters = @((Get-ItemProperty $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ })
    $previousService = if (Test-Path $servicePath)
    {
        Get-ItemProperty $servicePath | Select-Object ImagePath, Start, Type, Group, ErrorControl, ClassCoverage, LabAllowedDriverKey
    }
    else
    {
        $null
    }
    $backup = "$stateRoot\Registration-before-$([guid]::NewGuid().ToString('N')).json"
    [pscustomobject]@{ Version = 3; CreatedUtc = [DateTime]::UtcNow.ToString('O'); Service = $previousService; VolumeClassUpperFilters = $volumeFilters } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backup -Encoding UTF8
    Write-Output "Original registration saved to $backup"
    # An immutable version/hash filename can be staged while the old driver is
    # loaded. SCM uses the new ImagePath on the next boot; no live replacement.
    $hash = (Get-FileHash -LiteralPath $source).Hash
    $relative = "System32\drivers\QueueCache-$($metadata.version)-$($hash.Substring(0,12)).sys"
    $destination = Join-Path $env:windir $relative
    if (Test-Path -LiteralPath $destination)
    {
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash)
        {
            throw 'Existing staged driver hash mismatch.'
        }
    }
    else
    {
        Copy-Item -LiteralPath $source -Destination $destination
    }
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash)
    {
        throw 'Staged driver verification failed.'
    }
    # Everything the next boot does not load comes first, so a failure (or a crash) here leaves
    # Windows booting exactly as before. Installing the RAM provider starts its root adapter
    # immediately; with no new boot filter registered yet, a crash there cannot leave a boot-start
    # driver behind that points at an unflushed file.
    Import-Certificate -FilePath "$package\QueueCacheLab.cer" -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath "$package\QueueCacheLab.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    UpdatePath $false
    & $controller --managed-provider-install "$package\ramdisk\qcramdisk.inf"
    if ($LASTEXITCODE -notin @(0, 3010)) { throw "RAM provider installation failed: $LASTEXITCODE" }
    # One startup coordinator owns managed resources before regular saved profiles.
    # Start at the required reboot, after the intended native modules are loaded.
    Native $controller @('--managed-service-install')
    Unregister-ScheduledTask -TaskName 'QueueCache-Restore' -Confirm:$false -ErrorAction SilentlyContinue
    Register-SignInTask
    $servicePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\qcachelab'
    if (Test-Path $servicePath)
    {
        $service = Get-ItemProperty $servicePath
        if ($service.Type -ne 1 -or $service.ImagePath -notmatch '^(\\SystemRoot\\)?System32\\drivers\\(qcachelab|QueueCache-[\w.-]+)\.sys$')
        {
            throw 'Unexpected existing service; refusing to replace it.'
        }
    }
    # Activation, last: the boot-start filter service points at the staged driver and the Volume
    # class lists it. From here a failure restores the saved registration (see catch).
    $activating = $true
    if (-not (Test-Path $servicePath))
    {
        Native sc.exe @('create', 'qcachelab', 'type=', 'kernel', 'start=', 'demand', 'error=', 'normal', 'binPath=', $relative, 'DisplayName=', 'QueueCache')
        New-ItemProperty $servicePath -Name LabAllowedDriverKey -PropertyType String -Value 'unconfigured' | Out-Null
    }
    else
    {
        # Preserve existing target and start type, including an explicitly selected boot target.
        Native sc.exe @('config', 'qcachelab', 'binPath=', $relative, 'DisplayName=', 'QueueCache')
    }
    # Volume-class registration covers volumes created in future as well as existing
    # volumes after restart. Preserve every unrelated filter and its ordering.
    New-ItemProperty $servicePath -Name ClassCoverage -PropertyType DWord -Value 1 -Force | Out-Null
    Native sc.exe @('config', 'qcachelab', 'start=', 'boot', 'group=', 'Filter')
    $filters = @((Get-ItemProperty $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ })
    [string[]]$filters = Get-QueueCacheClassFilters $filters
    New-ItemProperty $volumeClassPath -Name UpperFilters -PropertyType MultiString -Value ([string[]]$filters) -Force | Out-Null
    Assert-RegistryMultiString -Path $volumeClassPath -Expected $filters
    # Persist the registration and the staged driver before reporting success.
    $systemHive = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM')
    try { $systemHive.Flush() } finally { $systemHive.Dispose() }
    [QueueCacheSetupNative]::FlushVolume($env:SystemDrive.Substring(0, 1))
    Write-Output "Driver staged at $destination. Reboot to load automatic volume coverage. No cache task was enabled. Test-signing prerequisites still apply."
    exit 3010
}
catch
{
    Write-Output "SETUP FAILED: $_"
    if ($Uninstall) { exit 1 }
    if (-not $activating)
    {
        Write-Output 'The driver registration was not changed; the next restart loads what it loaded before.'
        exit 1
    }
    try
    {
        Restore-PreviousRegistration $volumeFilters $previousService $backup
        Write-Output "The previous driver registration was restored from $backup; the next restart loads what it loaded before."
        exit 1
    }
    catch
    {
        # Exit 5: setup tells the user not to restart before recovering.
        Write-Output "ROLLBACK FAILED: $_"
        exit 5
    }
}
finally
{
    Stop-Transcript | Out-Null
}
