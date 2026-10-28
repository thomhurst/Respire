# Assert-PrGreen.ps1
# Fail-closed merge gate for the issue-pr-loop skill.
#
# Exits 0 ONLY when PR #<N> is provably safe to merge:
#   - state OPEN and not a draft
#   - mergeable == MERGEABLE
#   - mergeStateStatus == CLEAN
#   - every status check is terminal AND passing (CheckRun: status=COMPLETED and
#     conclusion in SUCCESS/SKIPPED/NEUTRAL; StatusContext: state=SUCCESS)
#   - no current-head top-level review has actionable finding headings
#   - no unresolved review threads
#   - the latest Claude review comment is CLEAR, or a maintainer replied after
#     it with a comment marked <!-- REVIEW_DISPOSITION -->
#
# Any other state — pending/queued/in-progress checks, an empty rollup, a failed
# check, an unresolved thread, or an API hiccup — exits 1 so the caller MUST NOT
# merge. The default on uncertainty is DENY.
#
# Why this exists: GitHub branch protection enforces required status checks.
# This script supplements those rules with validation of every reported check
# and review dispositions. The merge wrapper calls this fail-closed gate
# immediately before `gh pr merge`, and only merges if it exits 0.
#
# -ForAutoMerge is the gate for arming native auto-merge (Enable-AutoMerge.ps1).
# GitHub then waits for the required `CI Passed` check itself, so CI may still be
# queued or running, and mergeStateStatus may be BLOCKED by that pending check.
# Everything else still applies: no check has failed, the `claude-review` check for
# the current head has completed and passed (so its review exists and was read),
# no unresolved threads, and the current-head Claude review is CLEAR or disposed.
#
# Usage: pwsh scripts/Assert-PrGreen.ps1 -Pr 1234 [-Repo owner/name] [-ForAutoMerge]
# Exit:  0 = safe to merge (or to arm auto-merge); 1 = DO NOT MERGE (reason printed to stderr).

[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [string]$Repo,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedHead,
    [switch]$ForAutoMerge
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AssertPrGreenReviewHeuristics.ps1')

function Deny([string]$reason) {
    [Console]::Error.WriteLine("DO NOT MERGE #${Pr} -- $reason")
    exit 1
}

$repoArgs = @()
if ($Repo) { $repoArgs = @('--repo', $Repo) }

# Re-fetch fresh — survey output goes stale within seconds.
$raw = gh pr view $Pr @repoArgs --json number,state,isDraft,mergeable,mergeStateStatus,statusCheckRollup,latestReviews,commits,headRefOid,author 2>$null
if ($LASTEXITCODE -ne 0) { Deny "gh pr view failed (exit $LASTEXITCODE)" }
try {
    $view = $raw | ConvertFrom-Json
}
catch {
    Deny "could not parse PR state: $($_.Exception.Message)"
}

if ($view.state -ne 'OPEN') { Deny "state=$($view.state) (need OPEN)" }
if ($view.isDraft -ne $false) { Deny 'PR is a draft; mark it ready only after its reviewers approve the current head' }
if ($ExpectedHead -and $view.headRefOid -ne $ExpectedHead) { Deny 'PR head changed before gate validation' }
if ($view.mergeable -ne 'MERGEABLE') { Deny "mergeable=$($view.mergeable) (need MERGEABLE)" }
# Arming waits for required checks, so BLOCKED/UNSTABLE are expected then; failures are caught below.
$allowedMergeStates = if ($ForAutoMerge) { @('CLEAN', 'BLOCKED', 'UNSTABLE', 'HAS_HOOKS', 'BEHIND') } else { @('CLEAN') }
if ($allowedMergeStates -notcontains $view.mergeStateStatus) {
    Deny "mergeStateStatus=$($view.mergeStateStatus) (need $($allowedMergeStates -join '/'))"
}

$checks = @($view.statusCheckRollup | Where-Object { $null -ne $_ })
if ($checks.Count -eq 0) {
    Deny "statusCheckRollup is empty -- no checks registered yet; refusing to merge blind"
}

$allowedConclusions = @('SUCCESS', 'SKIPPED', 'NEUTRAL')
$reviewCheck = 'claude-review'
if ($ForAutoMerge -and -not ($checks | Where-Object { $_.name -eq $reviewCheck })) {
    Deny "no $reviewCheck check on the current head yet; wait for the review before arming auto-merge"
}
$bad = @()
foreach ($c in $checks) {
    if ($null -ne $c.status) {
        # CheckRun: must be terminal AND passing. Auto-merge waits for pending CI
        # itself, but never for the review, whose findings must be read first.
        if ($c.status -ne 'COMPLETED') {
            if ($ForAutoMerge -and $c.name -ne $reviewCheck) { continue }
            $bad += "$($c.name): status=$($c.status)"
            continue
        }
        if ($allowedConclusions -notcontains $c.conclusion) {
            $bad += "$($c.name): conclusion=$($c.conclusion)"
        }
    }
    elseif ($null -ne $c.state) {
        # StatusContext: state must be SUCCESS (or still pending when arming).
        if ($c.state -ne 'SUCCESS') {
            if ($ForAutoMerge -and $c.state -in @('PENDING', 'EXPECTED')) { continue }
            $bad += "$($c.context): state=$($c.state)"
        }
    }
    else {
        $bad += "unrecognized check entry: $($c | ConvertTo-Json -Compress)"
    }
}
if ($bad.Count -gt 0) {
    Deny ("checks not green: " + ($bad -join '; '))
}

$headCommit = @($view.commits | Where-Object { $null -ne $_ }) | Select-Object -Last 1
$headCommittedAt = if ($headCommit) { ConvertTo-UtcDateTimeOffset $headCommit.committedDate } else { $null }

# Top-level review comments are not review threads, so GitHub does not expose a
# resolved flag for them. Fail closed on common review-finding shapes instead
# of treating a COMMENTED review as automatically safe. A stale Claude review
# from before the current head commit is ignored only when the current-head
# claude-review check completed green.
$latestReviews = @($view.latestReviews | Where-Object { $null -ne $_ })
$requestedChanges = @($latestReviews | Where-Object { $_.state -eq 'CHANGES_REQUESTED' })
if ($requestedChanges.Count -gt 0) {
    $authors = ($requestedChanges | ForEach-Object { $_.author.login }) -join ', '
    Deny "latest review requests changes from: $authors"
}

$actionableReviews = @()
foreach ($review in $latestReviews) {
    if ($review.state -ne 'COMMENTED') { continue }
    if (Test-StaleBotReviewCanBeIgnored -Review $review -HeadCommitCommittedAt $headCommittedAt -Checks $checks) {
        continue
    }

    $body = [string]$review.body
    $reason = Get-ActionableReviewBodyReason -Body $body
    if ($reason) {
        $actionableReviews += "$($review.author.login): $reason"
    }
}
if ($actionableReviews.Count -gt 0) {
    Deny ("latest top-level review comment has actionable finding(s): " + ($actionableReviews -join '; '))
}

# Unresolved review threads block merge even when a review's state is COMMENTED.
if ($Repo) {
    $owner, $name = $Repo -split '/', 2
}
else {
    $nwo = gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $nwo) { Deny "could not resolve repo for review-thread check" }
    $owner, $name = $nwo.Trim() -split '/', 2
}

$query = 'query($owner:String!,$repo:String!,$pr:Int!,$after:String){repository(owner:$owner,name:$repo){pullRequest(number:$pr){reviewThreads(first:100,after:$after){pageInfo{hasNextPage endCursor} nodes{isResolved}}}}}'
$unresolved = 0
$after = $null
$seenCursors = @{}
do {
    $graphqlArgs = @(
        'api',
        'graphql',
        '-f', "query=$query",
        '-F', "owner=$owner",
        '-F', "repo=$name",
        '-F', "pr=$Pr"
    )
    if ($after) { $graphqlArgs += @('-f', "after=$after") }

    $threadsRaw = & gh @graphqlArgs 2>$null
    if ($LASTEXITCODE -ne 0) { Deny "could not fetch review threads (exit $LASTEXITCODE)" }
    try {
        $reviewThreads = ($threadsRaw | ConvertFrom-Json).data.repository.pullRequest.reviewThreads
    }
    catch {
        Deny "could not parse review threads: $($_.Exception.Message)"
    }
    if ($null -eq $reviewThreads) { Deny "review-thread response did not contain the requested PR" }

    $unresolved += @($reviewThreads.nodes | Where-Object { -not $_.isResolved }).Count
    if ($reviewThreads.pageInfo.hasNextPage) {
        $endCursor = [string]$reviewThreads.pageInfo.endCursor
        if (-not $endCursor) { Deny "review-thread response has another page but no cursor" }
        if ($seenCursors.ContainsKey($endCursor)) { Deny "review-thread pagination repeated cursor '$endCursor'" }
        $seenCursors[$endCursor] = $true
        $after = $endCursor
    }
    else {
        $after = $null
    }
}
while ($after)

if ($unresolved -gt 0) { Deny "$unresolved unresolved review thread(s)" }

# Claude Code Review posts as a PR issue comment, so it never shows up in
# latestReviews. Its latest review must either carry a CLEAR verdict or have a
# later maintainer reply, marked <!-- REVIEW_DISPOSITION -->, recording how
# each finding was handled.
$commentLines = gh api "repos/$owner/$name/issues/$Pr/comments" --paginate --jq '.[] | {login: .user.login, createdAt: .created_at, body: .body}' 2>$null
if ($LASTEXITCODE -ne 0) { Deny "could not fetch PR comments (exit $LASTEXITCODE)" }
try {
    $issueComments = @($commentLines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
catch {
    Deny "could not parse PR comments: $($_.Exception.Message)"
}

# Only a disposition reply from someone with write access can answer a review.
# Look up permissions just for the logins that posted a marked reply.
$authorizedLogins = @()
$candidateLogins = @(
    $issueComments |
        Where-Object { ([string]$_.body).Contains('<!-- REVIEW_DISPOSITION -->') } |
        ForEach-Object { Get-CommentAuthorLogin $_ } |
        Where-Object { -not (Test-IsBotLogin $_) } |
        Sort-Object -Unique
)
foreach ($login in $candidateLogins) {
    $permission = gh api "repos/$owner/$name/collaborators/$login/permission" --jq '.permission' 2>$null
    if ($LASTEXITCODE -eq 0 -and ([string]$permission).Trim() -in @('admin', 'maintain', 'write')) {
        $authorizedLogins += $login
    }
}

$requiresClaude = Test-ClaudeReviewRequired -Checks $checks -AuthorLogin $view.author.login
$skippedClaude = Test-ClaudeReviewSkipped -Checks $checks -AuthorLogin $view.author.login
$claudeReviewReason = Get-UnansweredClaudeReviewReason -Comments $issueComments -AuthorizedLogins $authorizedLogins `
    -HeadSha $view.headRefOid -RequireReview:$requiresClaude -ReviewSkipped:$skippedClaude
if ($claudeReviewReason) { Deny $claudeReviewReason }

if ($ForAutoMerge) {
    Write-Host "OK #${Pr} -- MERGEABLE, no failed checks, $reviewCheck complete, no unresolved threads, Claude review answered. Safe to arm auto-merge."
}
else {
    Write-Host "OK #${Pr} -- MERGEABLE, CLEAN, $($checks.Count) check(s) green, no unresolved threads, Claude review answered. Safe to merge."
}
exit 0
