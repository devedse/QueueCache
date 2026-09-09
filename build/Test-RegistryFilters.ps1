#Requires -Version 5.1
# Exercise the actual installer verifier against one disposable HKCU key.
# Never import/run the installer or touch services, disks, or HKLM.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    "$PSScriptRoot/../packaging/Install-Driver.ps1", [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definitions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Assert-RegistryMultiString','Get-QueueCacheClassFilters')
}, $false))
if ($definitions.Count -ne 2) { throw 'Expected installer verifier and filter-order function.' }
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }

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
        @{ Before=@('partmgr'); After=@('qcachelab','partmgr') },
        @{ Before=@('partmgr','qcachelab'); After=@('qcachelab','partmgr') },
        @{ Before=@('qcachelab','partmgr'); After=@('qcachelab','partmgr') },
        @{ Before=@('other','partmgr','third','QCACHELAB','qcachelab'); After=@('other','qcachelab','partmgr','third') },
        @{ Before=@('PARTMGR','third'); After=@('qcachelab','PARTMGR','third') }
    )) {
        [string[]]$ordered = Get-QueueCacheClassFilters $case.Before
        if (($ordered -join '|') -cne ($case.After -join '|')) { throw 'Incorrect class-filter ordering.' }
        $testKey.SetValue('UpperFilters', $ordered, [Microsoft.Win32.RegistryValueKind]::MultiString)
        Assert-RegistryMultiString $testPath $case.After
    }
    Expect-Rejection { Get-QueueCacheClassFilters @() }
    Expect-Rejection { Get-QueueCacheClassFilters @('other','qcachelab') }
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
