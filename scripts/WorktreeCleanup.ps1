# WorktreeCleanup.ps1
# Shared worktree-removal helper, dot-sourced by Merge-Pr.ps1 and
# Remove-MergedWorktrees.ps1. Not meant to be run directly.
#
# Removal policy (one place, both callers):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths.
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - Long-path safe: git's own delete now works because core.longpaths=true is set
#     system-wide; the `\\?\` extended-length Remove-Item is kept as a fallback for
#     environments where that config is missing.

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
    'website/.docusaurus'
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

function Get-PrNumberFromWorktreePath {
    param([Parameter(Mandatory)][string]$Path)

    $leaf = Split-Path -Path $Path -Leaf
    if ($leaf -match '^pr-(?<Number>\d+)(?:-|$)') {
        return [int]$Matches.Number
    }
    return $null
}

function Test-BranchIdentifiesPrNumber {
    param(
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][int]$PrNumber
    )

    return $Branch -match "(?:^|/)pr-$PrNumber(?:$|[-/])"
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

function Get-WorktreeOwnershipBlocker {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Worktree
    )

    $gitDirectory = git -C $Worktree rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $gitDirectory) { return 'could not inspect Git worktree ownership' }
    if (Test-Path -LiteralPath (Join-Path $gitDirectory 'locked')) { return 'Git worktree is locked' }

    $lockNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $enabled = git -C $Worktree config --local --bool --get extensions.worktreeConfig 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { return 'could not inspect worktree configuration' }
    if ($enabled -eq 'true') {
        $marker = git -C $Worktree config --worktree --get agent.lockName 2>$null
        if ($LASTEXITCODE -notin @(0, 1)) { return 'could not inspect ownership marker' }
        if ($marker) { [void]$lockNames.Add($marker.Trim()) }
    }
    # Also cover the short interval between checkout creation and marker registration.
    $branch = git -C $Worktree symbolic-ref --quiet --short HEAD 2>$null
    if ($LASTEXITCODE -notin @(0, 1)) { return 'could not inspect worktree branch' }
    foreach ($identity in @((Split-Path $Worktree -Leaf), $branch)) {
        if ($identity -match '(?:^|/)((?:pr|issue)-\d+)(?:$|[-/])') { [void]$lockNames.Add($Matches[1]) }
    }
    foreach ($lockName in $lockNames) {
        $blocker = Get-AgentLockBlocker -Repo $Repo -LockName $lockName
        if ($blocker) { return $blocker }
    }
    return $null
}

function Remove-MergedWorktree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repo,       # a checkout that is NOT the one being removed (main)
        [Parameter(Mandatory)][string]$Worktree,   # path to remove
        [string]$ExpectedHead,                    # tip independently verified as completed by the caller
        [string]$Label = '',                       # e.g. "#1234" for log lines
        [switch]$WhatIf
    )

    if (-not (Test-Path -LiteralPath $Worktree)) {
        if (-not $WhatIf) { git -C $Repo worktree prune }
        return
    }

    # Only linked worktrees are valid deletion targets. This rejects the primary
    # checkout even through a filesystem alias before any git or recursive delete.
    if (-not (Test-IsLinkedWorktree -Path $Worktree)) {
        Write-Host "WARNING: refusing to remove primary checkout or non-linked worktree $Label : $Worktree"
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
    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving worktree $Label : $Worktree (could not inspect worktree status)"
        return
    }
    $work = @($status | Where-Object {
        if ($_ -notmatch '^(\?\?|!!) ') { return $true }
        return -not (Test-DisposableWorktreePath -Path $_.Substring(3))
    })
    if ($work.Count -gt 0) {
        Write-Host "Preserving dirty worktree $Label : $Worktree (uncommitted work)"
        foreach ($entry in $work) { Write-Host "  $entry" }
        return
    }

    if ($WhatIf) { Write-Host "sweep: WOULD remove $Worktree -- $Label"; return }

    # Recheck after status inspection, which can take time in a large build tree.
    $head = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead -or
        (Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $Worktree)) {
        Write-Host "Preserving worktree $Label : $Worktree (HEAD or ownership changed during inspection)"
        return
    }

    # Primary path: let git remove it (force clears untracked artifacts; tracked is clean).
    git -C $Repo worktree remove --force $Worktree 2>$null

    # Fallback for long-path failures (only if core.longpaths is somehow off).
    if (Test-Path -LiteralPath $Worktree) {
        # Git may have refused removal because ownership changed. Never bypass that
        # refusal with recursive filesystem deletion.
        $blocker = Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $Worktree
        $head = git -C $Worktree rev-parse HEAD 2>$null
        if ($blocker -or $LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead) {
            Write-Host "Preserving worktree $Label : $Worktree (could not confirm HEAD and ownership after failed removal)"
            return
        }
        # Avoid recursing through a package-manager junction if one exists in a docs
        # worktree. Leave it for manual cleanup instead of risking deletion outside
        # the worktree.
        $junction = Get-ChildItem -LiteralPath $Worktree -Directory -Recurse -Force -Filter node_modules -ErrorAction SilentlyContinue |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($junction) {
            Write-Host "WARNING: worktree $Label requires manual removal -- detach the node_modules junction at $($junction.FullName) first, then re-run cleanup: $Worktree"
            return
        }
        # \\?\ disables Win32 path normalization, so forward slashes (git's output
        # format) are NOT translated — convert to backslashes or the delete no-ops.
        Remove-Item -LiteralPath ('\\?\' + ($Worktree -replace '/', '\')) -Recurse -Force -ErrorAction SilentlyContinue
    }

    git -C $Repo worktree prune
    if (Test-Path -LiteralPath $Worktree) {
        Write-Host "WARNING: could not fully remove $Worktree"
    } else {
        Write-Host "Removed worktree $Label : $Worktree"
    }
}
