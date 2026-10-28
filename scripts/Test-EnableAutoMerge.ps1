# Exercise Enable-AutoMerge.ps1 and Assert-PrGreen.ps1 -ForAutoMerge against isolated
# GitHub responses: arming must wait for nothing but the review, and must refuse drafts,
# failures, a missing or running review, a blocking review, and conflicts.
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "enable-auto-merge-$([guid]::NewGuid().ToString('N'))"
$pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $runner = Join-Path $testRoot 'runner.ps1'
    @'
param($Scripts, $DataPath, $PwshPath, [switch]$Gate, [string]$Mode = 'merge', $ExpectedHead)
$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $DataPath -Raw | ConvertFrom-Json
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'pr' -and $args[1] -eq 'view') {
        if ($args -contains 'headRefOid' -and $args -contains '--jq') { return $data.Head }
        return @{
            headRefOid = $data.Head; state = 'OPEN'; isDraft = $data.IsDraft
            mergeable = $data.Mergeable; mergeStateStatus = $data.MergeState
            statusCheckRollup = $data.Checks; latestReviews = @(); commits = @()
            author = @{ login = 'maintainer' }
        } | ConvertTo-Json -Depth 6 -Compress
    }
    if ($args[0] -eq 'api' -and $args[1] -eq 'graphql') {
        return '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[],"pageInfo":{"hasNextPage":false}}}}}}'
    }
    if ($args[0] -eq 'api' -and $args[1] -like 'repos/*/issues/*/comments*') {
        return @(@{ login = 'github-actions[bot]'; createdAt = '2026-01-01T00:00:00Z'; body = $data.ReviewBody } | ConvertTo-Json -Compress)
    }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'merge') {
        Set-Content -LiteralPath $data.Armed -Value ($args -join ' ')
        return
    }
    throw "Unexpected GitHub command: $args"
}
function global:pwsh {
    $index = [Array]::IndexOf($args, '-ExpectedHead')
    $expected = if ($index -ge 0) { $args[$index + 1] } else { '' }
    $mode = if ($args -contains '-ForAutoMerge') { 'auto' } else { 'merge' }
    & $PwshPath -NoProfile -File $PSCommandPath -Scripts $Scripts -DataPath $DataPath -PwshPath $PwshPath -Gate -ExpectedHead $expected -Mode $mode
    $global:LASTEXITCODE = $LASTEXITCODE
}
if ($Gate) {
    $parameters = @{ Pr = 1; Repo = 'fixture/repo'; ForAutoMerge = ($Mode -eq 'auto') }
    if ($ExpectedHead) { $parameters.ExpectedHead = $ExpectedHead }
    & (Join-Path $Scripts 'Assert-PrGreen.ps1') @parameters
} else {
    & (Join-Path $Scripts 'Enable-AutoMerge.ps1') -Pr 1 -Repo 'fixture/repo'
}
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $runner

    $head = 'c' * 40
    function New-Review([string]$Verdict) {
        "<!-- claude-code-review -->`n## Review`n`nLooks good.`n<!-- REVIEW_HEAD_SHA: $head -->`n<!-- REVIEW_VERDICT: $Verdict -->"
    }
    function New-Check([string]$Name, [string]$Status, [string]$Conclusion) {
        @{ name = $Name; status = $Status; conclusion = $Conclusion }
    }
    $reviewDone = New-Check 'claude-review' 'COMPLETED' 'SUCCESS'
    $ciPending = New-Check 'CI Passed' 'IN_PROGRESS' $null
    $base = @{ IsDraft = $false; Mergeable = 'MERGEABLE'; MergeState = 'BLOCKED'; Checks = @($reviewDone, $ciPending); Verdict = 'CLEAR' }

    $scenarios = [ordered]@{
        'pending CI arms'       = @{ Arms = $true }
        'draft'                 = @{ IsDraft = $true; MergeState = 'DRAFT' }
        'failed check'          = @{ Checks = @($reviewDone, $ciPending, (New-Check 'Writer invariants (net10.0)' 'COMPLETED' 'FAILURE')) }
        'review still running'  = @{ Checks = @((New-Check 'claude-review' 'IN_PROGRESS' $null), $ciPending) }
        'review check missing'  = @{ Checks = @($ciPending) }
        'blocking review'       = @{ Verdict = 'BLOCKING' }
        'conflicts'             = @{ Mergeable = 'CONFLICTING'; MergeState = 'DIRTY' }
    }
    foreach ($name in $scenarios.Keys) {
        $scenario = $base.Clone()
        foreach ($key in $scenarios[$name].Keys) { $scenario[$key] = $scenarios[$name][$key] }
        $slug = $name -replace '\W', '-'
        $armed = Join-Path $testRoot "$slug.armed"
        $dataPath = Join-Path $testRoot "$slug.json"
        @{
            Head = $head; IsDraft = $scenario.IsDraft; Mergeable = $scenario.Mergeable; MergeState = $scenario.MergeState
            Checks = $scenario.Checks; ReviewBody = (New-Review $scenario.Verdict); Armed = $armed
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $dataPath

        & $pwshPath -NoProfile -File $runner -Scripts $PSScriptRoot -DataPath $dataPath -PwshPath $pwshPath
        if ($scenario.Arms) {
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $armed)) { throw "$name must arm auto-merge." }
            $call = (Get-Content -LiteralPath $armed -Raw).Trim()
            foreach ($required in @('--auto', '--squash', "--match-head-commit $head")) {
                if (-not $call.Contains($required)) { throw "$name armed without '$required': $call" }
            }
        }
        elseif ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $armed)) {
            throw "$name must not arm auto-merge."
        }
    }

    # The plain merge gate still needs every check terminal: pending CI must not merge now.
    $plainData = Join-Path $testRoot 'plain.json'
    @{
        Head = $head; IsDraft = $false; Mergeable = 'MERGEABLE'; MergeState = 'CLEAN'
        Checks = @($reviewDone, $ciPending); ReviewBody = (New-Review 'CLEAR'); Armed = (Join-Path $testRoot 'plain.armed')
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $plainData
    & $pwshPath -NoProfile -File $runner -Scripts $PSScriptRoot -DataPath $plainData -PwshPath $pwshPath -Gate -ExpectedHead $head 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { throw 'The merge-now gate must deny pending checks.' }

    Write-Host "OK auto-merge arms on pending CI pinned to the head; refuses $(@($scenarios.Keys | Select-Object -Skip 1) -join ', '); merge-now gate denies pending CI."
} finally {
    $root = [IO.Path]::GetFullPath($testRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $root.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
