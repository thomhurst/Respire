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
    Push-Location $repo
    try { & $sweep -StaleDays 1 } finally { Pop-Location }
    foreach ($path in $preserved) { Assert (Test-Path -LiteralPath $path) "Unsafe removal: $path" }
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
