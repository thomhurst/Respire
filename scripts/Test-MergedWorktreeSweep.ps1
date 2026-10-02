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
    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value 'bin/'
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
    $orphan = Join-Path $worktreeRoot 'pr-123-orphan'
    New-Item -ItemType Directory -Path $orphan | Out-Null
    Set-Content -LiteralPath (Join-Path $orphan '.git') -Value "gitdir: $(Join-Path $repo '.git/worktrees/missing-gitdir')"
    Set-Content -LiteralPath (Join-Path $orphan 'source.txt') -Value 'The deleted registration used the explicit held lock, not pr-123.'
    $preserved += @($raceAfterStatus, $raceBeforeRemoval, $raceAfterFailure, $orphan)
    $fixtures = Join-Path $testRoot 'fixtures.json'
    @{ Merged = $global:sweepTestMerged; Open = $global:sweepTestOpen; Association = $global:sweepTestAssociation
        AfterStatus = $raceAfterStatus; BeforeRemoval = $raceBeforeRemoval; AfterFailure = $raceAfterFailure } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $fixtures
    $runner = Join-Path $testRoot 'run-sweep.ps1'
    @'
param($Sweep, $Repo, $GitExecutable, $Fixtures)
$data = Get-Content -LiteralPath $Fixtures -Raw | ConvertFrom-Json
function global:git {
    if ($args -contains 'fetch') { $global:LASTEXITCODE = 0; return }
    if ($args -contains 'remove' -and $args -contains 'worktree') {
        $target = $args[-1]
        if (($target -replace '\\', '/') -eq ($data.BeforeRemoval -replace '\\', '/')) {
            Set-Content -LiteralPath (Join-Path $target 'source.txt') -Value 'Written just before Git removal.'
        }
        if (($target -replace '\\', '/') -eq ($data.AfterFailure -replace '\\', '/')) {
            Set-Content -LiteralPath (Join-Path $target 'new-source.txt') -Value 'Written during failed Git removal.'
            $global:LASTEXITCODE = 1
            return
        }
    }
    $output = & $GitExecutable @args
    $code = $LASTEXITCODE
    if ($args -contains 'status' -and ($args[1] -replace '\\', '/') -eq ($data.AfterStatus -replace '\\', '/')) {
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
    Assert ((Get-Content -LiteralPath (Join-Path $raceBeforeRemoval 'source.txt')) -eq 'Written just before Git removal.') 'Tracked edit was lost.'
    Assert (Test-Path -LiteralPath (Join-Path $orphan 'source.txt')) 'Orphan source was lost.'
    Assert (-not (Test-Path -LiteralPath $safe)) 'Exact completed PR tip was not removed.'
    Assert (-not (Test-Path -LiteralPath $released)) 'Released completed snapshot was not removed.'
    . (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
    Remove-MergedWorktree -Repo $repo -Worktree $unpublished -ExpectedHead $main
    Assert (Test-Path -LiteralPath $unpublished) 'Changed HEAD was removed by the shared helper.'
    Remove-MergedWorktree -Repo $repo -Worktree $locked -ExpectedHead $main
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
