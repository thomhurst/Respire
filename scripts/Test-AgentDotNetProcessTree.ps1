$ErrorActionPreference = 'Stop'

# Import only the snapshot helpers, without starting a guard or stopping any
# real process. Fixtures model Windows ticks, Linux clock ticks and macOS
# seconds:microseconds, including PID reuse that is unsafe to force on a host.
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Guard script did not parse.' }
foreach ($name in @('Test-ProcessStartOrder', 'Get-ProcessTreeState', 'Sync-TrackedProcesses', 'Get-TrackedWorkingSetBytes')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if ($null -ne $definition) { . ([scriptblock]::Create($definition.Extent.Text)) }
}

function New-ProcessRecord([int]$Id, [int]$ParentId, [string]$Identity) {
    [pscustomobject]@{ ProcessId = $Id; ParentProcessId = $ParentId; StartIdentity = $Identity; WorkingSetBytes = 100L }
}
function Get-ProcessSnapshot { $script:fixture }
function Assert-Tree([string]$RootIdentity, [int[]]$Expected) {
    $state = Get-ProcessTreeState -RootProcessId 10 -RootStartIdentity $RootIdentity
    $actual = @($state.Processes.ProcessId | Sort-Object)
    if (($actual -join ',') -ne (($Expected | Sort-Object) -join ',')) {
        throw "Unsafe process ancestry: expected $($Expected -join ','); got $($actual -join ',')."
    }
    return $state
}

foreach ($identities in @(
    @('638966280000000000', '638966280000000010', '638966279999999990'),
    @('100', '110', '90'),
    @('100:20', '100:100', '100:9')
)) {
    $rootIdentity, $childIdentity, $oldIdentity = $identities
    # Unordered snapshots must still find legitimate descendants. An older
    # orphan retains a parent PID that now belongs to this wrapper; even its
    # younger children are outside the guarded tree.
    $script:fixture = @(
        (New-ProcessRecord 13 12 $childIdentity),
        (New-ProcessRecord 22 21 $childIdentity),
        (New-ProcessRecord 24 23 $childIdentity),
        (New-ProcessRecord 23 12 $rootIdentity),
        (New-ProcessRecord 12 10 $childIdentity),
        (New-ProcessRecord 21 10 $oldIdentity),
        (New-ProcessRecord 10 1 $rootIdentity)
    )
    $state = Assert-Tree $rootIdentity @(10, 12, 13)
    $tracked = [Collections.Generic.Dictionary[int, string]]::new()
    Sync-TrackedProcesses $tracked @($state.Snapshot) @($state.Processes)
    if ((Get-TrackedWorkingSetBytes $tracked @($state.Snapshot)) -ne 300) {
        throw 'An unrelated process affected the memory budget.'
    }

    # A missing/recycled root must not seed a new tree. Previously verified
    # descendants stay tracked for containment fallback after the root exits.
    $script:fixture = @($script:fixture | Where-Object ProcessId -ne 10)
    $state = Assert-Tree $rootIdentity @()
    Sync-TrackedProcesses $tracked @($state.Snapshot) @($state.Processes)
    if (($tracked.Keys | Sort-Object) -join ',' -ne '12,13') {
        throw 'Root exit lost already verified descendants.'
    }
    $script:fixture += New-ProcessRecord 10 1 $childIdentity
    $null = Assert-Tree $rootIdentity @()
    $null = Assert-Tree '' @()
}

# Timestamp resolution can give a real parent and child the same start time.
$script:fixture = @((New-ProcessRecord 10 1 '100'), (New-ProcessRecord 12 10 '100'))
$null = Assert-Tree '100' @(10, 12)
foreach ($invalid in @('', 'invalid', '100:1:2', '9223372036854775808')) {
    if (Test-ProcessStartOrder $invalid '100') { throw 'Accepted an invalid parent creation identity.' }
    if (Test-ProcessStartOrder '100' $invalid) { throw 'Accepted an invalid child creation identity.' }
}
if (Test-ProcessStartOrder '100' '100:1') { throw 'Accepted mismatched identity formats.' }
Write-Output 'OK process ancestry rejects recycled parent/root PIDs and preserves descendants and memory accounting.'
