# Remove-MergedWorktrees.ps1
# Safety-net sweep: removes merged worktrees that owner release did not remove, for
# example after a crash or an expired lock TTL. Owner release stays the normal path.
#
# SQUASH-SAFE DETECTION:
#   - Exact merged PR head SHA (including renamed or detached checkouts).
#   - Detached/canonical merged-PR snapshots already reachable from origin/main.
#   - Exact head SHA from a merged commit-to-PR association outside the bulk window.
# A branch/path name, old commit date, or deleted upstream never proves that the
# current tip is disposable. Unpublished follow-up commits must survive.
#
# REMOVAL (Remove-MergedWorktree in WorktreeCleanup.ps1):
#   Every Redis identity that can own the checkout (its lock marker, pr-<N>/issue-<N> in
#   its directory or branch name, and pr-<N> when its named branch is that merged PR's own
#   head branch) is ACQUIRED and held through deletion and branch cleanup; a held,
#   unavailable or raced lock preserves it. A checkout without any identity is preserved.
#   The sweep removes the Git registration and directory, then deletes the local branch
#   while it still points at the merged head, and applies a remote branch deletion that
#   Merge-Pr deferred. A busy directory leaves a pending record for the next sweep.
#
# ORPHANED DIRECTORIES — report for manual recovery:
#   A failed `worktree remove` followed by `worktree prune` leaves a directory whose
#   .git file points at a gitdir that no longer exists. Such dirs are invisible to
#   `git worktree list`, so the sweep also scans the directories where worktrees are
#   known to live. A dangling .git file identifies this repository but cannot recover an
#   explicit agent.lockName from the lost registration. Preserve these directories even
#   when a lock inferred from their name is free. Markerless legacy remnants are also
#   retained; names and timestamps cannot prove that no agent owns their remaining files.
#
# AGENT EVIDENCE:
#   Entries under <main checkout>-evidence/ (one folder per work item) are removed once
#   their newest write is older than -EvidenceRetentionDays (default 7).
#
# Guards (never delete work):
#   - skip the main checkout and anything inside it (.claude/worktrees is harness-managed)
#   - skip other repositories' checkouts
#   - skip Git-locked worktrees; only a `PR checkout setup`/`Issue checkout setup` lock
#     older than the two-hour Redis TTL is unlocked (its owner cannot still be setting up)
#   - skip Redis-owned worktrees, including same-owner locks, by reserving, never by status
#   - skip a branch/tip that has an OPEN PR (branch reused for active work)
#   - skip a named branch with no commits beyond origin/main unless the canonical
#     pr-<N> path + branch identity of a merged PR proves it
#   - PRESERVE tracked changes and untracked/ignored files outside known generated output
#   - worktrees with NO merge evidence are kept and listed; opt in to reaping old
#     published detached snapshots with -StaleDays <n>
# -WhatIf only reads lock status: it acquires nothing and deletes nothing.
#
# Run it once per loop iteration (cheap: ~3 gh calls in bulk, plus one association
# call per unmatched leftover — a set that shrinks to near-zero after the first run).
#
# Usage:  pwsh scripts/Remove-MergedWorktrees.ps1 [-Repo owner/name] [-WhatIf] [-StaleDays n] [-EvidenceRetentionDays n]
# Exit:   0 always (a sweep failure must not break the loop; problems are logged)

[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$WhatIf,
    # Opt-in: also remove detached snapshots reachable from origin/main whose HEAD commit
    # is older than this many days. Unpublished work is always preserved.
    [int]$StaleDays = 0,
    [ValidateRange(1, 3650)]
    [int]$EvidenceRetentionDays = 7
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
$repoArgs = @(); if ($Repo) { $repoArgs = @('--repo', $Repo) }

function Warn([string]$m) { [Console]::Error.WriteLine("sweep: $m") }

function Preserve-OrphanedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Reason
    )

    # The lost Git registration may have held an explicit agent.lockName unrelated to
    # the directory name. Neither a free inferred lock nor timestamps prove ownership.
    Write-Host "sweep: preserving orphan for manual recovery; original lock identity is unavailable: $Path ($Reason)"
}

function Test-StaleSetupLock {
    param([Parameter(Mandatory)][string]$Path, [string]$Reason)

    # A setup lock guards checkout only until Redis ownership is registered. One older than
    # the AgentLocks.ps1 TTL outlived its owner; the Redis reservation still applies.
    if ($Reason -notmatch '^(?i:(PR|Issue) checkout setup)$') { return $false }
    $gitDirectory = git -C $Path rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory) { return $false }
    $lockFile = Get-Item -LiteralPath (Join-Path $gitDirectory.Trim() 'locked') -Force -ErrorAction SilentlyContinue
    return $lockFile -and $lockFile.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-2)
}

# "Exit 0 always" is load-bearing: a sweep failure must never kill an otherwise-healthy
# loop iteration. $ErrorActionPreference is 'Stop', so any unguarded throw (a git call
# failing, an unexpected gh output shape, a null string op) would exit non-zero. Wrap the
# whole body so every such error is logged and swallowed.
try {
    # Exact merged heads are squash-safe. Older PRs fall through to association lookup.
    $mergedOids = @{}; $mergedPrByNumber = @{}; $openNames = @{}; $openOids = @{}
    $rawMerged = gh pr list @repoArgs --state merged --limit 1000 --json number,mergedAt,headRefName,headRefOid 2>$null
    if ($LASTEXITCODE -ne 0) { Warn "could not list merged PRs (exit $LASTEXITCODE) -- skipping sweep this round"; exit 0 }
    foreach ($p in (($rawMerged -join "`n") | ConvertFrom-Json)) {
        # Several PRs can share a head SHA; keep each for lock identity.
        if ($p.headRefOid) {
            $oid = $p.headRefOid.Trim()
            if (-not $mergedOids.ContainsKey($oid)) { $mergedOids[$oid] = @() }
            $mergedOids[$oid] += $p
        }
        if ($p.number) { $mergedPrByNumber[[int]$p.number] = $p }
    }

    # Open-PR head branches/tips: never remove a worktree that is actively in review.
    $rawOpen = gh pr list @repoArgs --state open --limit 1000 --json headRefName,headRefOid 2>$null
    if ($LASTEXITCODE -ne 0) { Warn 'could not list open PRs -- skipping sweep this round'; exit 0 }
    foreach ($p in (($rawOpen -join "`n") | ConvertFrom-Json)) {
        if ($p.headRefName) { $openNames[$p.headRefName.Trim()] = $true }
        if ($p.headRefOid) { $openOids[$p.headRefOid.Trim()] = $true }
    }

    # Repo slug for the per-commit association API (gh api takes no --repo flag).
    $slug = $Repo
    if (-not $slug) {
        $slug = gh repo view --json nameWithOwner --jq .nameWithOwner 2>$null
        if ($LASTEXITCODE -ne 0) { $slug = $null }
    }

    $mainRepo = ((git worktree list --porcelain) | Where-Object { $_ -like 'worktree *' } |
        Select-Object -First 1) -replace '^worktree ', ''
    $mainNorm = ($mainRepo -replace '\\', '/').TrimEnd('/')
    $canonicalWorktreeRoot = "$mainNorm-worktrees"

    # Refresh origin/main for the detached-reachability tier. Best-effort: a stale
    # origin/main only makes that tier MISS (safe direction), never over-delete.
    git -C $mainRepo fetch origin main --quiet 2>$null
    $mainTip = git -C $mainRepo rev-parse --verify --quiet origin/main 2>$null
    if ($LASTEXITCODE -ne 0) { $mainTip = $null }

    # Walk worktrees (porcelain: worktree / branch|detached / locked records).
    $wts = @(); $cur = $null; $branch = $null; $detached = $false; $locked = $false; $lockReason = $null
    foreach ($line in (git -C $mainRepo worktree list --porcelain)) {
        if ($line -like 'worktree *') { $cur = $line.Substring(9); $branch = $null; $detached = $false; $locked = $false; $lockReason = $null }
        elseif ($line -like 'branch *') { $branch = ($line.Substring(7) -replace '^refs/heads/', '') }
        elseif ($line -eq 'detached') { $detached = $true }
        elseif ($line -eq 'locked' -or $line -like 'locked *') { $locked = $true; $lockReason = ($line -replace '^locked ?', '') }
        elseif ($line -eq '') { if ($cur) { $wts += [pscustomobject]@{ Path = $cur; Branch = $branch; Detached = $detached; Locked = $locked; LockReason = $lockReason } }; $cur = $null }
    }
    if ($cur) { $wts += [pscustomobject]@{ Path = $cur; Branch = $branch; Detached = $detached; Locked = $locked; LockReason = $lockReason } }

    $removed = 0; $unmatched = @()
    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($w in $wts) {
        if ($w.Path -eq $mainRepo) { continue }
        if (Test-IsDescendantPath -Path $w.Path -Parent $mainRepo) { continue } # harness-managed
        if ($w.Locked -and (Test-StaleSetupLock -Path $w.Path -Reason $w.LockReason)) {
            if ($WhatIf) { Write-Host "sweep: WOULD unlock stale setup lock: $($w.Path)" }
            else {
                git -C $mainRepo worktree unlock $w.Path 2>$null
                if ($LASTEXITCODE -eq 0) { $w.Locked = $false; Write-Host "sweep: unlocked stale setup lock: $($w.Path)" }
            }
        }
        if ($w.Locked) { Write-Host "sweep: skipping locked worktree (session may own it): $($w.Path)"; continue }
        if ($w.Branch -and $openNames.ContainsKey($w.Branch)) { continue }   # active open PR — keep
        # A registration whose .git file is gone is a partial removal; its pending record
        # finishes it. Never let Git resolve such a path to an enclosing repository.
        if (-not (Test-IsLinkedWorktree -Path $w.Path)) { continue }

        $sha = git -C $w.Path rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $sha) { continue }
        if ($openOids.ContainsKey($sha)) { continue }
        # A named branch with no commits beyond main is proven merged only by its name:
        # the canonical pr-<N> path and branch identity below.
        $freshBranch = $w.Branch -and -not $w.Detached -and
            (Test-HeadHasNoWorkBeyondMain -Repo $mainRepo -Head $sha -MainTip $mainTip)

        # A named branch proven to be a merged PR's own head branch takes that PR's lock
        # identity, so a checkout at a non-canonical path without a marker is still
        # reserved. Detached checkouts never borrow one: a setup checkout of main can sit
        # on a merged tip before its owner registers.
        $why = $null; $evidenceLocks = @()
        if (-not $freshBranch -and $mergedOids.ContainsKey($sha)) {
            $why = 'merged PR head tip SHA'
            if ($w.Branch -and -not $w.Detached) {
                $evidenceLocks = @($mergedOids[$sha] | Where-Object { $_.number -and $_.headRefName -ceq $w.Branch } |
                    ForEach-Object { "pr-$([int]$_.number)" })
            }
        }

        $pathPr = Get-PrNumberFromWorktreePath -Path $w.Path
        $canonicalMerged = $pathPr -and $mergedPrByNumber.ContainsKey($pathPr) -and
            (Test-IsCanonicalPrWorktree -Path $w.Path -WorktreeRoot $canonicalWorktreeRoot `
                -Branch $w.Branch -Detached:$w.Detached -PrNumber $pathPr)
        if (-not $why -and $mainTip -and ($w.Detached -or $canonicalMerged)) {
            git -C $mainRepo merge-base --is-ancestor $sha $mainTip 2>$null
            if ($LASTEXITCODE -eq 0) { $why = 'completed snapshot reachable from origin/main' }
        }

        # Association must identify this exact merged head. Merely naming an older
        # merged PR cannot establish that a new local follow-up commit is published.
        if (-not $why -and -not $freshBranch -and $slug) {
            $assocRaw = gh api "repos/$slug/commits/$sha/pulls" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $assoc = if ($assocRaw) { @(($assocRaw -join "`n") | ConvertFrom-Json) } else { @() }
                if (@($assoc | Where-Object { $_.state -eq 'open' }).Count -gt 0) { continue }
                $mergedHeads = @($assoc | Where-Object { $_.merged_at -and $_.head.sha -eq $sha })
                if ($mergedHeads.Count -gt 0) {
                    $why = 'merged PR head via commit association'
                    if ($w.Branch -and -not $w.Detached) {
                        $evidenceLocks = @($mergedHeads | Where-Object { $_.number -and $_.head.ref -ceq $w.Branch } |
                            ForEach-Object { "pr-$([int]$_.number)" })
                    }
                }
            }
        }

        # Age can select only already-published detached snapshots. It never authorizes
        # deleting clean unpublished commits or a fresh branch, even with stale cleanup.
        if (-not $why -and -not $freshBranch -and $StaleDays -gt 0 -and $mainTip) {
            git -C $mainRepo merge-base --is-ancestor $sha $mainTip 2>$null
            if ($LASTEXITCODE -eq 0) {
                $commitEpoch = git -C $w.Path log -1 --format=%ct 2>$null
                if ($LASTEXITCODE -eq 0 -and $commitEpoch -and (($nowEpoch - [long]$commitEpoch) -gt ($StaleDays * 86400L))) {
                    $why = "published snapshot older than $StaleDays day(s)"
                }
            }
        }

        if (-not $why) { $unmatched += $w; continue }

        # Delete the local branch only for merged-PR evidence. It is deleted while the
        # reservation is held, only at the merged head and only when no checkout uses it.
        $branchToDelete = if ($w.Branch -and -not $w.Detached -and $why -notlike 'published snapshot*') { $w.Branch } else { '' }
        $outcome = @(Invoke-WithWorktreeCleanupLocks -Repo $mainRepo -Worktree $w.Path -LockNames $evidenceLocks -Preview:$WhatIf -Action {
            Remove-MergedWorktree -Repo $mainRepo -Worktree $w.Path -ExpectedHead $sha -LockNames $evidenceLocks `
                -DeleteBranch $branchToDelete -Label "($why)" -WhatIf:$WhatIf
        })
        if ($outcome.Count -gt 0 -and ($outcome[-1] -is [bool] -and $outcome[-1] -or $outcome[-1] -eq 'preview')) { $removed++ }
    }

    if ($unmatched.Count -gt 0) {
        Write-Host "sweep: keeping $($unmatched.Count) worktree(s) with no merge evidence:"
        foreach ($w in $unmatched) {
            $label = if ($w.Branch) { "[$($w.Branch)]" } else { '(detached)' }
            Write-Host "sweep:   $($w.Path) $label"
        }
        if ($StaleDays -eq 0) { Write-Host 'sweep: re-run with -StaleDays <n> to also remove published detached snapshots older than n days.' }
    }

    # Removals that a busy directory interrupted, from owner release or this sweep. Run
    # before the orphan scan so a finished remnant is not reported for manual recovery.
    $pendingRemoved = Complete-PendingWorktreeRemovals -Repo $mainRepo -WhatIf:$WhatIf

    # --- Orphaned directories: registration gone, directory left behind. -------------
    # Scan the sibling `<main>-worktrees` convention plus every parent directory of a
    # registered worktree outside the main checkout (never inside it — .claude/worktrees
    # is harness-managed).
    $registered = @{}
    foreach ($w in $wts) { $registered[(($w.Path -replace '\\', '/').TrimEnd('/')).ToLowerInvariant()] = $true }
    $roots = @{}
    $roots[$canonicalWorktreeRoot.ToLowerInvariant()] = $canonicalWorktreeRoot
    foreach ($w in $wts) {
        $p = ($w.Path -replace '\\', '/').TrimEnd('/')
        if ($p -eq $mainNorm -or $p.StartsWith("$mainNorm/", [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $parent = (Split-Path -Path $p -Parent) -replace '\\', '/'
        if ($parent) { $roots[$parent.ToLowerInvariant()] = $parent }
    }

    $pendingPaths = @(Get-PendingWorktreeRemovalPaths -Repo $mainRepo)
    $orphansPreserved = 0
    foreach ($root in $roots.Values) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue)) {
            $norm = (($dir.FullName -replace '\\', '/').TrimEnd('/')).ToLowerInvariant()
            if ($registered.ContainsKey($norm)) { continue }
            # A deferred pending removal is retried by every sweep; it is not an orphan.
            if (@($pendingPaths | Where-Object { Test-SameNativePath -Left $_ -Right $dir.FullName }).Count -gt 0) { continue }
            $marker = Join-Path $dir.FullName '.git'
            if (Test-Path -LiteralPath $marker -PathType Leaf) {
                $gitdir = ((Get-Content -LiteralPath $marker -TotalCount 1) -replace '^gitdir:\s*', '') -replace '\\', '/'
                # Only report dirs that provably WERE worktrees of THIS repo and whose
                # registration is gone. A live marker (gitdir exists) is someone else's.
                if ($gitdir -notlike "$mainNorm/.git/worktrees/*") { continue }
                if (Test-Path -LiteralPath $gitdir) { continue }
                Preserve-OrphanedDirectory -Path $dir.FullName -Reason "dangling gitdir: $gitdir"
                $orphansPreserved++
                continue
            }

            # A .git directory is a standalone repository, never a failed linked worktree.
            if (Test-Path -LiteralPath $marker) { continue }

            # Markerless canonical remnants also need manual recovery; timestamps cannot
            # establish ownership, so do not walk their files just to vary a log message.
            if (-not (Test-SameNativePath -Left $root -Right $canonicalWorktreeRoot)) { continue }
            $pathPr = Get-PrNumberFromWorktreePath -Path $dir.FullName
            if (-not $pathPr -or -not $mergedPrByNumber.ContainsKey($pathPr)) { continue }
            Preserve-OrphanedDirectory -Path $dir.FullName -Reason "markerless remnant of merged PR #$pathPr"
            $orphansPreserved++
        }
    }

    $evidenceRemoved = Remove-StaleAgentEvidence -Root (Get-AgentEvidenceRoot -MainCheckout $mainRepo) -RetentionDays $EvidenceRetentionDays -WhatIf:$WhatIf

    if (-not $WhatIf) { git -C $mainRepo worktree prune }
    $verb = if ($WhatIf) { 'would remove' } else { 'removed' }
    Write-Host "sweep: $verb $removed merged worktree(s); finished $pendingRemoved pending removal(s); preserved $orphansPreserved orphaned dir(s) for manual recovery; $verb $evidenceRemoved stale evidence entr(ies) older than $EvidenceRetentionDays day(s)."
}
catch {
    Warn "unexpected sweep error (ignored, loop continues): $_"
}
exit 0
