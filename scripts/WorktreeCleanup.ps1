# WorktreeCleanup.ps1
# Shared worktree-removal helper, dot-sourced by Merge-Pr.ps1 and
# Remove-MergedWorktrees.ps1. Not meant to be run directly.
#
# Removal policy (one place, both callers):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths.
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - Git performs the final dirty/lock checks. Failed removal preserves the directory;
#     recursive filesystem deletion must never bypass Git's refusal.

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
    foreach ($name in @((Split-Path $Worktree -Leaf), $branch)) {
        $identity = Get-WorktreeIdentity -Name $name
        if ($identity) { [void]$lockNames.Add($identity.LockName) }
    }
    foreach ($lockName in $lockNames) {
        $blocker = Get-AgentLockBlocker -Repo $Repo -LockName $lockName
        if ($blocker) { return $blocker }
    }
    return $null
}

function Get-WorktreeStatusBlocker {
    param([Parameter(Mandatory)][string]$Worktree)
    $status = @(git -C $Worktree status --porcelain=v1 --untracked-files=all --ignored=matching 2>$null)
    if ($LASTEXITCODE -ne 0) { return 'could not inspect worktree status' }
    $work = @($status | Where-Object {
        if ($_ -notmatch '^(\?\?|!!) ') { return $true }
        return -not (Test-DisposableWorktreePath -Path $_.Substring(3))
    })
    if ($work.Count -gt 0) { return "uncommitted work: $($work -join ', ')" }
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
    $blocker = Get-WorktreeStatusBlocker -Worktree $Worktree
    if ($blocker) {
        Write-Host "Preserving worktree $Label : $Worktree ($blocker)"
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

    $blocker = Get-WorktreeStatusBlocker -Worktree $Worktree
    if ($blocker) {
        Write-Host "Preserving worktree $Label : $Worktree ($blocker)"
        return
    }
    # Atomically vacate the published path before the last ignored-file inspection.
    # A path-based writer either lands in this snapshot or recreates the original path;
    # it cannot put an ignored file into the deletion target after that inspection.
    $original = [IO.Path]::GetFullPath($Worktree).TrimEnd([char[]]@('/', '\'))
    $parent = Split-Path -Path $original -Parent
    $quarantine = "$original-cleanup-$([guid]::NewGuid().ToString('N'))"
    if (-not (Test-IsDescendantPath -Path $original -Parent $parent) -or
        -not (Test-IsDescendantPath -Path $quarantine -Parent $parent) -or
        (Test-Path -LiteralPath $quarantine)) {
        Write-Host "WARNING: could not establish a safe quarantine path for $Worktree"
        return
    }
    git -C $Repo worktree move $original $quarantine 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving worktree $Label : $original (could not quarantine checkout)"
        return
    }
    try {
        $head = git -C $quarantine rev-parse HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedHead -or
            (Get-WorktreeOwnershipBlocker -Repo $Repo -Worktree $quarantine) -or
            (Get-WorktreeStatusBlocker -Worktree $quarantine)) {
            Write-Host "Preserving quarantined worktree $Label : $quarantine (work or ownership changed)"
            return
        }

        # Some disposable output (for example Debug/) is not ignored by Git. Remove
        # only those explicitly classified paths; leave ordinary source to Git's guard.
        $untracked = @(git -C $quarantine status --porcelain=v1 --untracked-files=all 2>$null)
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Preserving quarantined worktree $Label : $quarantine (final status inspection failed)"
            return
        }
        foreach ($entry in $untracked) {
            if ($entry -notmatch '^\?\? ' -or -not (Test-DisposableWorktreePath -Path $entry.Substring(3))) {
                Write-Host "Preserving quarantined worktree $Label : $quarantine (non-disposable entry: $entry)"
                return
            }
            git -C $quarantine --literal-pathspecs clean -f -- $entry.Substring(3) 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Preserving quarantined worktree $Label : $quarantine (generated-file cleanup failed)"
                return
            }
        }
        # Never force removal or recursively delete after Git refuses.
        git -C $Repo worktree remove $quarantine 2>$null
        if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $quarantine)) {
            Write-Host "WARNING: preserving worktree after Git removal failed $Label : $quarantine"
            return
        }
        if (Test-Path -LiteralPath $original) {
            Write-Host "Preserving newly created path $Label : $original"
            return
        }
        Write-Host "Removed worktree $Label : $original"
        return $true
    }
    finally {
        # Restore retained work when its old path is still free. Never overwrite a new
        # checkout or files created by a writer using the original path.
        if ((Test-Path -LiteralPath $quarantine) -and -not (Test-Path -LiteralPath $original)) {
            git -C $Repo worktree move $quarantine $original 2>$null
            if ($LASTEXITCODE -ne 0) { Write-Host "Recovery worktree remains at $quarantine" }
        }
    }
}
