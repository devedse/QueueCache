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
# Version 1: disk-class installs. Version 2 also records the Volume class list.
if (-not $ConfirmRestore -or $backup.Version -notin @(1, 2) -or -not $backup.CreatedUtc -or $null -eq $backup.ClassUpperFilters -or
    $null -eq $backup.Devices -or ($backup.Version -eq 2 -and $null -eq $backup.VolumeClassUpperFilters))
{
    throw 'A version-1 or version-2 QueueCache registration backup and explicit -ConfirmRestore are required.'
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
    $volumeClassPath = Join-Path $systemRoot 'Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}'
    if (-not (Test-Path -LiteralPath $volumeClassPath)) { throw 'Volume class registry key is missing.' }
    # A version-1 backup predates volume registration: restore that list without our entry.
    [string[]]$volumeFilters = if ($backup.Version -eq 2) { @($backup.VolumeClassUpperFilters | Where-Object { $_ }) }
        else { @((Get-ItemProperty -LiteralPath $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters | Where-Object { $_ -and $_ -ine 'qcachelab' }) }
    # Validate every recorded target before changing any registration. A stale or
    # malformed per-device key must not leave only the class filter restored.
    $deviceTargets = @(
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
            [pscustomobject]@{ Path = $devicePath; Filters = [string[]]@($device.UpperFilters | Where-Object { $_ }) }
        }
    )
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

    if ($PSCmdlet.ShouldProcess($volumeClassPath, "restore volume-class UpperFilters from $backupPath"))
    {
        if ($volumeFilters.Count)
        {
            New-ItemProperty -LiteralPath $volumeClassPath -Name UpperFilters -PropertyType MultiString -Value $volumeFilters -Force | Out-Null
        }
        else
        {
            Remove-ItemProperty -LiteralPath $volumeClassPath -Name UpperFilters -ErrorAction SilentlyContinue
        }
    }

    foreach ($device in $deviceTargets)
    {
        if ($PSCmdlet.ShouldProcess($device.Path, 'restore per-device UpperFilters'))
        {
            if ($device.Filters.Count)
            {
                New-ItemProperty -LiteralPath $device.Path -Name UpperFilters -PropertyType MultiString -Value $device.Filters -Force | Out-Null
            }
            else
            {
                Remove-ItemProperty -LiteralPath $device.Path -Name UpperFilters -ErrorAction SilentlyContinue
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
    if ($WhatIfPreference)
    {
        Write-Output "Dry run only; registration was not restored from $backupPath."
    }
    else
    {
        Write-Output "Registration restored from $backupPath. Reboot normally before loading storage drivers; this script does not reboot."
    }
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
