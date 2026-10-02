# Offline regression suite: real Git worktrees, deterministic GitHub and lock responses.
$ErrorActionPreference = 'Stop'
$sweep = Join-Path $PSScriptRoot 'Remove-MergedWorktrees.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "merged-sweep-$([Guid]::NewGuid().ToString('N'))"
$repo = Join-Path $testRoot 'repo'
$worktreeRoot = "$repo-worktrees"
$gitExecutable = (Get-Command git -CommandType Application | Select-Object -First 1).Source
$global:sweepTestMerged = @()
$global:sweepTestOpen = @()
$global:sweepTestAssociation = @()

function Invoke-TestGit {
    & $gitExecutable @args 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $args" }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function New-Checkout([string]$Name, [switch]$Detached) {
    $path = Join-Path $worktreeRoot $Name
    if ($Detached) { Invoke-TestGit -C $repo worktree add --detach $path HEAD }
    else { Invoke-TestGit -C $repo worktree add -b $Name $path HEAD }
    return $path
}
function Add-Commit([string]$Path) {
    Set-Content -LiteralPath (Join-Path $Path 'source.txt') -Value ([Guid]::NewGuid().ToString())
    $previousDate = $env:GIT_COMMITTER_DATE
    try {
        $env:GIT_COMMITTER_DATE = '2000-01-01T00:00:00Z'
        Invoke-TestGit -C $Path commit -am follow-up
    }
    finally { $env:GIT_COMMITTER_DATE = $previousDate }
}
function global:git {
    if ($args -contains 'fetch') { $global:LASTEXITCODE = 0; return }
    & $gitExecutable @args
}
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'repo') { return 'fixture/repo' }
    if ($args[0] -eq 'api') { return ConvertTo-Json -InputObject $global:sweepTestAssociation -Compress }
    if ($args -contains 'merged') { return ConvertTo-Json -InputObject $global:sweepTestMerged -Compress }
    if ($args -contains 'open') { return ConvertTo-Json -InputObject $global:sweepTestOpen -Compress }
    throw "Unexpected gh arguments: $args"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $repo 'scripts') -Force | Out-Null
    # Only the primary checkout owns this script. A branch-local copy must not be used.
    @'
param([string]$Verb, [string]$LockName, [string]$OwnerId)
if ($Verb -ne 'status') { throw 'Cleanup must only query ownership.' }
switch ($LockName) {
    'held' { 'HELD'; exit 0 }
    'mine' { 'HELD-BY-ME'; exit 0 }
    'unavailable' { exit 1 }
    'unexpected' { 'unknown'; exit 0 }
    'pr-125' { 'HELD'; exit 0 }
    default { 'FREE'; exit 0 }
}
'@ | Set-Content -LiteralPath (Join-Path $repo 'scripts/AgentLocks.ps1')
    Invoke-TestGit -C $repo init -b main
    Invoke-TestGit -C $repo config user.name 'Sweep Test'
    Invoke-TestGit -C $repo config user.email 'sweep@example.invalid'
    Invoke-TestGit -C $repo config extensions.worktreeConfig true
    Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value original
    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value @('bin/', '.env')
    Invoke-TestGit -C $repo add .
    Invoke-TestGit -C $repo commit -m fixture
    $main = & $gitExecutable -C $repo rev-parse HEAD
    Invoke-TestGit -C $repo update-ref refs/remotes/origin/main $main
    $safe = New-Checkout 'completed'
    Add-Commit $safe
    $mergedTip = & $gitExecutable -C $safe rev-parse HEAD
    $global:sweepTestMerged = @([pscustomobject]@{ number = 123; mergedAt = '2020-01-01T00:00:00Z'; headRefName = 'completed'; headRefOid = $mergedTip })
    $preserved = @()
    foreach ($state in @('held', 'mine', 'unavailable', 'unexpected')) {
        $path = New-Checkout "pr-123-$state"
        Invoke-TestGit -C $path config --worktree agent.lockName $state
        $preserved += $path
    }
    $unpublished = New-Checkout 'pr-123-follow-up'
    Add-Commit $unpublished
    $preserved += $unpublished
    $detached = New-Checkout 'pr-123-detached' -Detached
    Add-Commit $detached
    $preserved += $detached
    $reused = New-Checkout 'reused-branch'
    Add-Commit $reused
    $global:sweepTestMerged += [pscustomobject]@{ number = 124; mergedAt = '2020-01-01T00:00:00Z'; headRefName = 'reused-branch'; headRefOid = $main }
    $preserved += $reused
    $locked = New-Checkout 'pr-123-git-locked'
    Invoke-TestGit -C $repo worktree lock $locked
    $preserved += $locked
    $dirty = New-Checkout 'pr-123-dirty'
    Set-Content -LiteralPath (Join-Path $dirty 'source.txt') -Value dirty
    $preserved += $dirty
    $openPath = New-Checkout 'pr-123-open'
    $global:sweepTestOpen = @([pscustomobject]@{ headRefName = 'pr-123-open'; headRefOid = 'not-the-fixture-main' })
    $preserved += $openPath
    $unregistered = New-Checkout 'pr-125-before-registration'
    $preserved += $unregistered
    $released = New-Checkout 'pr-123-released'
    Invoke-TestGit -C $released config --worktree agent.lockName released
    New-Item -ItemType Directory -Path (Join-Path $released 'bin') | Out-Null
    Set-Content -LiteralPath (Join-Path $released 'bin/generated.dll') -Value generated
    $raceAfterStatus = New-Checkout 'pr-123-after-status'
    $raceBeforeRemoval = New-Checkout 'pr-123-before-removal'
    $raceAfterFailure = New-Checkout 'pr-123-after-failure'
    $raceBranch = New-Checkout 'pr-123-branch-race'
    $raceCheckout = New-Checkout 'pr-123-checkout-race'
    $replacementCheckout = Join-Path $worktreeRoot 'replacement-checkout'
    $raceIgnored = New-Checkout 'pr-123-ignored-race'
    $raceOldPath = New-Checkout 'pr-123-old-path-race'
    $generated = New-Checkout 'pr-123-generated'
    New-Item -ItemType Directory -Path (Join-Path $generated 'Debug') | Out-Null
    Set-Content -LiteralPath (Join-Path $generated 'Debug/generated.dll') -Value generated
    foreach ($path in @($safe, $raceBeforeRemoval, $raceAfterFailure, $raceBranch, $raceCheckout, $raceIgnored, $raceOldPath)) {
        New-Item -ItemType Directory -Path (Join-Path $path 'bin') | Out-Null
        Set-Content -LiteralPath (Join-Path $path 'bin/generated.dll') -Value generated
    }
    $replacementHead = & $gitExecutable -C $unpublished rev-parse HEAD
    $orphan = Join-Path $worktreeRoot 'pr-123-orphan'
    New-Item -ItemType Directory -Path $orphan | Out-Null
    Set-Content -LiteralPath (Join-Path $orphan '.git') -Value "gitdir: $(Join-Path $repo '.git/worktrees/missing-gitdir')"
    Set-Content -LiteralPath (Join-Path $orphan 'source.txt') -Value 'The deleted registration used the explicit held lock, not pr-123.'
    $preserved += @($raceAfterStatus, $raceBeforeRemoval, $raceAfterFailure, $raceIgnored, $raceOldPath, $orphan)
    $fixtures = Join-Path $testRoot 'fixtures.json'
    @{ Merged = $global:sweepTestMerged; Open = $global:sweepTestOpen; Association = $global:sweepTestAssociation
        AfterStatus = $raceAfterStatus; BeforeRemoval = $raceBeforeRemoval; AfterFailure = $raceAfterFailure
        RaceBranch = $raceBranch; ReplacementHead = $replacementHead; RaceCheckout = $raceCheckout
        ReplacementCheckout = $replacementCheckout; RaceIgnored = $raceIgnored; RaceOldPath = $raceOldPath } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixtures
    $runner = Join-Path $testRoot 'run-sweep.ps1'
    @'
param($Sweep, $Repo, $GitExecutable, $Fixtures)
$data = Get-Content -LiteralPath $Fixtures -Raw | ConvertFrom-Json
function global:git {
    if ($args -contains 'fetch') { $global:LASTEXITCODE = 0; return }
    if ($args -contains 'remove' -and $args -contains 'worktree') {
        throw 'Unattended cleanup must not recursively remove checkout contents.'
    }
    $target = $args[1] -replace '\\', '/'
    if ($args -contains 'clean') {
        if ($target -eq ($data.BeforeRemoval -replace '\\', '/')) {
            Set-Content -LiteralPath (Join-Path $args[1] 'source.txt') -Value 'Written just before artifact cleanup.'
        }
        if ($target -eq ($data.AfterFailure -replace '\\', '/')) {
            Set-Content -LiteralPath (Join-Path $args[1] 'new-source.txt') -Value 'Written during failed artifact cleanup.'
            $global:LASTEXITCODE = 1
            return
        }
        if ($target -eq ($data.RaceIgnored -replace '\\', '/')) {
            # The child inherits an already-open cwd, just like a background worker.
            # Unattended cleanup must preserve its ignored file even after inspection.
            $process = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -CommandType Application | Select-Object -First 1).Source)
            $process.WorkingDirectory = $args[1]
            $process.ArgumentList.Add('-NoProfile')
            $process.ArgumentList.Add('-Command')
            $process.ArgumentList.Add('Set-Content -LiteralPath .env -Value "Ignored file written through cwd after inspection."')
            $child = [Diagnostics.Process]::Start($process)
            $child.WaitForExit()
            if ($child.ExitCode -ne 0) { throw 'Could not inject background cwd write.' }
            $child.Dispose()
        }
        if ($target -eq ($data.RaceOldPath -replace '\\', '/')) {
            Set-Content -LiteralPath (Join-Path $args[1] '.env') -Value 'Ignored file written at the original path.'
        }
    }
    $output = & $GitExecutable @args
    $code = $LASTEXITCODE
    if ($code -eq 0 -and $args -contains 'clean' -and $target -eq ($data.RaceBranch -replace '\\', '/')) {
        & $GitExecutable -C $Repo update-ref refs/heads/pr-123-branch-race $data.ReplacementHead
        if ($LASTEXITCODE -ne 0) { throw 'Could not inject branch advancement.' }
    }
    if ($code -eq 0 -and $args -contains 'clean' -and $target -eq ($data.RaceCheckout -replace '\\', '/')) {
        & $GitExecutable -C $Repo worktree add --force $data.ReplacementCheckout pr-123-checkout-race 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not inject same-commit branch checkout.' }
    }
    if ($args -contains 'status' -and $target -eq ($data.AfterStatus -replace '\\', '/')) {
        Set-Content -LiteralPath (Join-Path $args[1] 'new-source.txt') -Value 'Written after status was read.'
    }
    $global:LASTEXITCODE = $code
    $output
}
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'repo') { return 'fixture/repo' }
    if ($args[0] -eq 'api') { return ConvertTo-Json -InputObject @($data.Association) -Compress }
    if ($args -contains 'merged') { return ConvertTo-Json -InputObject @($data.Merged) -Compress }
    if ($args -contains 'open') { return ConvertTo-Json -InputObject @($data.Open) -Compress }
    throw "Unexpected gh arguments: $args"
}
Set-Location -LiteralPath $Repo
& $Sweep -StaleDays 1
'@ | Set-Content -LiteralPath $runner
    & pwsh -NoProfile -File $runner $sweep $repo $gitExecutable $fixtures
    Assert ($LASTEXITCODE -eq 0) 'Sweep child process failed.'
    foreach ($path in $preserved) { Assert (Test-Path -LiteralPath $path) "Unsafe removal: $path" }
    foreach ($path in @($raceAfterStatus, $raceAfterFailure)) {
        Assert (Test-Path -LiteralPath (Join-Path $path 'new-source.txt')) "New source was lost: $path"
    }
    Assert ((Get-Content -LiteralPath (Join-Path $raceBeforeRemoval 'source.txt')) -eq 'Written just before artifact cleanup.') 'Tracked edit was lost.'
    Assert (Test-Path -LiteralPath (Join-Path $orphan 'source.txt')) 'Orphan source was lost.'
    Assert ((Get-Content -LiteralPath (Join-Path $raceIgnored '.env')) -eq 'Ignored file written through cwd after inspection.') 'Late background cwd write was lost.'
    Assert ((Get-Content -LiteralPath (Join-Path $raceOldPath '.env')) -eq 'Ignored file written at the original path.') 'Recreated path was removed.'
    foreach ($path in @($safe, $released, $raceBranch, $generated)) {
        Assert (Test-Path -LiteralPath (Join-Path $path 'source.txt')) "Completed checkout source was removed: $path"
        Assert (-not (Test-Path -LiteralPath (Join-Path $path 'bin/generated.dll'))) "Generated output survived cleanup: $path"
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $generated 'Debug/generated.dll'))) 'Unignored generated output survived cleanup.'
    $checkoutHead = & $gitExecutable -C $replacementCheckout rev-parse HEAD
    Assert ($LASTEXITCODE -eq 0 -and $checkoutHead -eq $main) 'Same-commit checkout lost its branch ref.'
    $branchHead = & $gitExecutable -C $repo rev-parse --verify refs/heads/pr-123-branch-race
    Assert ($LASTEXITCODE -eq 0 -and $branchHead -eq $replacementHead) 'Concurrent branch advancement was deleted.'
    & $gitExecutable -C $repo show-ref --verify --quiet refs/heads/completed
    Assert ($LASTEXITCODE -eq 0) 'Completed local branch recovery ref was deleted.'
    . (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
    Clear-CompletedWorktreeArtifacts -Repo $repo -Worktree $unpublished -ExpectedHead $main
    Assert (Test-Path -LiteralPath $unpublished) 'Changed HEAD was removed by the shared helper.'
    Clear-CompletedWorktreeArtifacts -Repo $repo -Worktree $locked -ExpectedHead $main
    Assert (Test-Path -LiteralPath $locked) 'Git lock was bypassed by the shared helper.'
    Write-Host 'OK merged worktree sweep preserves active and unpublished work.'
}
finally {
    Remove-Item -LiteralPath Function:\git, Function:\gh -ErrorAction SilentlyContinue
    Remove-Variable -Name sweepTestMerged, sweepTestOpen, sweepTestAssociation -Scope Global -ErrorAction SilentlyContinue
    # The fixture root is uniquely created above; never operate on repository worktrees.
    if (-not ([IO.Path]::GetFullPath($testRoot)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
