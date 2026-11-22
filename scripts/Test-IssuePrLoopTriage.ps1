#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

# Load the driver's triage functions without starting the queue, GitHub, or Redis.
$errors = $null
$driver = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Start-IssuePrLoop.ps1'), [ref]$null, [ref]$errors)
if ($errors.Count) { throw ($errors -join "`n") }
foreach ($functionName in 'Get-TriageItem', 'Write-TriageResult') {
    $definition = $driver.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $false)
    if (-not $definition) { throw "Could not find the driver function $functionName." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Invoke-Locks([string[]]$Arguments) {
    $name = $Arguments[([array]::IndexOf($Arguments, '-LockName') + 1)]
    switch ($Arguments[0]) {
        'defer' {
            $seconds = [int]$Arguments[([array]::IndexOf($Arguments, '-DeferSeconds') + 1)]
            $writes.Add("${name}=${seconds}")
            $expires[$name] = $now + $seconds
        }
        'deferred' {
            $active = foreach ($key in $name -split ',') {
                if ($expires[$key] -gt $now) { "$key=recorded" }
            }
            return [pscustomobject]@{ ExitCode = 0; Output = $active -join "`n" }
        }
        'acquire' {
            if ($name -ne 'triage') { throw "Unexpected lock: $name" }
            return [pscustomobject]@{ ExitCode = 0 }
        }
        default { throw "Unexpected lock command: $Arguments" }
    }
}

function gh { '[]' }
function Get-QueueableCount { 0 }
function Test-HasLabel { $false }

function Assert-TriageWrites([string]$Result, [string[]]$Expected) {
    $writes.Clear()
    $expires.Clear()
    Write-TriageResult $batch $Result
    if (($writes -join ', ') -ne ($Expected -join ', ')) {
        throw "For '${Result}', expected lock records [$($Expected -join ', ')], got [$($writes -join ', ')]."
    }
}

$writes = [Collections.Generic.List[string]]::new()
$expires = @{}
$now = 1
$batchRetries = @('triage-retry-12=60', 'triage-retry-15=60', 'triage-retry-18=60', 'triage-retry-19=60')
$ParkedAuditHours = 24
$AuditDays = 3
$batch = @(
    [pscustomobject]@{ Number = 12; Kind = 'pr'; Order = 0 }
    [pscustomobject]@{ Number = 15; Kind = 'issue'; Order = 0 }
    [pscustomobject]@{ Number = 18; Kind = 'issue'; Order = 3 }
    [pscustomobject]@{ Number = 19; Kind = 'issue'; Order = 3 }
)

# Audited items can be unchanged. Unlisted parked and ordinary items remain due.
Assert-TriageWrites 'triaged: no changes; audited: #12, #18' @('audit-12=86400', 'audit-18=259200')
Assert-TriageWrites 'triaged: all items skipped; audited: none' $batchRetries
# A summary can mention related items. Only the audited list counts, within this batch.
Assert-TriageWrites 'triaged: closed #15, filed #40; audited: #15, #40, #15' @('audit-15=86400')
Assert-TriageWrites 'triaged: no changes' $batchRetries
Assert-TriageWrites 'triaged: no changes; audited: #12, invalid, #18' $batchRetries
Assert-TriageWrites 'failed #12: audit interrupted; audited: #12' $batchRetries
Assert-TriageWrites 'triaged: no changes; audited: #40' $batchRetries
Assert-TriageWrites '' @()

# A fully skipped batch retries after a short delay without holding up other due items.
$TriageBatch = 1
$script:lastIssues = foreach ($number in 18, 19) {
    [pscustomobject]@{ number = $number }
}
$first = Get-TriageItem
if ($first.Items.Number -ne 18) { throw 'Expected issue 18 in the first batch.' }
Write-TriageResult $first.Items 'triaged: all items skipped; audited: none'
$next = Get-TriageItem
if ($next.Items.Number -ne 19) { throw 'Expected issue 19 while issue 18 is cooling down.' }
Write-TriageResult $next.Items 'triaged: all items skipped; audited: none'
if (Get-TriageItem) { throw 'Expected no triage while all due issues are cooling down.' }
$now += 60
$retried = Get-TriageItem
if ($retried.Items.Number -ne 18) { throw 'Expected skipped issue 18 to be due after the retry delay.' }

Write-Host 'OK triage records completed audits and retries skipped batches after a delay.'
