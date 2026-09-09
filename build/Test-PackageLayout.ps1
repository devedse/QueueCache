#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($folder in @('tests','write-tests','file-tests','lab')) {
    if (Test-Path -LiteralPath (Join-Path $package $folder)) { throw "Obsolete installed folder: $folder" }
}
foreach ($file in @('controller/qcache.exe','controller/QueueCache.Developer.dll','desktop/QueueCache.Desktop.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $file))) { throw "Missing packaged application component: $file" }
}
if (@(Get-ChildItem -LiteralPath $package -Filter 'qcache-*-tests.exe' -Recurse).Count) { throw 'Standalone test executable leaked into package.' }
Write-Host 'Package layout verified: shared developer library; no standalone test applications.'
