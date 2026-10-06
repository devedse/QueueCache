#Requires -Version 7
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release', [string]$CliPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$candidates = @("src/QueueCache.Cli/bin/$Configuration/net10.0/win-x64/qcache.exe", "src/QueueCache.Cli/bin/$Configuration/net10.0/qcache.exe")
$cli = $candidates | ForEach-Object { Get-Item (Join-Path $root $_) -ErrorAction SilentlyContinue } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
if ($CliPath)
{
    $cli = (Resolve-Path -LiteralPath $CliPath).Path
}
foreach ($arguments in @(@('--help'), @('apply', '--help'), @('policy', '--help'), @('policy', 'apply', '--help'), @('policy', 'enable', '--help'), @('policy', 'set', '--help'), @('volume', '--help'), @('volume', 'list', '--help'), @('disk', 'list', '--help'), @('test', '--help'), @('benchmark', '--help'), @('--version')))
{
    & $cli @arguments
    if ($LASTEXITCODE)
    {
        throw "Help/version failed: $arguments"
    }
}
& $cli test --does-not-exist 2>&1 | Out-Host
if ($LASTEXITCODE -ne 2)
{
    throw 'Invalid arguments must return 2 before device access.'
}
& $cli apply 'Q:' --budget-mib 0 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1)
{
    throw 'Invalid budget must fail before device access.'
}
& $cli policy apply 'Q:' --budget-mib 131073 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1)
{
    throw 'Grouped invalid budget must fail before device access.'
}
$consentOutput = & $cli policy apply 'Q:' --budget-mib 64 2>&1 | Out-String
if ($LASTEXITCODE -ne 1 -or $consentOutput -notmatch 'Explicitly accept volatile flushes')
{
    throw 'Fast apply must require explicit volatility consent before device access.'
}
foreach ($name in @('pause', 'resume', 'remove'))
{
    & $cli policy $name --help
    if ($LASTEXITCODE)
    {
        throw "Task help failed: $name"
    }
    & $cli policy $name 'Q:\not-a-volume' 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 2)
    {
        throw 'Invalid task volume must fail before disk access.'
    }
}
Write-Host 'CLI contract checks passed. No disk handle opened.'
foreach ($command in @(@('developer'), @('developer', 'verify'), @('developer', 'verify-status'), @('developer', 'verify-recover'), @('developer', 'test'), @('developer', 'write-tests'), @('developer', 'file-tests'), @('developer', 'driver'), @('developer', 'driver', 'delay'), @('developer', 'driver', 'fault'), @('developer', 'driver', 'registration'), @('developer', 'lab-disk'), @('developer', 'lab-disk', 'create'), @('developer', 'lab-disk', 'attach'), @('developer', 'lab-disk', 'detach')))
{
    & $cli @command --help
    if ($LASTEXITCODE)
    {
        throw "Developer help failed: $command"
    }
}
foreach ($arguments in @(
        @('developer', 'verify', 'Q:', '--suite', 'not-a-suite'),
        @('developer', 'verify', 'Q:', '--detach'),
        @('developer', 'verify', 'Q:', '--suite', 'managed-lifecycle-verify', '--managed-transition', 'not-a-transition'),
        @('developer', 'write-tests', 'X:', '4294967296', '{00000000-0000-0000-0000-000000000001}', 'unknown-mode'),
        @('developer', 'write-tests', 'PhysicalDrive1', '4294967296', '{00000000-0000-0000-0000-000000000001}', 'write-disposable-region'),
        @('developer', 'write-tests', 'X:', '4294967296', 'invalid', 'write-disposable-region'),
        @('developer', 'write-tests', 'X:', '4294967296', '{00000000-0000-0000-0000-000000000001}', 'write-dirty-prefix', '--prefix-bytes', '65536'),
        @('developer', 'write-tests', 'X:', '4294967296', '{00000000-0000-0000-0000-000000000001}', 'verify-base-prefix', '--prefix-bytes', '1'),
        @('developer', 'file-tests', 'Q', '1', '4294967296', 'invalid', 'verify-files', '--run-id', 'invalid'),
        @('developer', 'file-tests', 'Q', '1', '4294967296', 'invalid', 'test-coalescing', '--size-mib', '64'),
        @('developer', 'file-tests', 'Q', '0', '4294967296', 'invalid', 'write-new-files'),
        @('developer', 'test', 'PhysicalDrive1', '4294967296', '{00000000-0000-0000-0000-000000000001}'),
        @('developer', 'test', 'Q:', '4294967296', 'invalid')
    ))
{
    & $cli @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 2)
    {
        throw "Developer invalid arguments must fail before disk access: $arguments"
    }
}
Write-Host 'Developer CLI contract checks passed. No disk handle opened.'
foreach ($name in @('create', 'list', 'status', 'physical-list', 'capabilities', 'inspect', 'start', 'stop', 'flush', 'save', 'export', 'format', 'cache', 'startup', 'remove', 'delete-image', 'recover', 'configure', 'timing'))
{
    & $cli disk $name --help
    if ($LASTEXITCODE) { throw "Managed disk help failed: $name" }
}
$managedId = '00000000-0000-0000-0000-000000000123'
foreach ($arguments in @(
    @('disk', 'create', '--mode', 'ram', '--size-mib', '0'),
    @('disk', 'create', '--mode', 'ram', '--size-mib', '16', '--new-image', 'C:\Images\wrong.vhdx'),
    @('disk', 'create', '--mode', 'ram', '--size-mib', '16', '--budget-mib', '64'),
    @('disk', 'create', '--mode', 'ram', '--size-mib', '16', '--initialize-raw'),
    @('disk', 'create', '--mode', 'cached-vhdx', '--size-mib', '16', '--new-image', 'C:\Images\new.vhdx', '--initialize-raw'),
    @('disk', 'create', '--mode', 'image-in-ram', '--load', 'C:\Images\source.vhdx', '--checkpoint-directory', 'C:\Images\Checkpoints', '--read-only', '--initialize-raw'),
    @('disk', 'create', '--mode', 'cached-vhdx', '--size-mib', '16', '--new-image', 'C:\Images\new.vhdx', '--preset', 'Fast'),
    @('disk', 'create', '--mode', 'image-in-ram', '--size-mib', '16', '--new-image', 'C:\Images\new.vhdx'),
    @('disk', 'stop', $managedId, '--save', '--discard'),
    @('disk', 'format', $managedId),
    @('disk', 'delete-image', $managedId, '--path', 'C:\Images\old.vhdx'),
    @('disk', 'export', $managedId, '--path', '\\server\share\image.vhdx'),
    @('disk', 'cache', $managedId, '--budget-mib', '0'),
    @('disk', 'cache', $managedId, '--preset', 'Fast'),
    @('disk', 'startup', $managedId),
    @('disk', 'configure', $managedId),
    @('disk', 'configure', $managedId, '--size-mib', '0'),
    @('disk', 'start', $managedId, '--expected-creation', '1')
))
{
    & $cli @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 1) { throw "Managed validation must fail before broker/native access: $arguments" }
}
foreach ($arguments in @(
    @('disk', 'create', '--mode', 'unknown'),
    @('disk', 'create', '--mode', 'ram', '--size-mib', '16', '--letter', 'C'),
    @('disk', 'status', 'not-a-resource'),
    @('disk', 'configure', $managedId, '--letter', 'C'),
    @('disk', 'stop', '00000000-0000-0000-0000-000000000000'),
    @('disk', 'export', $managedId),
    @('disk', 'timing', $managedId)
))
{
    & $cli @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 2) { throw "Invalid managed syntax must fail before broker/native access: $arguments" }
}
Write-Host 'Managed disk CLI contracts passed. No broker or disk handle opened.'
$verificationHelp = & $cli developer verify --help | Out-String
if ($LASTEXITCODE -ne 0 -or $verificationHelp -notmatch 'DiskSpd64.exe' -or $verificationHelp -notmatch 'Microsoft' -or $verificationHelp -notmatch 'flush-interference')
{
    throw 'Verification help must describe suites and both DiskSpd variants.'
}
& $cli developer verify 'Q:' --suite quick --repeats 0 2>&1 | Out-Host
if ($LASTEXITCODE -ne 1)
{
    throw 'Invalid runner settings must fail before device access.'
}
if ($verificationHelp -notmatch 'run.log')
{
    throw 'Verification help must describe persistent logging.'
}
$testIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try
{
    $testElevated = ([Security.Principal.WindowsPrincipal]::new($testIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
finally
{
    $testIdentity.Dispose()
}
if (-not $testElevated)
{
    foreach ($command in @('verify', 'verify-recover'))
    {
        # The guard must reject before even resolving this deliberately invalid target.
        $guardOutput = & $cli developer $command 'not-a-target' 2>&1 | Out-String
        if ($LASTEXITCODE -ne 1 -or $guardOutput -notmatch 'Administrator access required' -or $guardOutput -notmatch 'Run as administrator')
        {
            throw "Missing elevation guidance for $command"
        }
    }
}
# GitHub's pwsh wrapper propagates the last native exit code. The negative tests
# intentionally leave it nonzero, so report this script's own successful result.
exit 0
