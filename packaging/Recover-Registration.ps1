#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [string]$OfflineSystemHive,
    [Parameter(Mandatory)][switch]$ConfirmRestore
)
$ErrorActionPreference = 'Stop'
$backupPath = (Resolve-Path -LiteralPath $BackupFile).Path
$backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
if (-not $ConfirmRestore -or $backup.Version -ne 1 -or -not $backup.CreatedUtc -or $null -eq $backup.ClassUpperFilters -or $null -eq $backup.Devices)
{
    throw 'A version-1 QueueCache registration backup and explicit -ConfirmRestore are required.'
}

$mountName = 'QueueCacheRecovery'
$mounted = $false
try
{
    if ($OfflineSystemHive)
    {
        $hive = (Resolve-Path -LiteralPath $OfflineSystemHive).Path
        if (Test-Path "Registry::HKEY_LOCAL_MACHINE\$mountName")
        {
            throw "HKLM\$mountName is already loaded; inspect and unload it before retrying."
        }
        & reg.exe load "HKLM\$mountName" $hive | Out-Host
        if ($LASTEXITCODE) { throw "Cannot load offline SYSTEM hive: exit $LASTEXITCODE" }
        $mounted = $true
        $current = [int](Get-ItemPropertyValue "Registry::HKEY_LOCAL_MACHINE\$mountName\Select" Current)
        if ($current -lt 1 -or $current -gt 999) { throw 'Offline SYSTEM hive has an invalid current control set.' }
        $systemRoot = "Registry::HKEY_LOCAL_MACHINE\$mountName\ControlSet$($current.ToString('000'))"
    }
    else
    {
        $service = Get-Service qcachelab -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped')
        {
            throw 'QueueCache is loaded. Use Safe Mode or an offline SYSTEM hive; do not rewrite registration under a running driver.'
        }
        $systemRoot = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet'
    }

    $classPath = Join-Path $systemRoot 'Control\Class\{4d36e967-e325-11ce-bfc1-08002be10318}'
    if (-not (Test-Path -LiteralPath $classPath)) { throw 'Disk class registry key is missing.' }
    [string[]]$classFilters = @($backup.ClassUpperFilters | Where-Object { $_ })
    if ($PSCmdlet.ShouldProcess($classPath, "restore disk-class UpperFilters from $backupPath"))
    {
        if ($classFilters.Count)
        {
            New-ItemProperty -LiteralPath $classPath -Name UpperFilters -PropertyType MultiString -Value $classFilters -Force | Out-Null
        }
        else
        {
            Remove-ItemProperty -LiteralPath $classPath -Name UpperFilters -ErrorAction SilentlyContinue
        }
    }

    foreach ($device in @($backup.Devices))
    {
        if ($device.DriverKey -notmatch '^\{[0-9A-Fa-f-]{36}\}\\[0-9]{4}$' -or $null -eq $device.UpperFilters)
        {
            throw 'Backup contains an invalid disk driver key or filter list.'
        }
        $devicePath = Join-Path (Join-Path $systemRoot 'Control\Class') $device.DriverKey
        if (-not (Test-Path -LiteralPath $devicePath))
        {
            throw "Recorded disk registry key is absent: $($device.DriverKey)"
        }
        [string[]]$filters = @($device.UpperFilters | Where-Object { $_ })
        if ($PSCmdlet.ShouldProcess($devicePath, 'restore per-device UpperFilters'))
        {
            if ($filters.Count)
            {
                New-ItemProperty -LiteralPath $devicePath -Name UpperFilters -PropertyType MultiString -Value $filters -Force | Out-Null
            }
            else
            {
                Remove-ItemProperty -LiteralPath $devicePath -Name UpperFilters -ErrorAction SilentlyContinue
            }
        }
    }

    $servicePath = Join-Path $systemRoot 'Services\qcachelab'
    if ($null -eq $backup.Service)
    {
        if ((Test-Path -LiteralPath $servicePath) -and $PSCmdlet.ShouldProcess($servicePath, 'remove service absent from pre-install backup'))
        {
            Remove-Item -LiteralPath $servicePath -Recurse -Force
        }
    }
    else
    {
        if ($PSCmdlet.ShouldProcess($servicePath, 'restore pre-install service values'))
        {
            $null = New-Item -Path $servicePath -Force
            foreach ($name in @('ImagePath', 'Group', 'LabAllowedDriverKey'))
            {
                $value = $backup.Service.$name
                if ($null -eq $value) { Remove-ItemProperty -LiteralPath $servicePath -Name $name -ErrorAction SilentlyContinue }
                else { New-ItemProperty -LiteralPath $servicePath -Name $name -PropertyType $(if ($name -eq 'ImagePath') { 'ExpandString' } else { 'String' }) -Value ([string]$value) -Force | Out-Null }
            }
            foreach ($name in @('Start', 'Type', 'ErrorControl', 'ClassCoverage'))
            {
                $value = $backup.Service.$name
                if ($null -eq $value) { Remove-ItemProperty -LiteralPath $servicePath -Name $name -ErrorAction SilentlyContinue }
                else { New-ItemProperty -LiteralPath $servicePath -Name $name -PropertyType DWord -Value ([int]$value) -Force | Out-Null }
            }
        }
    }
    Write-Output "Registration restored from $backupPath. Reboot normally before loading storage drivers; this script does not reboot."
}
finally
{
    if ($mounted)
    {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload "HKLM\$mountName" | Out-Host
        if ($LASTEXITCODE) { Write-Warning "Offline hive remains mounted at HKLM\$mountName; unload it before retrying or rebooting." }
    }
}
