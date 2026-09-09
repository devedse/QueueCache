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
if ($setup -notmatch 'SetupIconFile=\.\.\\assets\\branding\\queuecache\.ico') { throw 'Installer must use the QueueCache icon.' }
$icon = [IO.BinaryReader]::new([IO.File]::OpenRead("$root/assets/branding/queuecache.ico"))
try {
    if($icon.ReadUInt16() -ne 0 -or $icon.ReadUInt16() -ne 1 -or $icon.ReadUInt16() -ne 7) { throw 'Expected a seven-frame Windows icon.' }
    foreach($size in @(16,24,32,48,64,128,256)) {
        $expected = if($size -eq 256) { 0 } else { $size }
        if($icon.ReadByte() -ne $expected -or $icon.ReadByte() -ne $expected) { throw "Missing icon size: $size" }
        $null=$icon.ReadBytes(6)
        $length=$icon.ReadUInt32(); $offset=$icon.ReadUInt32()
        if($length -eq 0 -or $offset -lt 118 -or $offset+$length -gt $icon.BaseStream.Length) { throw 'Invalid icon frame bounds.' }
    }
} finally { $icon.Dispose() }
& "$env:windir/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/Test-RegistryFilters.ps1"
if ($LASTEXITCODE) { throw 'Windows PowerShell 5.1 registry verification regression failed.' }
& "$PSScriptRoot/Test-RegistryFilters.ps1"
Write-Host 'Packaging syntax/contract checks passed. No installer or updater executed.'
