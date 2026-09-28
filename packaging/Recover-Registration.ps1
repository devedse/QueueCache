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
# Version 3: the Volume class UpperFilters and the service values before installation.
if (-not $ConfirmRestore -or $backup.Version -ne 3 -or -not $backup.CreatedUtc -or $null -eq $backup.VolumeClassUpperFilters)
{
    throw 'A version-3 QueueCache registration backup and explicit -ConfirmRestore are required.'
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

    $volumeClassPath = Join-Path $systemRoot 'Control\Class\{71a27cdd-812a-11d0-bec7-08002be2092f}'
    if (-not (Test-Path -LiteralPath $volumeClassPath)) { throw 'Volume class registry key is missing.' }
    [string[]]$volumeFilters = @($backup.VolumeClassUpperFilters | Where-Object { $_ })
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
