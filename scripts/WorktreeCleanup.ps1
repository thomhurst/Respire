# WorktreeCleanup.ps1
# Shared unattended-cleanup helper, dot-sourced by Merge-Pr.ps1 and
# Remove-MergedWorktrees.ps1. Not meant to be run directly.
#
# Cleanup policy (one place, both callers):
#   - PRESERVE tracked changes and untracked/ignored files outside known generated paths.
#   - CLEAR known build artifacts and root-level workflow output covered by .gitignore.
#   - PRESERVE the checkout itself: a free lock cannot rule out background directory handles.
#     Explicit owner release removes checkouts after the owner stops its processes.

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
    # A free lease cannot prove that an old process has released its cwd/directory
    # handles. Even renaming the tree cannot stop that process from writing ignored
    # source after inspection. Unattended cleanup therefore removes only disposable
    # generated output; owner-driven release remains responsible for checkout removal.
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

# PENDING RELEASE REMOVALS
# On Windows a directory cannot be deleted while any process has its current directory
# inside it, for example the agent shell that ran release from the worktree. Git then
# deletes most of the tree, including the .git file, and fails. Owner release records
# each vetted checkout before deleting it and removes the record on success. The sweep
# finishes a recorded removal only when every remaining file is known generated output:
# it deletes those files one by one and empty directories non-recursively, so source
# written since release survives and blocks completion.

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
        [Parameter(Mandatory)][string]$LockName,
        $PendingBranch
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
        lockName      = $LockName
        recordedAt    = [DateTimeOffset]::UtcNow.ToString('o')
        pendingBranch = $PendingBranch
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $record -NoNewline
    return $record
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
        $drop = { if (-not $WhatIf) { Remove-Item -LiteralPath $recordFile.FullName -Force -ErrorAction SilentlyContinue } }

        if ($path -and (Test-Path -LiteralPath $path)) {
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

            $lockNames = @([string]$record.lockName)
            $identity = Get-WorktreeIdentity -Name (Split-Path -Path $path -Leaf)
            if ($identity) { $lockNames += $identity.LockName }
            $blocker = $null
            foreach ($lockName in @($lockNames | Where-Object { $_ } | Sort-Object -Unique)) {
                $blocker = Get-AgentLockBlocker -Repo $Repo -LockName $lockName
                if ($blocker) { break }
            }
            if ($blocker) {
                Write-Host "sweep: deferring pending removal of $path ($blocker)"
                continue
            }
            if ($WhatIf) {
                Write-Host "sweep: WOULD finish pending removal of $path"
                continue
            }
            try { $blocker = Remove-GeneratedRemnant -Path $path -GitDirectory ([string]$record.gitDirectory) }
            catch { $blocker = "could not inspect remnant: $($_.Exception.Message)" }
            if ($blocker -like 'remaining file*') {
                Write-Host "sweep: WARNING preserving pending removal for manual recovery; $blocker : $path"
                & $drop
                continue
            }
            if ($blocker) {
                Write-Host "sweep: pending removal is still blocked, retrying next sweep ($blocker): $path"
                continue
            }
        }
        elseif ($WhatIf) {
            Write-Host "sweep: WOULD forget completed pending removal of $path"
            continue
        }

        git -C $Repo worktree prune 2>$null | Out-Null
        & $drop
        $completed++
        Write-Host "sweep: finished pending removal of $path"
        $branch = $record.pendingBranch
        if ($branch) {
            . (Join-Path $PSScriptRoot 'MergedBranchCleanup.ps1')
            $null = Remove-MergedRemoteBranch -Repo $Repo -Branch $branch.Branch `
                -ExpectedHead $branch.ExpectedHead -RemoteUrlHash $branch.RemoteUrlHash
        }
    }
    return $completed
}
