#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

# Load the driver functions without running its startup or contacting GitHub or Redis.
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Start-IssuePrLoop.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join "`n") }
foreach ($definition in $ast.EndBlock.Statements) {
    if ($definition -is [Management.Automation.Language.FunctionDefinitionAst]) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
}

function gh {
    $global:LASTEXITCODE = 0
    return $fixturePrs
}
function Get-MainCandidate {}
function Get-PrActivity { @{} }
function Get-OpenIssues { $fixtureIssues }
function Get-StrandedBranches { @{} }
function Invoke-Locks { [pscustomobject]@{ ExitCode = 0; Output = '' } }

function New-Issue([int]$Number, [string]$Title = "Issue $Number") {
    [pscustomobject]@{
        number = $Number
        title = $Title
        author = @{ __typename = 'User' }
        labels = @{ nodes = @() }
        assignees = @{ nodes = @() }
        issueDependenciesSummary = @{ blockedBy = 0; blocking = 0 }
        subIssuesSummary = @{ total = 0; completed = 0 }
        subIssues = @{ nodes = @() }
        closedByPullRequestsReferences = @{ nodes = @() }
        comments = @{ nodes = @(); totalCount = 0 }
    }
}

function Assert-QueueCount([string]$Case, [int]$Expected, [int]$ExpectedCandidates = $Expected) {
    Reset-SurveyStats
    $freshCandidates = @(Get-Candidates | Where-Object Rank -eq 6)
    if ($freshCandidates.Count -ne $ExpectedCandidates) {
        throw "$Case selected $($freshCandidates.Count) fresh issues, expected $ExpectedCandidates."
    }
    foreach ($item in @((Get-TriageItem), (Get-DiscoveryItem))) {
        if ((Get-Prompt $item) -notlike "*The queue has $Expected issue(s) that a unit can claim now.*") {
            throw "$Case reported $($item.Queueable) claimable issues to $($item.Kind), expected $Expected."
        }
    }
}

$repoSlug = 'test/repo'
$skill = 'SKILL.md'
$ownerVariable = 'TEST_AGENT_LOCK_OWNER_ID'
$prFields = 'number'
$excludedIssueLabels = @('wontfix', 'duplicate', 'question', 'needs-human', 'on-hold')
$excludedPrLabels = $parkedLabels = @('needs-human', 'on-hold')
$surveyStats = @{}
$script:promoted = [Collections.Generic.HashSet[int]]::new()
$TriageBatch = 8
$DiscoveryHours = 6
$MaxOpenPrs = 15
$fixturePrs = '[]'

$dashboard = New-Issue 29 'Dependency Dashboard'
$fixtureIssues = @(1..4 | ForEach-Object { New-Issue $_ }) + $dashboard
Assert-QueueCount 'Four eligible issues and a dashboard' 4

$fixtureIssues += New-Issue 5
Assert-QueueCount 'Five eligible issues and a dashboard' 5

$bot = New-Issue 30
$bot.author.__typename = 'Bot'
$assigned = New-Issue 31
$assigned.assignees.nodes = @(@{ login = 'someone' })
$parent = New-Issue 32
$parent.subIssuesSummary.total = 1
$parent.subIssues.nodes = @(@{ number = 1; state = 'OPEN' })
$blocked = New-Issue 33
$blocked.issueDependenciesSummary.blockedBy = 1
$claimed = New-Issue 34
$claimed.labels.nodes = @(@{ name = 'in-progress' })
$hasPr = New-Issue 35
$hasPr.closedByPullRequestsReferences.nodes = @(@{ state = 'OPEN' })
$fixtureIssues += @($bot, $assigned, $parent, $blocked, $claimed, $hasPr)
Assert-QueueCount 'Five eligible issues among excluded issues' 5

$MaxOpenPrs = 1
$fixturePrs = '[{"number":99,"isDraft":true,"labels":[],"statusCheckRollup":[]}]'
Assert-QueueCount 'Open PR limit reached' 5 0

$MaxOpenPrs = 15
$fixtureIssues = @()
$fixturePrs = '[{"number":99,"isDraft":true,"labels":[{"name":"on-hold"}],"statusCheckRollup":[]}]'

$script:lastIssues = $null
if ($null -ne (Get-TriageItem) -or $null -ne (Get-DiscoveryItem)) {
    throw 'An uninitialized survey scheduled triage or discovery.'
}

Reset-SurveyStats
$null = Get-Candidates
$triage = Get-TriageItem
if ($triage.Kind -ne 'triage' -or $triage.Queueable -ne 0 -or $triage.Items.Count -ne 1 -or
    $triage.Items[0].Kind -ne 'pr' -or $triage.Items[0].Number -ne 99) {
    throw 'A completed survey with no issues should schedule an audit of the parked PR.'
}

$fixturePrs = '[]'
Reset-SurveyStats
$null = Get-Candidates
if ($null -ne (Get-TriageItem)) {
    throw 'A completed survey with no issues or parked PRs scheduled triage.'
}

$discovery = Get-DiscoveryItem
if ($discovery.Kind -ne 'discovery' -or $discovery.Queueable -ne 0) {
    throw 'A completed survey with no issues or due audits should schedule discovery.'
}

Write-Host 'Issue/PR loop queue count and scheduling tests passed.'
