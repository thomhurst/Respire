# WorktreeCleanup.ps1
# Shared unattended-cleanup helper, dot-sourced by Merge-Pr.ps1,
# Remove-MergedWorktrees.ps1 and Remove-ReleasedWorktree.ps1. Not meant to be run directly.
#
# Cleanup policy (one place, every caller):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths,
#     except that the sweep saves the tracked and untracked changes of a merged PR or closed
#     work item to a retained branch before removing its checkout (unknown ignored files
#     still preserve it).
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - REMOVE a merged checkout (Remove-MergedWorktree) only while every Redis identity
#     that can own it is reserved by the remover, never after a FREE status check.
#     A checkout without a recoverable identity is preserved.
#   - DETACH directory junctions and symlinks before deleting, so no delete follows a link.
#   - DELETE agent evidence (<main checkout>-evidence/<lock name>/) of a pr-<N>/issue-<N>
#     item once it is closed and no registered worktree uses it, under a temporary Redis
#     lease, and any other entry once its newest write is older than the retention window.

# A named branch whose tip is already on main has no commits of its own: it was just
# cut from main, or never got work. Its tip is main's tip, so commit association and
# rebase-merged PR heads point at whichever PR produced main's tip. Only the branch
# name (tier 1) can prove such a branch merged. Unknown main tip fails closed.
function Test-HeadHasNoWorkBeyondMain {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Head,
        [AllowNull()][string]$MainTip
    )

    if (-not $MainTip) { return $true }
    git -C $Repo merge-base --is-ancestor $Head $MainTip 2>$null
    return $LASTEXITCODE -eq 0
}

function New-OrdinalStringMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, bool]])]
    param()

    return [System.Collections.Generic.Dictionary[string, bool]]::new([System.StringComparer]::Ordinal)
}

$script:DisposableWorktreeGeneratedDirectories = New-OrdinalStringMap
foreach ($directory in @(
    '.artifacts',
    '.vs',
    '__pycache__',
    'ARM',
    'ARM64',
    'artifacts',
    'benchmark-results',
    'BenchmarkDotNet.Artifacts',
    'bin',
    'bld',
    'CodeCoverage',
    'Debug',
    'DebugPublic',
    'log',
    'logs',
    'node_modules',
    'obj',
    'Release',
    'Releases',
    'results',
    'StrykerOutput',
    'temptest',
    'TestResults',
    'Win32',
    'x64',
    'x86'
)) {
    $script:DisposableWorktreeGeneratedDirectories[$directory] = $true
}

$script:DisposableWorktreeScopedDirectories = @(
    'docs/.cache',
    'docs/.docusaurus',
    'docs/build',
    'website/build',
    'website/.docusaurus',
    # Root-level SDK installs and agent scratch space.
    '.dotnet',
    '.tmp'
)

function Test-DisposableWorktreePath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    # Quoted porcelain paths require Git's escape decoding. Preserve them rather than
    # risk classifying an unusual source path as generated output.
    if ($Path.StartsWith('"', [System.StringComparison]::Ordinal)) { return $false }

    $normalizedPath = $Path -replace '\\', '/'
    # Keep these root-only patterns aligned with .gitignore. Ignore rules alone
    # cannot authorize deleting arbitrary files: ignored source and secrets survive.
    if (-not $normalizedPath.Contains('/') -and
        ($normalizedPath -cmatch '\.(log|nettrace)$' -or
         $normalizedPath -cmatch '(^|-)(pr-body|review-disposition|review-validation|rebase-validation|comment|issue)\.md$')) {
        return $true
    }
    foreach ($docsGeneratedDirectory in $script:DisposableWorktreeScopedDirectories) {
        if ($normalizedPath -ceq $docsGeneratedDirectory -or
            $normalizedPath.StartsWith("$docsGeneratedDirectory/", [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    foreach ($segment in ($normalizedPath -split '/')) {
        if ($script:DisposableWorktreeGeneratedDirectories.ContainsKey($segment)) { return $true }
    }

    return $false
}

function Test-IsLinkedWorktree {
    param([Parameter(Mandatory)][string]$Path)

    # A primary checkout has a .git directory. A linked worktree has a .git file,
    # including when reached through a symlink or junction. Requiring that marker
    # avoids path-text comparisons and refuses arbitrary directories fail-closed.
    $marker = Join-Path $Path '.git'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }

    $firstLine = Get-Content -LiteralPath $marker -TotalCount 1 -ErrorAction SilentlyContinue
    return $firstLine -match '^gitdir:\s*\S+'
}

function Test-SameNativePath {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )

    try {
        $leftPath = [IO.Path]::GetFullPath($Left).TrimEnd([char[]]@('/', '\'))
        $rightPath = [IO.Path]::GetFullPath($Right).TrimEnd([char[]]@('/', '\'))
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        return [string]::Equals($leftPath, $rightPath, $comparison)
    }
    catch {
        return $false
    }
}

function Get-WorktreeIdentity {
    param([AllowNull()][string]$Name)

    if ($Name -match '(?:^|/)(?<Kind>pr|issue)-(?<Number>\d+)(?:$|[-/])') {
        return [pscustomobject]@{
            PrNumber = if ($Matches.Kind -eq 'pr') { [int]$Matches.Number } else { $null }
            LockName = "$($Matches.Kind)-$($Matches.Number)"
        }
    }
    return $null
}

function Get-PrNumberFromWorktreePath {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-WorktreeIdentity -Name (Split-Path -Path $Path -Leaf)).PrNumber
}

function Test-BranchIdentifiesPrNumber {
    param(
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][int]$PrNumber
    )

    return (Get-WorktreeIdentity -Name $Branch).PrNumber -eq $PrNumber
}

function Test-IsCanonicalPrWorktree {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$WorktreeRoot,
        [AllowNull()][string]$Branch,
        [switch]$Detached,
        [Parameter(Mandatory)][int]$PrNumber
    )

    $parent = Split-Path -Path $Path -Parent
    if (-not (Test-SameNativePath -Left $parent -Right $WorktreeRoot)) { return $false }
    if ((Get-PrNumberFromWorktreePath -Path $Path) -ne $PrNumber) { return $false }
    if ($Detached) { return $true }
    return $Branch -and (Test-BranchIdentifiesPrNumber -Branch $Branch -PrNumber $PrNumber)
}

function Test-IsDescendantPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    try {
        $pathValue = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('/', '\'))
        $parentValue = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@('/', '\'))
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        return $pathValue.StartsWith($parentValue + [IO.Path]::DirectorySeparatorChar, $comparison)
    }
    catch {
        return $false
    }
}

function Select-MergeCleanupWorktree {
    param(
        [string]$ValidatedWorktree,
        [string]$CurrentBranchWorktree,
        [switch]$WasExplicit,
        [switch]$IdentityValid
    )

    if (-not $ValidatedWorktree -or -not $IdentityValid) { return $null }
    if ($WasExplicit) { return $ValidatedWorktree }
    if (-not $CurrentBranchWorktree) { return $null }
    if (Test-SameNativePath -Left $ValidatedWorktree -Right $CurrentBranchWorktree) {
        return $ValidatedWorktree
    }
    return $null
}

function ConvertTo-LockMarkerPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path.Trim()).TrimEnd('\', '/')
}

# Returns the agent.lockName marker that AgentLocks.ps1 wrote for this worktree, or $null.
# `git worktree add` copies the source checkout's config.worktree into the new worktree,
# so a marker counts only when its agent.lockWorktree names this worktree's own path.
# Throws when Git cannot be queried, so callers can fail closed.
function Get-WorktreeLockMarker {
    param([Parameter(Mandatory)][string]$Worktree)

    # `git config --worktree` aliases shared local config until this extension is enabled.
    $enabled = git -C $Worktree config --local --bool --get extensions.worktreeConfig 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'could not inspect worktree configuration' }
    if ($enabled -ne 'true') { return $null }
    $marker = git -C $Worktree config --worktree --get agent.lockName 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'could not inspect ownership marker' }
    if (-not $marker) { return $null }
    $markerOwner = git -C $Worktree config --worktree --get agent.lockWorktree 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { throw 'could not inspect ownership marker' }
    if (-not $markerOwner) { return $null }
    $top = git -C $Worktree rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $top) { throw 'could not inspect worktree root' }
    if ((ConvertTo-LockMarkerPath $markerOwner) -ne (ConvertTo-LockMarkerPath $top)) { return $null }
    return $marker.Trim()
}

function Get-AgentLockBlocker {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$LockName
    )
    # Repo is the primary checkout, never the potentially stale worktree copy.
    # Do not cache FREE across checks: another agent may acquire ownership during a sweep.
    $agentLocks = Join-Path $Repo 'scripts/AgentLocks.ps1'
    if (-not (Test-Path -LiteralPath $agentLocks -PathType Leaf)) { return 'canonical lock script is unavailable' }
    $state = @(& pwsh -NoProfile -File $agentLocks status -LockName $LockName -OwnerId worktree-cleanup-observer 2>$null)
    if ($LASTEXITCODE -ne 0 -or $state.Count -ne 1 -or $state[0] -ne 'FREE') {
        return "Redis lock '$LockName' is held or could not be checked"
    }
    return $null
}

# Every Redis identity that can own a checkout: its own lock marker, plus pr-<N>/issue-<N>
# in its directory or branch name, which cover the interval between checkout creation and
# marker registration. -Orphan skips Git queries for a remnant that lost its metadata.
# Throws when Git cannot be queried, so callers can fail closed.
function Get-WorktreeAgentLockNames {
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [switch]$Orphan
    )

    $lockNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $names = @(Split-Path $Worktree -Leaf)
    if (-not $Orphan) {
        $marker = Get-WorktreeLockMarker -Worktree $Worktree
        if ($marker) { [void]$lockNames.Add($marker) }
        $branch = git -C $Worktree symbolic-ref --quiet --short HEAD 2>$null
        if ($LASTEXITCODE -notin @(0, 1)) { throw 'could not inspect worktree branch' }
        $names += $branch
    }
    foreach ($name in $names) {
        $identity = Get-WorktreeIdentity -Name $name
        if ($identity) { [void]$lockNames.Add($identity.LockName) }
    }
    return @($lockNames | Sort-Object -CaseSensitive)
}

function Get-WorktreeOwnershipBlocker {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree
    )

    $gitDirectory = git -C $Worktree rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory) { return 'could not inspect Git worktree ownership' }
    if (Test-Path -LiteralPath (Join-Path $gitDirectory 'locked')) { return 'Git worktree is locked' }

    try { $lockNames = @(Get-WorktreeAgentLockNames -Worktree $Worktree) }
    catch { return "$_" }
    foreach ($lockName in $lockNames) {
        $blocker = Get-AgentLockBlocker -Repo $Repo -LockName $lockName
        if ($blocker) { return $blocker }
    }
    return $null
}

# Returns the porcelain status entries that are not disposable generated output.
function Get-WorktreeUncommittedWork {
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Status)

    return @($Status | Where-Object {
        if ($_ -notmatch '^(\?\?|!!) ') { return $true }
        return -not (Test-DisposableWorktreePath -Path $_.Substring(3))
    })
}

function Get-WorktreeStatusBlocker {
    param([Parameter(Mandatory)][string]$Worktree)
    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) { return 'could not inspect worktree status' }
    $work = @(Get-WorktreeUncommittedWork -Status $status)
    if ($work.Count -gt 0) { return "uncommitted work: $($work -join ', ')" }
    return $null
}

# Saves the tracked and untracked changes of a worktree as a stash commit on the branch
# `retained-worktrees/<name>-wip-<sha12>`, leaving the checkout clean. Restore it anywhere
# with `git stash apply <branch>`. Ignored files are not saved, so a caller that re-checks
# the status still preserves unknown ones. Throws when nothing could be saved.
function Save-WorktreeUncommittedWork {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$Name
    )

    $before = git -C $Repo rev-parse -q --verify refs/stash 2>$null
    git -C $Worktree stash push --include-untracked --quiet -m "retained worktree $Name" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git stash failed' }
    $stash = git -C $Repo rev-parse -q --verify refs/stash 2>$null
    if (-not $stash -or $stash -eq $before) { throw 'git stash saved nothing' }
    $stash = $stash.Trim()
    $branch = "retained-worktrees/$Name-wip-$($stash.Substring(0, 12))"
    git -C $Repo branch $branch $stash 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not create $branch; the changes remain in git stash list" }
    # The stash stack is shared by every worktree; drop only the entry created here.
    if ((git -C $Repo rev-parse -q --verify 'stash@{0}' 2>$null) -eq $stash) {
        git -C $Repo stash drop --quiet 'stash@{0}' 2>$null | Out-Null
    }
    return $branch
}

# Keeps a detached HEAD reachable from `retained-worktrees/<SHA>` after Git prunes the
# worktree reflog. Never moves an existing branch. Throws when HEAD cannot be retained.
function Save-DetachedWorktreeHead {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree
    )

    git -C $Worktree symbolic-ref -q HEAD 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { return }
    $head = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $head) { throw "Cannot resolve HEAD: $Worktree" }
    $head = $head.Trim()
    $branch = "retained-worktrees/$head"
    $existing = git -C $Repo rev-parse -q --verify "refs/heads/$branch" 2>$null
    if ($LASTEXITCODE -ne 0) {
        git -C $Repo branch $branch $head 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cannot retain detached HEAD: $Worktree" }
    } elseif ($existing -ne $head) {
        throw "Retention branch has moved: $branch"
    }
}

function Clear-CompletedWorktreeArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,       # primary checkout for canonical ownership checks
        [Parameter(Mandatory)][string]$Worktree,   # completed checkout whose generated output may be cleared
        [string]$ExpectedHead,                    # tip independently verified as completed by the caller
        [string]$Label = '',                       # e.g. "#1234" for log lines
        [switch]$WhatIf
    )

    if (-not (Test-Path -LiteralPath $Worktree)) {
        return
    }

    # Only linked worktrees are valid cleanup targets. This rejects the primary
    # checkout even through a filesystem alias before any Git cleanup.
    if (-not (Test-IsLinkedWorktree -Path $Worktree)) {
        Write-Host "WARNING: refusing to clean primary checkout or non-linked worktree $Label : $Worktree"
        return
    }

    $head = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $ExpectedHead -or $head -ne $ExpectedHead) {
        Write-Host "Preserving worktree $Label : $Worktree (completed HEAD is missing or changed)"
        return
    }
    $blocker = Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $Worktree
    if ($blocker) {
        Write-Host "Preserving worktree $Label : $Worktree ($blocker)"
        return
    }

    # Preserve source and unknown ignored files; only known generated output is disposable.
    $blocker = Get-WorktreeStatusBlocker -Worktree $Worktree
    if ($blocker) {
        Write-Host "Preserving worktree $Label : $Worktree ($blocker)"
        return
    }

    if ($WhatIf) { Write-Host "sweep: WOULD clear generated output in $Worktree -- $Label"; return }

    # Recheck after status inspection, which can take time in a large build tree.
    $head = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead -or
        (Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $Worktree)) {
        Write-Host "Preserving worktree $Label : $Worktree (HEAD or ownership changed during inspection)"
        return
    }

    $blocker = Get-WorktreeStatusBlocker -Worktree $Worktree
    if ($blocker) {
        Write-Host "Preserving worktree $Label : $Worktree ($blocker)"
        return
    }
    # Merge-Pr runs while its caller still owns the item lock, so it cannot reserve that
    # lock and clears only disposable generated output. Owner release, or the merged-
    # worktree sweep after ownership is gone, removes the checkout itself.
    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving worktree $Label : $Worktree (could not enumerate generated output)"
        return
    }
    foreach ($entry in $status) {
        if ($entry -notmatch '^(\?\?|!!) ' -or -not (Test-DisposableWorktreePath -Path $entry.Substring(3))) {
            Write-Host "Preserving worktree $Label : $Worktree (new source or unknown output: $entry)"
            return
        }
    }
    foreach ($entry in $status) {
        $head = git -C $Worktree rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead -or
            (Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $Worktree)) {
            Write-Host "Preserving worktree $Label : $Worktree (HEAD or ownership changed before artifact cleanup)"
            return
        }
        git -C $Worktree --literal-pathspecs clean -fdx -- $entry.Substring(3) 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Preserving worktree $Label : $Worktree (could not clear generated path $($entry.Substring(3)))"
            return
        }
    }
    Write-Host "Cleared generated output; preserving checkout for owner release or manual recovery $Label : $Worktree"
    return $true
}

# Runs $Action while this process holds every Redis identity that can own $Worktree, and
# releases them afterwards. Reserving through deletion, rather than checking FREE and then
# deleting, stops a new owner from claiming the item midway. Preserves the checkout (skips
# $Action) when no identity is recoverable or any lock is held, unavailable or raced.
# -Preview only reads lock status and never acquires.
function Invoke-WithWorktreeCleanupLocks {
    param(
        [Parameter(Mandatory)][string]$Repo,       # primary checkout; its AgentLocks.ps1 is canonical
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][scriptblock]$Action,
        [string[]]$LockNames = @(),                # identities proven by merge evidence or a pending record
        [switch]$Orphan,
        [switch]$Preview
    )

    # $Action runs in this function's scope chain, so these names avoid caller variables.
    try {
        $reservationNames = @(@(Get-WorktreeAgentLockNames -Worktree $Worktree -Orphan:$Orphan) + $LockNames |
            Where-Object { $_ } | Sort-Object -CaseSensitive -Unique)
    }
    catch {
        Write-Host "Preserving worktree: ownership could not be inspected ($_): $Worktree"
        return
    }
    if ($reservationNames.Count -eq 0) {
        Write-Host "Preserving worktree: no recoverable lock identity: $Worktree"
        return
    }
    $reservationScript = Join-Path $Repo 'scripts/AgentLocks.ps1'
    if (-not (Test-Path -LiteralPath $reservationScript -PathType Leaf)) {
        Write-Host "Preserving worktree: canonical lock script is unavailable: $Worktree"
        return
    }

    $reservationOwner = "worktree-cleanup-$([Guid]::NewGuid().ToString('N'))"
    $reserved = [Collections.Generic.List[string]]::new()
    try {
        foreach ($reservationName in $reservationNames) {
            if ($Preview) {
                $reservationBlocker = Get-AgentLockBlocker -Repo $Repo -LockName $reservationName
                if ($reservationBlocker) {
                    Write-Host "Preserving worktree: $reservationBlocker : $Worktree"
                    return
                }
                continue
            }
            # Acquire prints a token. Never forward it.
            $null = @(& pwsh -NoProfile -File $reservationScript acquire -LockName $reservationName -OwnerId $reservationOwner 2>&1)
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Preserving worktree: Redis lock '$reservationName' is held or could not be reserved: $Worktree"
                return
            }
            $reserved.Add($reservationName)
        }
        & $Action
    }
    finally {
        for ($index = $reserved.Count - 1; $index -ge 0; $index--) {
            $null = @(& pwsh -NoProfile -File $reservationScript release -LockName $reserved[$index] -OwnerId $reservationOwner 2>&1)
            if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: could not release cleanup reservation $($reserved[$index])." }
        }
    }
}

# Reads the remote branch deletion that Merge-Pr deferred to checkout removal, or $null.
function Get-DeferredRemoteBranchCleanup {
    param([Parameter(Mandatory)][string]$GitDirectory)

    $path = Join-Path $GitDirectory 'respire-merged-branch.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($metadata.Branch -isnot [string] -or [string]::IsNullOrWhiteSpace($metadata.Branch) -or
            $metadata.ExpectedHead -notmatch '^[0-9a-f]{40}$' -or
            $metadata.RemoteUrlHash -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Invalid deferred branch cleanup metadata.'
        }
        return $metadata
    } catch {
        Write-Host 'Preserving remote branch: cleanup metadata is unreadable or invalid.'
        return $null
    }
}

# Branch cleanup after a checkout is gone. The local branch is deleted only while it still
# points at the removed head; `git branch -D` also refuses a branch that another worktree
# has checked out since. The remote branch uses MergedBranchCleanup's head and origin lease.
function Remove-RemovedWorktreeBranches {
    param(
        [Parameter(Mandatory)][string]$Repo,
        $LocalBranch,
        $PendingBranch
    )

    if ($LocalBranch -and $LocalBranch.Branch) {
        $current = git -C $Repo rev-parse --verify --quiet "refs/heads/$($LocalBranch.Branch)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $current) {
            if ($current.Trim() -cne [string]$LocalBranch.ExpectedHead) {
                Write-Host "Preserving local branch '$($LocalBranch.Branch)': it moved after its checkout was removed."
            }
            else {
                git -C $Repo branch -D $LocalBranch.Branch 2>$null | Out-Null
                if ($LASTEXITCODE -eq 0) { Write-Host "Deleted merged local branch '$($LocalBranch.Branch)'." }
                else { Write-Host "Preserving local branch '$($LocalBranch.Branch)': another checkout uses it or deletion failed." }
            }
        }
    }
    if ($PendingBranch) {
        . (Join-Path $PSScriptRoot 'MergedBranchCleanup.ps1')
        $null = Remove-MergedRemoteBranch -Repo $Repo -Branch $PendingBranch.Branch `
            -ExpectedHead $PendingBranch.ExpectedHead -RemoteUrlHash $PendingBranch.RemoteUrlHash
    }
}

# Removes a merged checkout's directory and Git registration, then its local branch when
# -DeleteBranch names it. Call only inside Invoke-WithWorktreeCleanupLocks. Preserves the main
# checkout and anything inside it, other repositories, Git-locked worktrees, a changed HEAD,
# tracked changes, and untracked or ignored files outside known generated output. A failed
# deletion (a busy directory on Windows) leaves a pending record that the next sweep finishes.
# -RetainWorkName (a merged PR or closed work item) instead saves tracked and untracked
# changes to `retained-worktrees/<name>-wip-<sha12>` before removal; unknown ignored files
# still preserve the checkout. -RetainDetachedHead (a closed item whose commits may never
# have merged) also keeps a detached HEAD as `retained-worktrees/<SHA>`.
# Returns $true when the checkout is gone, 'preview' when -WhatIf would remove it.
function Remove-MergedWorktree {
    param(
        [Parameter(Mandatory)][string]$Repo,       # primary checkout
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$ExpectedHead, # tip the caller verified as merged
        [string[]]$LockNames = @(),                # merge-evidence identities for a pending retry
        [string]$DeleteBranch,
        [string]$RetainWorkName,                  # finished work item whose changes are retained
        [switch]$RetainDetachedHead,
        [string]$Label = '',
        [switch]$WhatIf
    )

    $keep = { param($Reason) Write-Host "Preserving worktree $Label : $Worktree ($Reason)"; return $false }
    if (-not (Test-Path -LiteralPath $Worktree)) { return $false }
    # Only linked worktrees are valid targets. This rejects the primary checkout even
    # through a filesystem alias before any Git operation.
    if (-not (Test-IsLinkedWorktree -Path $Worktree)) {
        Write-Host "WARNING: refusing to remove primary checkout or non-linked worktree $Label : $Worktree"
        return $false
    }
    if ((Test-SameNativePath -Left $Worktree -Right $Repo) -or (Test-IsDescendantPath -Path $Worktree -Parent $Repo)) {
        return & $keep 'main checkout or harness-managed worktree'
    }
    $common = git -C $Repo rev-parse --path-format=absolute --git-common-dir 2>$null
    $commonOk = $LASTEXITCODE -eq 0 -and $common
    $targetCommon = git -C $Worktree rev-parse --path-format=absolute --git-common-dir 2>$null
    if (-not $commonOk -or $LASTEXITCODE -ne 0 -or -not $targetCommon -or
        -not (Test-SameNativePath -Left $common.Trim() -Right $targetCommon.Trim())) {
        return & $keep 'belongs to another repository or could not be inspected'
    }
    $top = git -C $Worktree rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $top -or -not (Test-SameNativePath -Left $top.Trim() -Right $Worktree)) {
        return & $keep 'not a worktree root'
    }
    $gitDirectory = git -C $Worktree rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory -or (Test-SameNativePath -Left $gitDirectory.Trim() -Right $common.Trim())) {
        return & $keep 'could not inspect Git worktree registration'
    }
    $gitDirectory = $gitDirectory.Trim()
    if (Test-Path -LiteralPath (Join-Path $gitDirectory 'locked')) { return & $keep 'Git worktree is locked' }
    $head = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead) { return & $keep 'merged HEAD is missing or changed' }
    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) { return & $keep 'could not inspect worktree status' }
    $work = @(Get-WorktreeUncommittedWork -Status $status)
    # A stash cannot carry ignored files (secrets, local settings), so they always
    # preserve the checkout.
    if ($work.Count -gt 0 -and (-not $RetainWorkName -or @($work | Where-Object { $_.StartsWith('!! ') }).Count -gt 0)) {
        return & $keep "uncommitted work: $($work -join ', ')"
    }

    if ($WhatIf) {
        $retained = if ($work.Count -gt 0) { " (saving $($work.Count) uncommitted entr(ies) to a retained branch)" } else { '' }
        Write-Host "sweep: WOULD remove $Worktree -- $Label$retained"
        return 'preview'
    }

    if ($RetainWorkName) {
        # Nothing will come back for a finished item's checkout. Keep everything it holds
        # reachable from a branch instead of keeping the directory.
        try {
            if ($work.Count -gt 0) {
                $saved = Save-WorktreeUncommittedWork -Repo $Repo -Worktree $Worktree -Name $RetainWorkName
                Write-Host "Saved uncommitted work of $Worktree to $saved (restore with: git stash apply $saved)"
                $blocker = Get-WorktreeStatusBlocker -Worktree $Worktree
                if ($blocker) { return & $keep "after saving, $blocker" }
            }
            if ($RetainDetachedHead) { Save-DetachedWorktreeHead -Repo $Repo -Worktree $Worktree }
        }
        catch { return & $keep "could not retain its work: $_" }
    }

    $pendingBranch = Get-DeferredRemoteBranchCleanup -GitDirectory $gitDirectory
    $localBranch = if ($DeleteBranch) { [ordered]@{ Branch = $DeleteBranch; ExpectedHead = $ExpectedHead } } else { $null }
    if (-not (Remove-WorktreeDirectoryLinks -Worktree $Worktree -Label $Label)) {
        return & $keep 'directory links could not be detached'
    }
    try { $recordedLocks = @(Get-WorktreeAgentLockNames -Worktree $Worktree) + $LockNames }
    catch { return & $keep "ownership could not be inspected: $_" }
    # Without a record a failed delete would strand an unrecognizable remnant.
    $pending = Register-PendingWorktreeRemoval -Repo $Repo -Worktree $Worktree -GitDirectory $gitDirectory `
        -LockNames $recordedLocks -PendingBranch $pendingBranch -LocalBranch $localBranch
    if (-not $pending) { return & $keep 'could not record a pending removal' }
    git -C $Repo -c core.longpaths=true worktree remove --force -- $Worktree 2>$null | Out-Null
    if (Test-Path -LiteralPath $Worktree) {
        Write-Host "Merged worktree removal is pending: $Worktree is in use. The next Remove-MergedWorktrees.ps1 sweep finishes it."
        return $false
    }
    git -C $Repo worktree prune 2>$null | Out-Null
    Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
    Write-Host "Removed merged worktree $Label : $Worktree"
    Remove-RemovedWorktreeBranches -Repo $Repo -LocalBranch $localBranch -PendingBranch $pendingBranch
    return $true
}

# Git for Windows follows a directory junction while deleting a worktree: it empties the
# target, then fails with "Directory not empty" on the link and leaves a remnant without
# its .git file. Package managers link node_modules packages with junctions, and a junction
# can also point into another checkout or a shared store. Delete each link itself, never its
# target, before any recursive delete. Returns whether no directory link remains.
function Remove-WorktreeDirectoryLinks {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [string]$Label = ''
    )

    if (-not $IsWindows) { return $true }

    # Get-ChildItem does not recurse through links, so every match is a link to remove.
    $find = { @(Get-ChildItem -LiteralPath $Worktree -Directory -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue) }
    foreach ($link in (& $find)) {
        # DirectoryInfo.Delete() without recursion removes the reparse point only.
        try { $link.Delete() } catch { Write-Host "Could not detach directory link $($link.FullName): $_" }
    }

    $remaining = & $find
    if ($remaining.Count -gt 0) {
        Write-Host "WARNING: worktree $Label kept: $($remaining.Count) directory link(s) could not be detached, first $($remaining[0].FullName): $Worktree"
        return $false
    }
    return $true
}

# AGENT EVIDENCE
# Agents keep logs, reports and PR-body drafts for a work item under
# <main checkout>-evidence/<lock name>/, beside the worktree root, so nothing lands
# loose in the parent directory. Evidence matters only while its item is open.
function Get-AgentEvidenceRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MainCheckout)

    return "$(($MainCheckout -replace '\\', '/').TrimEnd('/'))-evidence"
}

# Removes each top-level entry of the evidence root whose newest write is older than
# the retention window, except entries named in -Skip (already handled by the finished
# pass, which -WhatIf leaves on disk). Returns the number of entries removed (or that would be).
function Remove-StaleAgentEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][int]$RetentionDays,
        [AllowEmptyCollection()][string[]]$Skip = @(),
        [switch]$WhatIf
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return 0 }
    $cutoff = [DateTime]::UtcNow.AddDays(-$RetentionDays)
    $removed = 0
    foreach ($entry in (Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)) {
        if ($entry.Name -cin $Skip) { continue }
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Write-Host "sweep: skipping evidence entry that is a link: $($entry.FullName)"
            continue
        }
        $items = @($entry)
        if ($entry.PSIsContainer) {
            # Get-ChildItem does not recurse through links, so a link's target never counts.
            $items += @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue)
        }
        $newest = ($items | Measure-Object -Property LastWriteTimeUtc -Maximum).Maximum
        if ($newest -ge $cutoff) { continue }
        if (Remove-AgentEvidenceEntry -Entry $entry -Kind 'stale' -WhatIf:$WhatIf) { $removed++ }
    }
    return $removed
}

# Removes the evidence of finished work items: each `pr-<N>` or `issue-<N>` folder whose
# item is closed (merged or not) and that no registered worktree still resolves to.
# Evidence of an open item survives release, because the merge gate cites archives across
# units. A temporary Redis lease on the item is acquired and held through deletion, so a
# new owner cannot start writing midway; a held or unavailable lease, or an unreadable
# item state, keeps the evidence. -WhatIf only reads lock status (FREE is required).
# Returns the names of the entries removed (or that would be).
function Remove-FinishedAgentEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$AgentLocks,     # the primary checkout's AgentLocks.ps1
        # Receives the lock name; returns $true only when the item is confirmed closed.
        [Parameter(Mandatory)][scriptblock]$IsClosed,
        [AllowEmptyCollection()][string[]]$ActiveLockNames = @(),
        [switch]$WhatIf
    )

    $removed = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $removed.ToArray() }
    if (-not (Test-Path -LiteralPath $AgentLocks -PathType Leaf)) {
        Write-Host 'sweep: canonical lock script is unavailable; keeping finished evidence'
        return $removed.ToArray()
    }
    $active = New-OrdinalStringMap
    foreach ($name in $ActiveLockNames) { if ($name) { $active[$name] = $true } }

    foreach ($entry in (Get-ChildItem -LiteralPath $Root -Directory -Force -ErrorAction SilentlyContinue)) {
        $name = $entry.Name
        if ($name -cnotmatch '^(pr|issue)-\d+$' -or $active.ContainsKey($name)) { continue }
        try { if (-not (& $IsClosed $name)) { continue } }
        catch {
            Write-Host "sweep: could not read the state of $name; keeping its evidence"
            continue
        }

        $owner = "evidence-cleanup-$([Guid]::NewGuid().ToString('N'))"
        if ($WhatIf) {
            $state = @(& pwsh -NoProfile -File $AgentLocks status -LockName $name -OwnerId $owner 2>$null)
            $leased = $LASTEXITCODE -eq 0 -and $state.Count -eq 1 -and $state[0] -eq 'FREE'
        }
        else {
            # Acquire prints a token. Never forward it.
            $null = @(& pwsh -NoProfile -File $AgentLocks acquire -LockName $name -OwnerId $owner 2>&1)
            $leased = $LASTEXITCODE -eq 0
        }
        if (-not $leased) {
            Write-Host "sweep: keeping evidence of $name (Redis lock '$name' is held or could not be reserved)"
            continue
        }
        try {
            if (Remove-AgentEvidenceEntry -Entry $entry -Kind 'finished' -WhatIf:$WhatIf) { $removed.Add($name) }
        }
        finally {
            if (-not $WhatIf) {
                $null = @(& pwsh -NoProfile -File $AgentLocks release -LockName $name -OwnerId $owner 2>&1)
                if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: could not release evidence cleanup reservation $name." }
            }
        }
    }
    return $removed.ToArray()
}

# Deletes one top-level evidence entry without following links. Returns whether it is
# gone (or, with -WhatIf, would be removed).
function Remove-AgentEvidenceEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][IO.FileSystemInfo]$Entry,
        [Parameter(Mandatory)][string]$Kind,
        [switch]$WhatIf
    )

    if (($Entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Write-Host "sweep: skipping evidence entry that is a link: $($Entry.FullName)"
        return $false
    }
    if ($WhatIf) {
        Write-Host "sweep: WOULD remove $Kind evidence $($Entry.FullName)"
        return $true
    }
    if ($Entry -is [IO.DirectoryInfo] -and -not (Remove-WorktreeDirectoryLinks -Worktree $Entry.FullName -Label '(evidence)')) { return $false }
    Remove-Item -LiteralPath $Entry.FullName -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Entry.FullName) {
        Write-Host "sweep: WARNING could not remove $Kind evidence $($Entry.FullName)"
        return $false
    }
    Write-Host "sweep: removed $Kind evidence $($Entry.FullName)"
    return $true
}

# PENDING REMOVALS
# On Windows a directory cannot be deleted while any process has its current directory
# inside it, for example the agent shell that ran release from the worktree. Git then
# deletes most of the tree, including the .git file, and fails. Owner release and the
# merged-worktree sweep record each vetted checkout, with every lock identity that can
# own it, before deleting it, and remove the record on success. The sweep finishes a
# recorded removal while it reserves those identities, and only when every remaining file
# is known generated output: it deletes those files one by one and empty directories
# non-recursively, so source written since the removal began survives and blocks it.

function Get-PendingWorktreeRemovalDirectory {
    param([Parameter(Mandatory)][string]$Repo)

    $common = git -C $Repo rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $common) { return $null }
    return Join-Path $common.Trim() 'agent-pending-removals'
}

function Register-PendingWorktreeRemoval {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$GitDirectory,
        # A record without identities is never finished unattended.
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$LockNames,
        $PendingBranch,
        $LocalBranch
    )

    $directory = Get-PendingWorktreeRemovalDirectory -Repo $Repo
    if (-not $directory) { return $null }
    $path = [IO.Path]::GetFullPath($Worktree).TrimEnd([char[]]@('/', '\'))
    $key = if ($IsWindows) { $path.ToLowerInvariant() } else { $path }
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
    $record = Join-Path $directory ($hash.Substring(0, 16).ToLowerInvariant() + '.json')
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [ordered]@{
        path          = $path
        gitDirectory  = [IO.Path]::GetFullPath($GitDirectory.Trim())
        lockNames     = @($LockNames | Where-Object { $_ } | Sort-Object -CaseSensitive -Unique)
        recordedAt    = [DateTimeOffset]::UtcNow.ToString('o')
        pendingBranch = $PendingBranch
        localBranch   = $LocalBranch
    } | ConvertTo-Json -Compress -Depth 4 | Set-Content -LiteralPath $record -NoNewline
    return $record
}

# Paths with a readable pending removal record; a later sweep finishes them.
function Get-PendingWorktreeRemovalPaths {
    param([Parameter(Mandatory)][string]$Repo)

    $directory = Get-PendingWorktreeRemovalDirectory -Repo $Repo
    if (-not $directory -or -not (Test-Path -LiteralPath $directory)) { return @() }
    return @(Get-ChildItem -LiteralPath $directory -File -Filter '*.json' | ForEach-Object {
        try { [string](Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).path } catch { $null }
    } | Where-Object { $_ })
}

# Deletes a remnant that holds only generated output. Returns $null on success, or why
# the remnant was kept.
function Remove-GeneratedRemnant {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$GitDirectory
    )

    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction Stop)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Path, $file.FullName)
        if ($relative -eq '.git') {
            $gitdir = ((Get-Content -LiteralPath $file.FullName -TotalCount 1) -replace '^gitdir:\s*', '').Trim()
            if (Test-SameNativePath -Left $gitdir -Right $GitDirectory) { continue }
        }
        elseif (Test-DisposableWorktreePath -Path $relative) { continue }
        return "remaining file is not generated output: $relative"
    }
    foreach ($file in $files) {
        try { [IO.File]::Delete($file.FullName) } catch { return "file is in use: $($file.FullName)" }
    }
    # Non-recursive deletes fail on any file written after enumeration.
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Recurse -Force -ErrorAction Stop |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object FullName) + $Path
    foreach ($directory in $directories) {
        try { [IO.Directory]::Delete($directory, $false) } catch { return "directory is in use or not empty: $directory" }
    }
    return $null
}

function Complete-PendingWorktreeRemovals {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [switch]$WhatIf
    )

    $directory = Get-PendingWorktreeRemovalDirectory -Repo $Repo
    if (-not $directory -or -not (Test-Path -LiteralPath $directory)) { return 0 }
    $completed = 0
    foreach ($recordFile in (Get-ChildItem -LiteralPath $directory -File -Filter '*.json')) {
        try {
            $record = Get-Content -LiteralPath $recordFile.FullName -Raw | ConvertFrom-Json
            $path = [string]$record.path
            $recordedAt = [DateTimeOffset]::Parse([string]$record.recordedAt, [Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            Write-Host "sweep: WARNING unreadable pending removal record: $($recordFile.FullName)"
            continue
        }
        if (-not $path) {
            Write-Host "sweep: WARNING pending removal record has no path: $($recordFile.FullName)"
            continue
        }
        $drop = { if (-not $WhatIf) { Remove-Item -LiteralPath $recordFile.FullName -Force -ErrorAction SilentlyContinue } }

        if (Test-Path -LiteralPath $path) {
            # A new checkout at the same path has its own Git metadata, created after the record.
            $marker = Join-Path $path '.git'
            if (Test-Path -LiteralPath $marker -PathType Container) {
                Write-Host "sweep: dropping pending removal for a standalone repository: $path"
                & $drop
                continue
            }
            if (Test-Path -LiteralPath $marker -PathType Leaf) {
                $gitdir = ((Get-Content -LiteralPath $marker -TotalCount 1) -replace '^gitdir:\s*', '').Trim()
                if ($gitdir -and (Test-Path -LiteralPath $gitdir) -and (
                        -not (Test-SameNativePath -Left $gitdir -Right ([string]$record.gitDirectory)) -or
                        (Get-Item -LiteralPath $gitdir -Force).CreationTimeUtc -gt $recordedAt.UtcDateTime)) {
                    Write-Host "sweep: dropping pending removal superseded by a new checkout: $path"
                    & $drop
                    continue
                }
            }
        }

        # Release records one lockName; current records list every identity in lockNames.
        $recordLocks = @(@($record.lockNames) + @($record.lockName) | Where-Object { $_ } | ForEach-Object { [string]$_ })
        # Reserve the identities through deletion and branch cleanup. A held, unavailable
        # or raced lock defers the record to a later sweep.
        $outcome = @(Invoke-WithWorktreeCleanupLocks -Repo $Repo -Worktree $path -Orphan -LockNames $recordLocks -Preview:$WhatIf -Action {
            if ($WhatIf) {
                Write-Host "sweep: WOULD finish pending removal of $path"
                return 'preview'
            }
            if (Test-Path -LiteralPath $path) {
                try { $remnantBlocker = Remove-GeneratedRemnant -Path $path -GitDirectory ([string]$record.gitDirectory) }
                catch { $remnantBlocker = "could not inspect remnant: $($_.Exception.Message)" }
                if ($remnantBlocker -like 'remaining file*') {
                    Write-Host "sweep: WARNING preserving pending removal for manual recovery; $remnantBlocker : $path"
                    return 'preserved'
                }
                if ($remnantBlocker) {
                    Write-Host "sweep: pending removal is still blocked, retrying next sweep ($remnantBlocker): $path"
                    return 'blocked'
                }
            }
            git -C $Repo worktree prune 2>$null | Out-Null
            Write-Host "sweep: finished pending removal of $path"
            Remove-RemovedWorktreeBranches -Repo $Repo -LocalBranch $record.localBranch -PendingBranch $record.pendingBranch
            return 'finished'
        })
        $result = if ($outcome.Count -gt 0) { $outcome[-1] } else { $null }
        if ($result -in @('preserved', 'finished')) { & $drop }
        if ($result -eq 'finished') { $completed++ }
        if (-not $result) { Write-Host "sweep: deferring pending removal of $path" }
    }
    return $completed
}
