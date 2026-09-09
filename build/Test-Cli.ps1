#Requires -Version 7
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release', [string]$CliPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$candidates = @("src/QueueCache.Cli/bin/$Configuration/net10.0/win-x64/qcache.exe", "src/QueueCache.Cli/bin/$Configuration/net10.0/qcache.exe")
$cli = $candidates | ForEach-Object { Get-Item (Join-Path $root $_) -ErrorAction SilentlyContinue } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if ($CliPath) { $cli = (Resolve-Path -LiteralPath $CliPath).Path }
foreach ($arguments in @(@('--help'),@('apply','--help'),@('policy','--help'),@('policy','apply','--help'),@('policy','enable','--help'),@('policy','set','--help'),@('disk','list','--help'),@('disk','attach','--help'),@('test','--help'),@('benchmark','--help'),@('--version'))) {
    & $cli @arguments
    if ($LASTEXITCODE) { throw "Help/version failed: $arguments" }
}
& $cli test --does-not-exist 2>&1 | Out-Host
if ($LASTEXITCODE -ne 2) { throw 'Invalid arguments must return 2 before device access.' }
& $cli apply 'Q:' --budget-mib 0 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1) { throw 'Invalid budget must fail before device access.' }
& $cli policy apply 'Q:' --budget-mib 4097 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1) { throw 'Grouped invalid budget must fail before device access.' }
foreach($name in @('pause','resume','remove')) {
    & $cli policy $name --help
    if($LASTEXITCODE) { throw "Task help failed: $name" }
    & $cli policy $name 'Q:\not-a-volume' 2>&1 | Out-Host
    if($LASTEXITCODE -ne 2) { throw 'Invalid task volume must fail before disk access.' }
}
& $cli disk attach 'Q:\not-a-volume' 2>&1 | Out-Host
if ($LASTEXITCODE -ne 2) { throw 'Invalid attachment target must fail during parsing.' }
Write-Host 'CLI contract checks passed. No disk handle opened.'
foreach ($command in @(@('developer'), @('developer','test'), @('developer','write-tests'), @('developer','file-tests'), @('developer','driver'), @('developer','driver','delay'), @('developer','driver','fault'), @('developer','driver','inspect'))) {
    & $cli @command --help
    if ($LASTEXITCODE) { throw "Developer help failed: $command" }
}
foreach ($arguments in @(
    @('developer','write-tests','1','4294967296','invalid','unknown-mode'),
    @('developer','write-tests','0','4294967296','invalid','write-disposable-region'),
    @('developer','write-tests','1','4294967296','invalid','write-dirty-prefix','--prefix-bytes','65536'),
    @('developer','write-tests','1','4294967296','invalid','verify-base-prefix','--prefix-bytes','1'),
    @('developer','file-tests','Q','1','4294967296','invalid','verify-files','--run-id','invalid'),
    @('developer','file-tests','Q','1','4294967296','invalid','test-coalescing','--size-mib','64'),
    @('developer','file-tests','Q','0','4294967296','invalid','write-new-files'),
    @('developer','test','-1','4294967296','invalid')
)) {
    & $cli @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 2) { throw "Developer invalid arguments must fail before disk access: $arguments" }
}
Write-Host 'Developer CLI contract checks passed. No disk handle opened.'
# GitHub's pwsh wrapper propagates the last native exit code. The negative tests
# intentionally leave it nonzero, so report this script's own successful result.
exit 0
