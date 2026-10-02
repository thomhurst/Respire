$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Merge-Pr.ps1'
$script = Get-Content -LiteralPath $scriptPath -Raw

$mergeMatch = [regex]::Match($script, '(?m)^gh pr merge .*$', 'IgnoreCase')
if (-not $mergeMatch.Success) {
    throw 'Merge-Pr.ps1 must invoke gh pr merge.'
}

if ($mergeMatch.Value -match '--delete-branch') {
    throw 'gh pr merge must not delete a branch before its worktree is removed.'
}

$primaryGuardIndex = $script.IndexOf("is not in an isolated linked worktree")
if ($primaryGuardIndex -lt 0 -or $primaryGuardIndex -gt $mergeMatch.Index) {
    throw 'Primary-checkout cleanup guard must run before gh pr merge.'
}

$cleanupIndex = $script.IndexOf('Remove-MergedWorktree', $mergeMatch.Index)
$remoteDeleteIndex = $script.IndexOf('push "--force-with-lease=${remoteRef}:$mergedHead" origin ":$remoteRef"', $mergeMatch.Index)

if ($cleanupIndex -lt 0) {
    throw 'Merged worktree cleanup is missing.'
}

if ($remoteDeleteIndex -lt 0) {
    throw 'Post-merge remote branch cleanup must compare the verified head.'
}

if ($cleanupIndex -gt $remoteDeleteIndex) {
    throw 'The worktree must be removed before deleting its remote branch.'
}

if ($script -match 'branch -[dD]|update-ref -d') {
    throw 'Merge cleanup must retain local refs that another worktree can reuse.'
}

$mergedConfirmationIndex = $script.IndexOf('Write-Host "Merged', $mergeMatch.Index)
if ($mergedConfirmationIndex -lt 0) {
    throw 'Successful merge confirmation is missing.'
}

$postMergeResolutionIndex = $script.IndexOf('# Re-resolve after the merge', $mergedConfirmationIndex)
if ($postMergeResolutionIndex -lt 0 -or $postMergeResolutionIndex -gt $cleanupIndex) {
    throw 'PR worktree must be re-resolved after the merge and before cleanup.'
}

$postMergeScript = $script.Substring($mergedConfirmationIndex)
if ($postMergeScript -match '(?m)^\s*Fail\s+') {
    throw 'Post-merge cleanup must warn rather than report that the merge was aborted.'
}

if ($script -notmatch '\$worktreeWasExplicit') {
    throw 'Merge-Pr must preserve whether -Worktree was explicitly supplied.'
}

if ($script -notmatch '\$worktreeIdentityToken') {
    throw 'Merge-Pr must retain an identity token for the validated worktree.'
}

$stalePruneIndex = $script.IndexOf('worktree prune')
$linkedGuardCallIndex = $script.IndexOf('Test-IsLinkedWorktree -Path $Worktree')
if ($stalePruneIndex -lt 0 -or $stalePruneIndex -gt $linkedGuardCallIndex) {
    throw 'Stale worktree registrations must be pruned before linked-worktree validation.'
}

Write-Output 'OK Merge-Pr command ordering and post-merge failure semantics passed.'
