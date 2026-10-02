# Real Git lifecycle coverage; GitHub and the merge gate are stubbed only inside
# this disposable fixture. No repository or service outside the fixture is mutated.
$ErrorActionPreference = 'Stop'
$originalCurrentDirectory = [Environment]::CurrentDirectory
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "merge-lifecycle-$([guid]::NewGuid().ToString('N'))"
$repo = Join-Path $testRoot 'repo'
$remote = Join-Path $testRoot 'remote.git'
$fixtureScripts = Join-Path $testRoot 'scripts'
function Invoke-TestGit {
    & git @args 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git command failed: $args" }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    New-Item -ItemType Directory -Path $fixtureScripts -Force | Out-Null
    foreach ($name in @('Merge-Pr', 'MergedBranchCleanup', 'WorktreeCleanup')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "$name.ps1") -Destination $fixtureScripts
    }
    Set-Content -LiteralPath (Join-Path $fixtureScripts 'Assert-PrGreen.ps1') -Value 'exit 0'
    $runner = Join-Path $testRoot 'run-merge.ps1'
    @'
param($MergeScript, $RepoPath, $DataPath, $Worktree)
$data = Get-Content -LiteralPath $DataPath -Raw | ConvertFrom-Json
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'pr' -and $args[1] -eq 'view') { return $data.Head | ConvertTo-Json -Compress }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'merge') {
        Set-Content -LiteralPath $data.MergedMarker -Value merged
        return
    }
    throw "Unexpected GitHub command: $args"
}
Set-Location -LiteralPath $RepoPath
$parameters = @{ Pr = 1; Repo = 'fixture/repo' }
if ($Worktree) { $parameters.Worktree = $Worktree }
& $MergeScript @parameters
'@ | Set-Content -LiteralPath $runner
    Invoke-TestGit init -b main $repo
    Invoke-TestGit -C $repo config user.name 'Merge Lifecycle Test'
    Invoke-TestGit -C $repo config user.email 'merge@example.invalid'
    Invoke-TestGit -C $repo config extensions.worktreeConfig true
    Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value committed
    Invoke-TestGit -C $repo add .
    Invoke-TestGit -C $repo commit -m fixture
    Invoke-TestGit init --bare $remote
    Invoke-TestGit -C $repo remote add origin $remote
    $head = & git -C $repo rev-parse HEAD
    . (Join-Path $PSScriptRoot 'MergedBranchCleanup.ps1')
    . (Join-Path $PSScriptRoot 'Remove-ReleasedWorktree.ps1')
    Push-Location -LiteralPath $repo
    try {
        foreach ($kind in @('retained', 'fork', 'multiple-push-urls', 'mismatched', 'malformed', 'incomplete', 'no-checkout')) {
            $worktree = $null
            Invoke-TestGit -C $repo branch $kind HEAD
            Invoke-TestGit -C $repo push origin "${kind}:refs/heads/$kind"
            if ($kind -ne 'no-checkout') {
                $worktree = Join-Path $testRoot $kind
                Invoke-TestGit -C $repo worktree add $worktree $kind
                Invoke-TestGit -C $worktree config --worktree agent.lockName fixture-owner
            }
            if ($kind -eq 'multiple-push-urls') {
                Invoke-TestGit -C $repo config --add remote.origin.pushurl $remote
                Invoke-TestGit -C $repo config --add remote.origin.pushurl (Join-Path $testRoot 'other-remote.git')
            }
            $prBranch = $kind
            if ($kind -eq 'mismatched') {
                $prBranch = 'actual-pr-branch'
                Invoke-TestGit -C $repo branch $prBranch HEAD
                Invoke-TestGit -C $repo push origin "${prBranch}:refs/heads/$prBranch"
                Invoke-TestGit -C $repo worktree add (Join-Path $testRoot 'actual-pr-checkout') $prBranch
            }
            $mergedMarker = Join-Path $testRoot "$kind-merged.txt"
            $dataPath = Join-Path $testRoot "$kind.json"
            @{ Head = @{ headRefName = $prBranch; headRefOid = $head; isCrossRepository = ($kind -eq 'fork') }
                MergedMarker = $mergedMarker } | ConvertTo-Json | Set-Content -LiteralPath $dataPath
            & pwsh -NoProfile -File $runner (Join-Path $fixtureScripts 'Merge-Pr.ps1') $repo $dataPath $worktree
            Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $mergedMarker)) 'Fixture merge failed.'
            if ($worktree) {
                Assert (Test-Path -LiteralPath (Join-Path $worktree 'source.txt')) 'Merge removed checkout contents.'
                $remoteHead = & git -C $repo ls-remote $remote "refs/heads/$prBranch"
                Assert ([bool]$remoteHead) 'Remote branch was deleted before owner release.'
                $gitDirectory = & git -C $worktree rev-parse --absolute-git-dir
                $marker = Join-Path $gitDirectory 'respire-merged-branch.json'
                Assert ((Test-Path -LiteralPath $marker) -eq ($kind -in @('retained', 'malformed', 'incomplete'))) 'Unexpected deferred cleanup marker.'
                if ($kind -eq 'malformed') { Set-Content -LiteralPath $marker -Value '{' }
                if ($kind -eq 'incomplete') { Set-Content -LiteralPath $marker -Value '{}' }
                if ($kind -eq 'retained') {
                    $originalMarker = Get-Content -LiteralPath $marker -Raw
                    $metadata = $originalMarker | ConvertFrom-Json
                    Assert ($metadata.ExpectedHead -eq $head -and $metadata.Branch -eq $kind) 'Wrong deferred branch identity.'
                    $syntheticUrl = 'https://synthetic-user:synthetic-secret@example.invalid/repo.git'
                    Set-MergedBranchCleanup -Worktree $worktree -Branch $kind -ExpectedHead $head -RemoteUrl $syntheticUrl
                    $secretMarker = Get-Content -LiteralPath $marker -Raw
                    $secretMetadata = $secretMarker | ConvertFrom-Json
                    Assert (-not $secretMarker.Contains('synthetic-') -and
                        $secretMetadata.RemoteUrlHash -eq (Get-RemoteUrlFingerprint $syntheticUrl)) 'Remote credentials leaked into cleanup metadata.'
                    Set-Content -LiteralPath $marker -Value $originalMarker
                }
                Remove-ReleasedWorktree -Worktree $worktree -LockName fixture-owner
                Assert (-not (Test-Path -LiteralPath $worktree)) 'Owner release did not remove the checkout.'
            }
            $remoteHead = & git -C $repo ls-remote $remote "refs/heads/$prBranch"
            Assert ([bool]$remoteHead -eq ($kind -in @('fork', 'multiple-push-urls', 'mismatched', 'malformed', 'incomplete'))) 'Incorrect remote branch lifecycle.'
            Invoke-TestGit -C $repo show-ref --verify "refs/heads/$kind"
            if ($kind -eq 'multiple-push-urls') { Invoke-TestGit -C $repo config --unset-all remote.origin.pushurl }
        }
    } finally { Pop-Location }
    Write-Host 'OK merge/release lifecycle, fork/multiple-URL/mismatched-checkout preservation, invalid metadata, and credential-free markers.'
} finally {
    [Environment]::CurrentDirectory = $originalCurrentDirectory
    $root = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $root.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
