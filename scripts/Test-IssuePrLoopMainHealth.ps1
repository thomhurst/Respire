#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

# Load the driver function without running its startup or contacting GitHub or Redis.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Start-IssuePrLoop.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
$definition = $ast.EndBlock.Statements | Where-Object Name -eq 'Get-MainFailures'
. ([scriptblock]::Create($definition.Extent.Text))

function gh {
    $global:LASTEXITCODE = 0
    $runs = $fixtureRuns
    foreach ($filter in @('--branch', '--event', '--workflow', '--status')) {
        $index = [Array]::IndexOf($args, $filter)
        if ($index -lt 0) { continue }
        $runs = switch ($filter) {
            '--branch' { $runs | Where-Object headBranch -eq $args[$index + 1] }
            '--event' { $runs | Where-Object event -eq $args[$index + 1] }
            '--workflow' { $runs | Where-Object workflowDatabaseId -eq $args[$index + 1] }
            '--status' { $runs | Where-Object status -eq $args[$index + 1] }
        }
    }
    $limit = [int]$args[[Array]::IndexOf($args, '--limit') + 1]
    $runs | Sort-Object createdAt -Descending | Select-Object -First $limit | ConvertTo-Json -AsArray
}

function New-Run([int]$Id, [string]$Event, [string]$Conclusion, [int]$Workflow = 1,
                 [string]$Branch = 'main', [string]$Status = 'completed') {
    [pscustomobject]@{
        databaseId = $Id
        workflowDatabaseId = $Workflow
        workflowName = 'CI'
        status = $Status
        conclusion = $Conclusion
        headBranch = $Branch
        event = $Event
        createdAt = "2026-11-23T00:00:$('{0:00}' -f $Id)Z"
    }
}

function Assert-MainFailure([string]$Case, [int[]]$Expected = @()) {
    $actual = @(Get-MainFailures | ForEach-Object databaseId)
    if (($actual -join ', ') -ne ($Expected -join ', ')) {
        throw "$Case reported run IDs [$($actual -join ', ')], expected [$($Expected -join ', ')]."
    }
}

$repoSlug = 'test/repo'
$fixtureRuns = @()
Assert-MainFailure 'No runs'

$fixtureRuns = @(
    (New-Run 10 push failure)
    (New-Run 11 workflow_dispatch success)
)
Assert-MainFailure 'A completed manual success clears an older push failure'

$fixtureRuns += New-Run 12 workflow_dispatch timed_out
Assert-MainFailure 'A newer manual failure is reported' 12

$fixtureRuns += @(
    (New-Run 13 workflow_dispatch failure -Workflow 2)
    (New-Run 14 workflow_dispatch success -Branch 'feature')
    (New-Run 15 workflow_dispatch '' -Status 'in_progress')
)
Assert-MainFailure 'Only completed manual runs for the same workflow on main count' 12

$fixtureRuns += New-Run 16 push success
Assert-MainFailure 'A newer push success supersedes an older manual failure'

$fixtureRuns += New-Run 17 workflow_dispatch failure
Assert-MainFailure 'A manual failure can supersede a successful push' 17

$fixtureRuns += New-Run 18 push '' -Status 'in_progress'
Assert-MainFailure 'A newer pending push suppresses an older failure'

$fixtureRuns += @(
    (New-Run 19 push failure -Workflow 2)
    (New-Run 20 push failure -Workflow 3)
    (New-Run 21 workflow_dispatch success -Workflow 2)
)
Assert-MainFailure 'A recovery run clears only its own workflow' 20

Write-Host 'Issue/PR loop main workflow health tests passed.'
