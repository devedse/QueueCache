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
function Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE) { throw "$Program failed: $LASTEXITCODE" }
}
function UpdatePath([bool]$Remove) {
    $entry = Join-Path $package 'controller'
    $parts = @([Environment]::GetEnvironmentVariable('Path','Machine') -split ';' | Where-Object { $_ -and $_.TrimEnd('\') -ine $entry.TrimEnd('\') })
    if (-not $Remove) { $parts += $entry }
    [Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'Machine')
}
function Assert-RegistryMultiString([string]$Path, [string[]]$Expected) {
    $key = Get-Item -LiteralPath $Path -ErrorAction Stop
    $kind = $key.GetValueKind('UpperFilters')
    # Get-ItemPropertyValue emits REG_MULTI_SZ as one array object in Windows
    # PowerShell 5.1. Wrapping its output in @() produces a nested array whose
    # -join representation is "System.String[]", not the actual filter names.
    [string[]]$actual = (Get-ItemProperty -LiteralPath $Path -Name UpperFilters -ErrorAction Stop).UpperFilters
    Write-Output ("Class filters: expected={0}; actual={1}; type={2}" -f
        (ConvertTo-Json -InputObject @($Expected) -Compress),
        (ConvertTo-Json -InputObject @($actual) -Compress), $kind)
    if ($kind -ne [Microsoft.Win32.RegistryValueKind]::MultiString -or $actual.Count -ne $Expected.Count) {
        throw 'Class filter verification failed: registry type or entry count differs.'
    }
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if (-not [string]::Equals($actual[$i], $Expected[$i], [StringComparison]::OrdinalIgnoreCase)) {
            throw "Class filter verification failed: entry $i differs (including ordering)."
        }
    }
}
try {
    if (-not [Environment]::Is64BitProcess) { throw '64-bit setup is required.' }
    if ($Uninstall) {
        $classPath='HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
        $classInstalled=@((Get-ItemProperty $classPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters) -contains 'qcachelab'
        foreach ($disk in Get-CimInstance Win32_DiskDrive) {
            $filter = & $controller lab-filter inspect $disk.PNPDeviceID | ConvertFrom-Json
            if ($LASTEXITCODE) { throw 'Cannot inspect disk filters; retaining recovery tools.' }
            if ($classInstalled -or $filter.UpperFilters -contains 'qcachelab') {
                # Driver remains loaded until reboot. Do not remove its service or binary.
                Native $controller @('disable', "PhysicalDrive$($disk.Index)")
            }
            if ($filter.UpperFilters -contains 'qcachelab') {
                Native $controller @('lab-filter','remove',$filter.InstanceId,$filter.DriverKey,'--lab-installer')
            }
        }
        $classPath='HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
        $filters=@((Get-ItemProperty $classPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ -and $_ -ine 'qcachelab' })
        if($filters.Count) { New-ItemProperty $classPath -Name UpperFilters -PropertyType MultiString -Value $filters -Force | Out-Null }
        else { Remove-ItemProperty $classPath -Name UpperFilters -ErrorAction SilentlyContinue }
        UpdatePath $true
        Unregister-ScheduledTask -TaskName 'QueueCache-Restore' -Confirm:$false -ErrorAction SilentlyContinue
        Write-Output 'Filters removed. Reboot to unload; driver binaries/service retained for recovery.'
        exit 3010
    }
    $metadata = Get-Content "$package\build-info.json" -Raw | ConvertFrom-Json
    if (-not $metadata.driverSigned -or -not $metadata.labWriteCache) { throw 'A signed QueueCache write-cache package is required.' }
    # Check active Code Integrity, not merely a pending BCD setting. Never
    # alter Secure Boot/test mode automatically as part of application setup.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QueueCacheCodeIntegrity {
    [StructLayout(LayoutKind.Sequential)] public struct Info { public uint Length; public uint Options; }
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, ref Info info, uint size, out uint required);
    public static bool TestSigningActive() { var i = new Info { Length = 8 }; uint n;
        int s = NtQuerySystemInformation(103, ref i, 8, out n);
        if (s < 0) throw new InvalidOperationException("Cannot query active Code Integrity: " + s);
        return (i.Options & 2) != 0;
    }
}
'@
    if (-not [QueueCacheCodeIntegrity]::TestSigningActive()) { throw 'This driver is test-signed. Enable Windows test-signing with Secure Boot disabled, reboot, then rerun setup. No security settings were changed.' }
    if ($metadata.version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid driver version.' }
    foreach ($line in Get-Content "$package\SHA256SUMS.txt") {
        $parts = $line -split '  ',2
        if ($parts.Count -ne 2) { throw 'Invalid checksum manifest.' }
        $path = [IO.Path]::GetFullPath((Join-Path $package $parts[1]))
        if (-not $path.StartsWith([IO.Path]::GetFullPath($package).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid checksum path.' }
        if ((Get-FileHash -LiteralPath $path).Hash -ne $parts[0]) { throw "Checksum mismatch: $($parts[1])" }
    }
    $source = "$package\driver\qcachelab.sys"
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $metadata.testCertificateThumbprint -or
        $signature.Status -notin @('Valid','UnknownError','NotTrusted')) { throw 'Driver signature mismatch.' }
    # Capture recovery data BEFORE changing the service or any registration.
    # Keep it outside the installation directory so uninstall cannot remove it.
    $servicePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\qcachelab'
    $classPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
    $filters = @((Get-ItemProperty $classPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ })
    $previousService = if(Test-Path $servicePath) { Get-ItemProperty $servicePath | Select-Object ImagePath,Start,Type,Group,ErrorControl,ClassCoverage,LabAllowedDriverKey } else { $null }
    $deviceFilters = @(foreach($disk in Get-CimInstance Win32_DiskDrive) {
        $filter = & $controller lab-filter inspect $disk.PNPDeviceID | ConvertFrom-Json
        if($LASTEXITCODE) { throw 'Cannot capture existing disk registration; no registration changed.' }
        $filter
    })
    $backup = "$stateRoot\Registration-before-$([guid]::NewGuid().ToString('N')).json"
    [pscustomobject]@{ Version=1; CreatedUtc=[DateTime]::UtcNow.ToString('O'); Service=$previousService; ClassUpperFilters=$filters; Devices=$deviceFilters } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backup -Encoding UTF8
    Write-Output "Original registration saved to $backup"
    # An immutable version/hash filename can be staged while the old driver is
    # loaded. SCM uses the new ImagePath on the next boot; no live replacement.
    $hash = (Get-FileHash -LiteralPath $source).Hash
    $relative = "System32\drivers\QueueCache-$($metadata.version)-$($hash.Substring(0,12)).sys"
    $destination = Join-Path $env:windir $relative
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash) { throw 'Existing staged driver hash mismatch.' }
    } else { Copy-Item -LiteralPath $source -Destination $destination }
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $hash) { throw 'Staged driver verification failed.' }
    $servicePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\qcachelab'
    if (-not (Test-Path $servicePath)) {
        Native sc.exe @('create','qcachelab','type=','kernel','start=','demand','error=','normal','binPath=',$relative,'DisplayName=','QueueCache')
        New-ItemProperty $servicePath -Name LabAllowedDriverKey -PropertyType String -Value 'unconfigured' | Out-Null
    } else {
        $service = Get-ItemProperty $servicePath
        if ($service.Type -ne 1 -or $service.ImagePath -notmatch '^(\\SystemRoot\\)?System32\\drivers\\(qcachelab|QueueCache-[\w.-]+)\.sys$') {
            throw 'Unexpected existing service; refusing to replace it.'
        }
        # Preserve existing target and start type, including an explicitly selected boot target.
        Native sc.exe @('config','qcachelab','binPath=',$relative,'DisplayName=','QueueCache')
    }
    UpdatePath $false
    # Class registration covers disks enumerated in future as well as existing
    # disks after restart. Preserve every unrelated filter and its ordering.
    $classPath='HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
    $filters=@((Get-ItemProperty $classPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ })
    New-ItemProperty $servicePath -Name ClassCoverage -PropertyType DWord -Value 1 -Force | Out-Null
    Native sc.exe @('config','qcachelab','start=','boot','group=','Filter')
    if($filters -notcontains 'qcachelab') { $filters+='qcachelab' }
    New-ItemProperty $classPath -Name UpperFilters -PropertyType MultiString -Value ([string[]]$filters) -Force | Out-Null
    Assert-RegistryMultiString -Path $classPath -Expected $filters
    foreach($filter in $deviceFilters) {
        if($filter.UpperFilters -contains 'qcachelab') { Native $controller @('lab-filter','remove',$filter.InstanceId,$filter.DriverKey,'--lab-installer') }
    }
    $action = New-ScheduledTaskAction -Execute $controller -Argument 'policy restore'
    $trigger = New-ScheduledTaskTrigger -AtStartup; $trigger.Delay='PT30S'
    $principal = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName QueueCache-Restore -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Output "Driver staged at $destination. Reboot to load automatic disk coverage. No cache task was enabled. Test-signing prerequisites still apply."
    exit 3010
} catch {
    Write-Output "SETUP FAILED: $_"
    exit 1
} finally { Stop-Transcript | Out-Null }
