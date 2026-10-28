# Enable-AutoMerge.ps1
# Arms GitHub native auto-merge (squash) for PR #<N>, pinned to the reviewed head.
# This is the issue-pr-loop's way to merge a PR that was just marked ready.
#
#   1. captures the PR head
#   2. runs  Assert-PrGreen.ps1 -ForAutoMerge -ExpectedHead <head>  and stops on any
#      denial: a draft, conflicts, a failed check, a `claude-review` check that is
#      missing or still running, an unresolved thread, or a current-head Claude review
#      that is neither CLEAR nor disposed
#   3. arms  gh pr merge --auto --squash --match-head-commit <head>
#
# GitHub then merges once the `Main` ruleset's required `CI Passed` check passes. That
# check is the ci.yml aggregator: it needs every ci.yml job and waits for every other
# pull_request workflow run on the head, so a failure anywhere keeps the PR open.
# A later push to the PR disarms auto-merge (the `disarm-auto-merge` job in
# claude-code-review.yml), so each new head needs its own review and a new call here.
#
# The caller must read and address every review body, inline finding, and issue
# comment BEFORE running this; the gate cannot judge review content on its own.
# Never call `gh pr merge --auto` directly.
#
# Usage: pwsh scripts/Enable-AutoMerge.ps1 -Pr 1234 [-Repo owner/name]
# Exit:  0 = armed; 1 = not armed (reason printed to stderr).

[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [string]$Repo
)

$ErrorActionPreference = 'Stop'

function Fail([string]$reason) {
    [Console]::Error.WriteLine("NOT ARMED #${Pr} -- $reason")
    exit 1
}

$repoArgs = @()
if ($Repo) { $repoArgs = @('--repo', $Repo) }

# Capture the head before the gate. The gate rejects a head that moved before it ran,
# and --match-head-commit makes GitHub refuse a head that moved after it.
$head = gh pr view $Pr @repoArgs --json headRefOid --jq '.headRefOid' 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) { Fail "could not read PR head (exit $LASTEXITCODE)" }
$head = ([string]$head).Trim()
if ($head -notmatch '^[0-9a-fA-F]{40}$') { Fail "unexpected PR head '$head'" }

$gateArgs = @('-Pr', $Pr, '-ForAutoMerge', '-ExpectedHead', $head) + $(if ($Repo) { @('-Repo', $Repo) } else { @() })
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Assert-PrGreen.ps1') @gateArgs
if ($LASTEXITCODE -ne 0) { Fail 'auto-merge gate denied (see above)' }

gh pr merge $Pr @repoArgs --auto --squash --match-head-commit $head
if ($LASTEXITCODE -ne 0) { Fail "gh pr merge --auto failed (exit $LASTEXITCODE)" }

Write-Host "Armed auto-merge for #${Pr} at $head."
exit 0
