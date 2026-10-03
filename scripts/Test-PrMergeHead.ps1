# Exercise the real merge wrapper and gate with isolated GitHub/Git responses.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "merge-head-$([guid]::NewGuid().ToString('N'))"
$pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $runner = Join-Path $testRoot 'runner.ps1'
    @'
param($Scripts, $DataPath, $PwshPath, [switch]$Gate, $ExpectedHead)
$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $DataPath -Raw | ConvertFrom-Json
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'pr' -and $args[1] -eq 'view') {
        $sha = if ($Gate) { $data.GateHead } else { $data.InitialHead }
        return @{
            headRefName = 'fixture'; headRefOid = $sha; isCrossRepository = $false
            state = 'OPEN'; mergeable = 'MERGEABLE'; mergeStateStatus = 'CLEAN'
            statusCheckRollup = @(@{ name = 'build'; status = 'COMPLETED'; conclusion = 'SUCCESS' })
            latestReviews = @(); commits = @(); author = @{ login = 'maintainer' }
        } | ConvertTo-Json -Depth 5 -Compress
    }
    if ($args[0] -eq 'api' -and $args[1] -eq 'graphql') {
        return '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}}}}}}'
    }
    if ($args[0] -eq 'api' -and $args[1] -like 'repos/*/issues/*/comments*') { return '[]' }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'merge') {
        $index = [Array]::IndexOf($args, '--match-head-commit')
        $expected = if ($index -ge 0) { $args[$index + 1] } else { '' }
        Set-Content -LiteralPath $data.Attempt -Value $expected
        if ($expected -and $expected -ne $data.MergeHead) { $global:LASTEXITCODE = 1; return }
        Set-Content -LiteralPath $data.Merged -Value $data.MergeHead
        return
    }
    throw "Unexpected GitHub command: $args"
}
function global:git {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'remote') { $global:LASTEXITCODE = 1; return }
    if ($args -contains 'worktree' -and $args -contains 'list') { return "worktree $PSScriptRoot" }
    if ($args -contains 'worktree' -and $args -contains 'prune') { return }
    throw "Unexpected Git command: $args"
}
function global:pwsh {
    $index = [Array]::IndexOf($args, '-ExpectedHead')
    $expected = if ($index -ge 0) { $args[$index + 1] } else { '' }
    & $PwshPath -NoProfile -File $PSCommandPath -Scripts $Scripts -DataPath $DataPath -PwshPath $PwshPath -Gate -ExpectedHead $expected
    $global:LASTEXITCODE = $LASTEXITCODE
}
if ($Gate) {
    $parameters = @{ Pr = 1; Repo = 'fixture/repo' }
    if ($ExpectedHead) { $parameters.ExpectedHead = $ExpectedHead }
    & (Join-Path $Scripts 'Assert-PrGreen.ps1') @parameters
} else {
    & (Join-Path $Scripts 'Merge-Pr.ps1') -Pr 1 -Repo 'fixture/repo'
}
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $runner
    $oldHead = 'a' * 40
    $newHead = 'b' * 40
    foreach ($scenario in @('changed-before-gate', 'changed-before-merge', 'unchanged')) {
        $dataPath = Join-Path $testRoot "$scenario.json"
        $merged = Join-Path $testRoot "$scenario.merged"
        $attempt = Join-Path $testRoot "$scenario.attempt"
        @{
            InitialHead = $oldHead
            GateHead = $(if ($scenario -eq 'changed-before-gate') { $newHead } else { $oldHead })
            MergeHead = $(if ($scenario -eq 'unchanged') { $oldHead } else { $newHead })
            Merged = $merged; Attempt = $attempt
        } | ConvertTo-Json | Set-Content -LiteralPath $dataPath
        & $pwshPath -NoProfile -File $runner -Scripts $PSScriptRoot -DataPath $dataPath -PwshPath $pwshPath
        if ($scenario -eq 'unchanged') {
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $merged)) { throw 'Unchanged reviewed head must merge.' }
            if ((Get-Content -LiteralPath $attempt -Raw).Trim() -ne $oldHead) { throw 'Merge must carry the reviewed SHA.' }
        } else {
            if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $merged)) { throw "$scenario merged an unreviewed head." }
            if ($scenario -eq 'changed-before-gate' -and (Test-Path -LiteralPath $attempt)) { throw 'Changed gate head reached merge.' }
        }
    }
    Write-Host 'OK unchanged head merges; changes before gate and before merge fail closed.'
} finally {
    $root = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $root.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
