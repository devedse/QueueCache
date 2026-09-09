#Requires -Version 5.1
# Exercise the actual installer verifier against one disposable HKCU key.
# Never import/run the installer or touch services, disks, or HKLM.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    "$PSScriptRoot/../packaging/Install-Driver.ps1", [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definitions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-RegistryMultiString'
}, $false))
if ($definitions.Count -ne 1) { throw 'Expected exactly one installer verifier.' }
. ([scriptblock]::Create($definitions[0].Extent.Text))

$testName = 'Software\QueueCache-RegistryTest-' + [guid]::NewGuid().ToString('N')
$testPath = 'HKCU:\' + $testName
$testKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testName)
function Expect-Rejection([scriptblock]$Check) {
    $rejected = $false
    try { & $Check } catch { $rejected = $true }
    if (-not $rejected) { throw 'Verifier accepted an invalid registry value.' }
}
try {
    foreach ($case in @(
        @{ Values = [string[]]@() },
        @{ Values = [string[]]@('qcachelab') },
        @{ Values = [string[]]@('partmgr','qcachelab') },
        @{ Values = [string[]]@('other-filter','partmgr','qcachelab') }
    )) {
        $testKey.SetValue('UpperFilters', $case.Values, [Microsoft.Win32.RegistryValueKind]::MultiString)
        Assert-RegistryMultiString $testPath $case.Values
    }
    $testKey.SetValue('UpperFilters', [string[]]@('partmgr','qcachelab'), [Microsoft.Win32.RegistryValueKind]::MultiString)
    Assert-RegistryMultiString $testPath @('PARTMGR','QCACHELAB')
    Expect-Rejection { Assert-RegistryMultiString $testPath @('qcachelab','partmgr') }
    Expect-Rejection { Assert-RegistryMultiString $testPath @('partmgr') }
    Expect-Rejection { Assert-RegistryMultiString $testPath @('partmgr','different') }
    # Regression: correct two-entry value must pass even if the old pipeline
    # comparison stringifies its one nested String[] element.
    $oldRead = @(Get-ItemPropertyValue $testPath UpperFilters) -join '|'
    Write-Host "Old expression returned: $oldRead; corrected verifier passed."
    $testKey.SetValue('UpperFilters', 'qcachelab', [Microsoft.Win32.RegistryValueKind]::String)
    Expect-Rejection { Assert-RegistryMultiString $testPath @('qcachelab') }
    $testKey.DeleteValue('UpperFilters')
    Expect-Rejection { Assert-RegistryMultiString $testPath @('qcachelab') }
    Write-Host "Registry verifier tests passed on PowerShell $($PSVersionTable.PSVersion). No disk registration changed."
} finally {
    $testKey.Dispose()
    # Exact GUID-named key created above, non-recursive deletion only.
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($testName)
}
