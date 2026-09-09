#Requires -Version 7
$ErrorActionPreference='Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($name in @('Install-Driver.ps1','Update-QueueCache.ps1')) {
    $path = Join-Path $root "packaging/$name"
    $tokens=$null; $parseErrors=$null
    $null = [Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
$installer = Get-Content "$root/packaging/Install-Driver.ps1" -Raw
if ($installer -match '\bRead-Host\b|SnapshotConfirmed|AllowFormattedDisk') { throw 'Product installation must not prompt for a disk or lab confirmations.' }
$setup = Get-Content "$root/packaging/QueueCache.iss" -Raw
if ($setup -match '\bSW_SHOW\b' -or $setup -notmatch '\{commondesktop\}\\QueueCache') { throw 'Installer must hide its helper and create the UI desktop shortcut.' }
Write-Host 'Packaging syntax/contract checks passed. No installer or updater executed.'
