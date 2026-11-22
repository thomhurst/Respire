#!/usr/bin/env pwsh
# Local checkout lifecycle. Keep branches; retain detached commits before removal.
# Called while AgentLocks still owns the lock, before another worker can acquire it.
function Remove-ReleasedWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$LockName
    )

    if (-not (Test-Path -LiteralPath $Worktree)) { return }
    . (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
    $target = (Resolve-Path -LiteralPath $Worktree).Path
    $common = git rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $common) { throw 'Cannot identify the calling repository.' }
    $targetCommon = git -C $target rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $targetCommon -or -not (Test-SameNativePath -Left $targetCommon -Right $common)) {
        throw "Worktree belongs to another repository: $target"
    }
    $top = git -C $target rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $top -or -not (Test-SameNativePath -Left $top -Right $target)) {
        throw "Not a worktree root: $target"
    }
    $gitDirectory = git -C $target rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory -or (Test-SameNativePath -Left $gitDirectory -Right $common) -or
        (Test-Path -LiteralPath (Join-Path $gitDirectory 'locked'))) {
        Write-Host "Preserving main or Git-locked worktree: $target"
        return
    }
    # A marker copied in by `git worktree add` names another checkout and does not count.
    $marker = Get-WorktreeLockMarker -Worktree $target
    if ($marker -cne $LockName) {
        Write-Host "Preserving worktree without a matching lock marker: $target"
        return
    }
    $entries = @(git worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list registered worktrees.' }
    $paths = @($entries | Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { $_.Substring(9) })
    $registered = $paths | Where-Object { Test-SameNativePath -Left $_ -Right $target } | Select-Object -First 1
    if (-not $registered) { throw "Unregistered worktree: $target" }
    $target = $registered
    $repo = $paths[0]
    if ((Test-SameNativePath -Left $target -Right $repo) -or (Test-IsDescendantPath -Path $target -Parent $repo)) {
        Write-Host "Preserving main or harness-managed worktree: $target"
        return
    }

    $status = @(git -C $target status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect worktree: $target" }
    $work = @(Get-WorktreeUncommittedWork -Status $status)
    if ($work.Count -gt 0) {
        Write-Host "Preserving dirty worktree: $target ($($work[0]))"
        return
    }
    $pendingBranch = Get-DeferredRemoteBranchCleanup -GitDirectory $gitDirectory
    # A stable ref keeps unpublished detached commits reachable after Git prunes the
    # worktree reflog.
    Save-DetachedWorktreeHead -Repo $repo -Worktree $target
    # Move this child shell out of the directory before deleting it on Windows. A parent
    # shell started inside the worktree still blocks deletion; the sweep finishes it.
    Set-Location -LiteralPath $repo
    [Environment]::CurrentDirectory = $repo
    if (-not (Remove-WorktreeDirectoryLinks -Worktree $target)) {
        Write-Host "Preserving released worktree with directory links that could not be detached: $target"
        return
    }
    $pending = Register-PendingWorktreeRemoval -Repo $repo -Worktree $target -GitDirectory $gitDirectory `
        -LockNames @($LockName) -PendingBranch $pendingBranch
    git -C $repo -c core.longpaths=true worktree remove --force -- $target
    if ($LASTEXITCODE -ne 0) {
        if (-not $pending) { throw "Could not remove released worktree: $target" }
        Write-Host "Released worktree removal is pending: $target is in use. Remove-MergedWorktrees.ps1 finishes it; release from the shared checkout to avoid this."
        return
    }
    if ($pending) { Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue }
    Write-Host "Removed released worktree: $target"
    if ($pendingBranch) {
        . (Join-Path $PSScriptRoot 'MergedBranchCleanup.ps1')
        Remove-MergedRemoteBranch -Repo $repo -Branch $pendingBranch.Branch `
            -ExpectedHead $pendingBranch.ExpectedHead -RemoteUrlHash $pendingBranch.RemoteUrlHash
    }
}
