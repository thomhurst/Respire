# Remove-MergedWorktrees.ps1
# Safety-net sweep for completed worktrees, preserving active or unpublished work.
#
# SQUASH-SAFE DETECTION:
#   - Exact merged PR head SHA (including renamed or detached checkouts).
#   - Detached/canonical merged-PR snapshots already reachable from origin/main.
#   - Exact head SHA from a merged commit-to-PR association outside the bulk window.
# A branch/path name, old commit date, or deleted upstream never proves that the
# current tip is disposable. Unpublished follow-up commits must survive.
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
# Guards (never delete work):
#   - skip the main checkout and anything inside it (.claude/worktrees is harness-managed)
#   - skip Git-locked and Redis-owned worktrees, including same-owner locks
#   - skip a branch/tip that has an OPEN PR (branch reused for active work)
#   - PRESERVE any worktree with uncommitted tracked changes (shared helper)
#   - worktrees with NO merge evidence are kept and listed; opt in to reaping old
#     published snapshots with -StaleDays <n>
#
# Run it once per loop iteration (cheap: ~3 gh calls in bulk, plus one association
# call per unmatched leftover — a set that shrinks to near-zero after the first run).
#
# Usage:  pwsh scripts/Remove-MergedWorktrees.ps1 [-Repo owner/name] [-WhatIf] [-StaleDays n]
# Exit:   0 always (a sweep failure must not break the loop; problems are logged)

[CmdletBinding()]
param(
    [string]$Repo,
    [switch]$WhatIf,
    # Opt-in: also remove clean snapshots reachable from origin/main whose HEAD
    # commit is older than this many days. Unpublished work is always preserved.
    [int]$StaleDays = 0
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
        if ($p.headRefOid) { $mergedOids[$p.headRefOid.Trim()] = $true }
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
    $wts = @(); $cur = $null; $branch = $null; $detached = $false; $locked = $false
    foreach ($line in (git -C $mainRepo worktree list --porcelain)) {
        if ($line -like 'worktree *') { $cur = $line.Substring(9); $branch = $null; $detached = $false; $locked = $false }
        elseif ($line -like 'branch *') { $branch = ($line.Substring(7) -replace '^refs/heads/', '') }
        elseif ($line -eq 'detached') { $detached = $true }
        elseif ($line -eq 'locked' -or $line -like 'locked *') { $locked = $true }
        elseif ($line -eq '') { if ($cur) { $wts += [pscustomobject]@{ Path = $cur; Branch = $branch; Detached = $detached; Locked = $locked } }; $cur = $null }
    }
    if ($cur) { $wts += [pscustomobject]@{ Path = $cur; Branch = $branch; Detached = $detached; Locked = $locked } }

    $removed = 0; $unmatched = @()
    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    foreach ($w in $wts) {
        if ($w.Path -eq $mainRepo) { continue }
        if (Test-IsDescendantPath -Path $w.Path -Parent $mainRepo) { continue } # harness-managed
        if ($w.Locked) { Write-Host "sweep: skipping locked worktree (session may own it): $($w.Path)"; continue }
        if ($w.Branch -and $openNames.ContainsKey($w.Branch)) { continue }   # active open PR — keep

        $sha = git -C $w.Path rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $sha) { continue }
        if ($openOids.ContainsKey($sha)) { continue }

        $why = $null
        if ($mergedOids.ContainsKey($sha)) { $why = 'merged PR head tip SHA' }

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
        if (-not $why -and $slug) {
            $assocRaw = gh api "repos/$slug/commits/$sha/pulls" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $assoc = if ($assocRaw) { @(($assocRaw -join "`n") | ConvertFrom-Json) } else { @() }
                if (@($assoc | Where-Object { $_.state -eq 'open' }).Count -gt 0) { continue }
                if (@($assoc | Where-Object { $_.merged_at -and $_.head.sha -eq $sha }).Count -gt 0) {
                    $why = 'merged PR head via commit association'
                }
            }
        }

        # Age can select only already-published snapshots. It never authorizes
        # deleting clean unpublished commits, even with explicit stale cleanup.
        if (-not $why -and $StaleDays -gt 0 -and $mainTip) {
            git -C $mainRepo merge-base --is-ancestor $sha $mainTip 2>$null
            if ($LASTEXITCODE -eq 0) {
                $commitEpoch = git -C $w.Path log -1 --format=%ct 2>$null
                if ($LASTEXITCODE -eq 0 -and $commitEpoch -and (($nowEpoch - [long]$commitEpoch) -gt ($StaleDays * 86400L))) {
                    $why = "published snapshot older than $StaleDays day(s)"
                }
            }
        }

        if (-not $why) { $unmatched += $w; continue }

        $didRemove = Remove-MergedWorktree -Repo $mainRepo -Worktree $w.Path -ExpectedHead $sha -Label "($why)" -WhatIf:$WhatIf
        if ($WhatIf) { continue }
        if ($didRemove) {
            $removed++
            # Local refs are cheap recovery points. Another checkout can reuse the same
            # branch and SHA at any time, which a compare-and-delete cannot detect.
        }
    }

    if ($unmatched.Count -gt 0) {
        Write-Host "sweep: keeping $($unmatched.Count) worktree(s) with no merge evidence:"
        foreach ($w in $unmatched) {
            $label = if ($w.Branch) { "[$($w.Branch)]" } else { '(detached)' }
            Write-Host "sweep:   $($w.Path) $label"
        }
        if ($StaleDays -eq 0) { Write-Host 'sweep: re-run with -StaleDays <n> to also remove published snapshots older than n days.' }
    }

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

    $orphansPreserved = 0
    foreach ($root in $roots.Values) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue)) {
            $norm = (($dir.FullName -replace '\\', '/').TrimEnd('/')).ToLowerInvariant()
            if ($registered.ContainsKey($norm)) { continue }
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

    if (-not $WhatIf) { git -C $mainRepo worktree prune }
    Write-Host "sweep: removed $removed merged worktree(s); preserved $orphansPreserved orphaned dir(s) for manual recovery."
}
catch {
    Warn "unexpected sweep error (ignored, loop continues): $_"
}
exit 0
