#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

# Load the driver's audit writer without starting the queue, GitHub, or Redis.
$errors = $null
$driver = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Start-IssuePrLoop.ps1'), [ref]$null, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
$writer = $driver.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-TriageAudits'
}, $false)
if (-not $writer) { throw 'Could not find the driver audit writer.' }
. ([scriptblock]::Create($writer.Extent.Text))

function Invoke-Locks([string[]]$Arguments) {
    if ($Arguments[0] -ne 'defer') { throw "Unexpected lock command: $Arguments" }
    $name = $Arguments[([array]::IndexOf($Arguments, '-LockName') + 1)]
    $seconds = $Arguments[([array]::IndexOf($Arguments, '-DeferSeconds') + 1)]
    $writes.Add("${name}=${seconds}")
}

function Assert-AuditWrites([string]$Result, [string[]]$Expected) {
    $writes.Clear()
    Write-TriageAudits $batch $Result
    if (($writes -join ', ') -ne ($Expected -join ', ')) {
        throw "For '${Result}', expected audit writes [$($Expected -join ', ')], got [$($writes -join ', ')]."
    }
}

$writes = [Collections.Generic.List[string]]::new()
$ParkedAuditHours = 24
$AuditDays = 3
$batch = @(
    [pscustomobject]@{ Number = 12; Kind = 'pr'; Order = 0 }
    [pscustomobject]@{ Number = 15; Kind = 'issue'; Order = 0 }
    [pscustomobject]@{ Number = 18; Kind = 'issue'; Order = 3 }
    [pscustomobject]@{ Number = 19; Kind = 'issue'; Order = 3 }
)

# Audited items can be unchanged. Unlisted parked and ordinary items remain due.
Assert-AuditWrites 'triaged: no changes; audited: #12, #18' @('audit-12=86400', 'audit-18=259200')
Assert-AuditWrites 'triaged: all items skipped; audited: none' @()
# A summary can mention related items. Only the audited list counts, within this batch.
Assert-AuditWrites 'triaged: closed #15, filed #40; audited: #15, #40, #15' @('audit-15=86400')
Assert-AuditWrites 'triaged: no changes' @()
Assert-AuditWrites 'triaged: no changes; audited: #12, invalid, #18' @()
Assert-AuditWrites 'failed #12: audit interrupted; audited: #12' @()
Assert-AuditWrites '' @()

Write-Host 'OK triage records audits only for completed batch items.'
