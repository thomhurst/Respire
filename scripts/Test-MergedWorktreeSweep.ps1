# Offline regression suite: runs the real sweep in-process against real Git worktrees, with
# deterministic GitHub responses and a fake canonical lock CLI. No shared Redis key is used.
$ErrorActionPreference = 'Stop'
$sweep = Join-Path $PSScriptRoot 'Remove-MergedWorktrees.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "merged-sweep-$([Guid]::NewGuid().ToString('N'))"
$repo = Join-Path $testRoot 'repo'
$worktreeRoot = "$repo-worktrees"
$elsewhere = Join-Path $testRoot 'elsewhere'
$lockState = Join-Path $testRoot 'locks.json'
$lockCalls = Join-Path $testRoot 'lock-calls.txt'
$gitExecutable = (Get-Command git -CommandType Application | Select-Object -First 1).Source
# The sweep resolves these mocks from its own script scope, so fixture state must be
# global: a $script: variable here would not be visible to it.
$global:sweepTestGit = $gitExecutable
$global:sweepTestMerged = @()
$global:sweepTestOpen = @()
$global:sweepTestAssociation = @()
$global:sweepTestAfterRemove = @{}
$holder = $null

function Invoke-TestGit {
    & $gitExecutable @args 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $args" }
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function New-Checkout([string]$Name, [switch]$Detached, [string]$Branch, [string]$Root = $worktreeRoot, [string]$Marker) {
    $path = Join-Path $Root $Name
    if (-not $Branch) { $Branch = $Name }
    if ($Detached) { Invoke-TestGit -C $repo worktree add --detach $path origin/main }
    else { Invoke-TestGit -C $repo worktree add -b $Branch $path origin/main }
    if ($Marker) {
        Invoke-TestGit -C $path config --worktree agent.lockName $Marker
        Invoke-TestGit -C $path config --worktree agent.lockWorktree $path
    }
    return $path
}
function Add-Commit([string]$Path) {
    Set-Content -LiteralPath (Join-Path $Path 'source.txt') -Value ([Guid]::NewGuid().ToString())
    Invoke-TestGit -C $Path commit -am follow-up
    return (& $gitExecutable -C $Path rev-parse HEAD).Trim()
}
# Commits once and records the tip as the merged head of PR $Number.
function New-MergedCheckout([string]$Name, [int]$Number, [switch]$Detached, [string]$Branch, [string]$Root = $worktreeRoot, [string]$Marker) {
    $path = New-Checkout $Name -Detached:$Detached -Branch $Branch -Root $Root -Marker $Marker
    $tip = Add-Commit $path
    $headRef = if ($Branch) { $Branch } else { $Name }
    $global:sweepTestMerged += [pscustomobject]@{ number = $Number; mergedAt = '2020-01-01T00:00:00Z'; headRefName = $headRef; headRefOid = $tip }
    return $path
}
function Set-Lock([string]$Name, [string]$State) {
    $states = Get-Content -LiteralPath $lockState -Raw | ConvertFrom-Json -AsHashtable
    $states[$Name] = $State
    $states | ConvertTo-Json | Set-Content -LiteralPath $lockState
}
function Get-Lock([string]$Name) {
    $states = Get-Content -LiteralPath $lockState -Raw | ConvertFrom-Json -AsHashtable
    if ($states.ContainsKey($Name)) { return $states[$Name] }
    return 'FREE'
}
function Test-Branch([string]$Name) {
    & $gitExecutable -C $repo show-ref --verify --quiet "refs/heads/$Name"
    return $LASTEXITCODE -eq 0
}
function Invoke-Sweep([switch]$Preview) {
    Set-Content -LiteralPath $lockCalls -Value $null
    $output = @(& $sweep -StaleDays 1 -WhatIf:$Preview 6>&1 2>&1)
    Assert ($LASTEXITCODE -eq 0) 'The sweep did not exit 0.'
    $script:sweepOutput = $output -join "`n"
    Write-Host "--- sweep$(if ($Preview) { ' -WhatIf' }) ---`n$($script:sweepOutput)"
    Assert ($script:sweepOutput -notmatch 'TOKEN-SHOULD-NOT-LEAK') 'The sweep printed a lock token.'
    $script:calls = @(Get-Content -LiteralPath $lockCalls | Where-Object { $_ })
}
function global:git {
    if ($args -contains 'fetch') { $global:LASTEXITCODE = 0; return }
    $output = & $global:sweepTestGit @args
    $code = $LASTEXITCODE
    if ($args -contains 'worktree' -and $args -contains 'remove') {
        $target = ([string]$args[-1] -replace '\\', '/').TrimEnd('/')
        $inject = $global:sweepTestAfterRemove[$target]
        if ($inject) { & $inject }
    }
    $global:LASTEXITCODE = $code
    $output
}
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'repo') { return 'fixture/repo' }
    if ($args[0] -eq 'api') { return ConvertTo-Json -InputObject @($global:sweepTestAssociation) -Compress }
    if ($args -contains 'merged') { return ConvertTo-Json -InputObject @($global:sweepTestMerged) -Compress }
    if ($args -contains 'open') { return ConvertTo-Json -InputObject @($global:sweepTestOpen) -Compress }
    throw "Unexpected gh arguments: $args"
}

try {
    New-Item -ItemType Directory -Path (Join-Path $repo 'scripts'), $worktreeRoot, $elsewhere -Force | Out-Null
    # Only the primary checkout owns this script. A branch-local copy must not be used.
    @'
param([Parameter(Position = 0)][string]$Verb, [string]$LockName, [string]$OwnerId)
$statePath = Join-Path $PSScriptRoot '../../locks.json'
$states = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
$state = if ($states.ContainsKey($LockName)) { $states[$LockName] } else { 'FREE' }
Add-Content -LiteralPath (Join-Path $PSScriptRoot '../../lock-calls.txt') -Value "$Verb $LockName"
if ($state -eq 'ERROR') { exit 1 }
switch ($Verb) {
    'status' { if ($state -eq 'HELD' -or $state -like 'OWNED:*') { 'HELD' } else { 'FREE' } }
    'acquire' {
        if ($state -ne 'FREE') { [Console]::Error.WriteLine('HELD'); exit 3 }
        $states[$LockName] = "OWNED:$OwnerId"
        $states | ConvertTo-Json | Set-Content -LiteralPath $statePath
        'TOKEN-SHOULD-NOT-LEAK'
    }
    'release' {
        if ($state -ne "OWNED:$OwnerId") { [Console]::Error.WriteLine('STALE'); exit 5 }
        $states[$LockName] = 'FREE'
        $states | ConvertTo-Json | Set-Content -LiteralPath $statePath
    }
    default { exit 1 }
}
exit 0
'@ | Set-Content -LiteralPath (Join-Path $repo 'scripts/AgentLocks.ps1')
    '{}' | Set-Content -LiteralPath $lockState
    Invoke-TestGit -C $repo init -b main
    Invoke-TestGit -C $repo config user.name 'Sweep Test'
    Invoke-TestGit -C $repo config user.email 'sweep@example.invalid'
    Invoke-TestGit -C $repo config extensions.worktreeConfig true
    Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value original
    Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value @('bin/', '.env', 'scripts/')
    Invoke-TestGit -C $repo add .
    $previousDate = $env:GIT_COMMITTER_DATE
    try { $env:GIT_COMMITTER_DATE = '2000-01-01T00:00:00Z'; Invoke-TestGit -C $repo commit -m 'old main' }
    finally { $env:GIT_COMMITTER_DATE = $previousDate }
    $oldMain = (& $gitExecutable -C $repo rev-parse HEAD).Trim()
    Invoke-TestGit -C $repo commit --allow-empty -m 'current main'
    $main = (& $gitExecutable -C $repo rev-parse HEAD).Trim()
    Invoke-TestGit -C $repo update-ref refs/remotes/origin/main $main
    # A rebase merge leaves main's tip equal to the merged PR head.
    $global:sweepTestMerged += [pscustomobject]@{ number = 199; mergedAt = '2020-01-01T00:00:00Z'; headRefName = 'rebased'; headRefOid = $main }
    Push-Location -LiteralPath $repo

    # --- Checkouts the sweep must remove. ---------------------------------------------
    $released = New-MergedCheckout 'pr-201-released' 201 -Marker 'pr-201'
    New-Item -ItemType Directory -Path (Join-Path $released 'bin') | Out-Null
    Set-Content -LiteralPath (Join-Path $released 'bin/generated.dll') -Value generated
    $adHoc = New-MergedCheckout 'ad-hoc-checkout' 213 -Branch 'feature/ad-hoc' -Root $elsewhere
    Set-Lock 'pr-213' 'HELD'
    $branchRace = New-MergedCheckout 'pr-215-branch-race' 215
    $checkoutRace = New-MergedCheckout 'pr-216-checkout-race' 216
    $replacementCheckout = Join-Path $testRoot 'replacement-checkout'
    $global:sweepTestAfterRemove[($branchRace -replace '\\', '/')] = {
        & $global:sweepTestGit -C $repo update-ref refs/heads/pr-215-branch-race $oldMain
    }.GetNewClosure()
    $global:sweepTestAfterRemove[($checkoutRace -replace '\\', '/')] = {
        & $global:sweepTestGit -C $repo worktree add --force $replacementCheckout pr-216-checkout-race 2>$null | Out-Null
    }.GetNewClosure()
    $setup = New-MergedCheckout 'pr-218-stale-setup' 218 -Detached
    Invoke-TestGit -C $repo worktree lock --reason 'PR checkout setup' $setup
    $setupLockFile = Join-Path ((& $gitExecutable -C $setup rev-parse --absolute-git-dir).Trim()) 'locked'
    (Get-Item -LiteralPath $setupLockFile).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-3)
    $staleSnapshot = New-Checkout 'pr-222-old-snapshot' -Detached
    Invoke-TestGit -C $staleSnapshot checkout --quiet --detach $oldMain
    $outside = Join-Path $testRoot 'outside-store'
    if ($IsWindows) {
        New-Item -ItemType Directory -Path $outside | Out-Null
        Set-Content -LiteralPath (Join-Path $outside 'keep.txt') -Value 'outside the worktree'
        $junction = New-MergedCheckout 'pr-212-junction' 212
        New-Item -ItemType Directory -Path (Join-Path $junction 'bin') | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $junction 'bin/pkg') -Target $outside | Out-Null
    }

    # --- Checkouts the sweep must preserve. -------------------------------------------
    $preserved = [ordered]@{}
    foreach ($state in @('HELD', 'ERROR', 'RACE')) {
        $preserved["$state ownership"] = New-MergedCheckout "pr-202-$($state.ToLowerInvariant())" 202 -Marker "lock-$state"
        Set-Lock "lock-$state" $state
    }
    $preserved['second identity held'] = New-MergedCheckout 'pr-203-multiple' 203 -Marker 'issue-203'
    Set-Lock 'pr-203' 'HELD'
    $preserved['tracked change'] = New-MergedCheckout 'pr-204-dirty' 204
    Set-Content -LiteralPath (Join-Path $preserved['tracked change'] 'source.txt') -Value dirty
    $preserved['unknown ignored file'] = New-MergedCheckout 'pr-205-ignored' 205
    Set-Content -LiteralPath (Join-Path $preserved['unknown ignored file'] '.env') -Value 'local settings'
    $preserved['untracked source'] = New-MergedCheckout 'pr-206-untracked' 206
    Set-Content -LiteralPath (Join-Path $preserved['untracked source'] 'NewFeature.cs') -Value 'source'
    $preserved['open PR branch'] = New-MergedCheckout 'pr-207-open' 207
    $preserved['open PR tip'] = New-MergedCheckout 'pr-208-open-tip' 208 -Detached
    $openTip = (& $gitExecutable -C $preserved['open PR tip'] rev-parse HEAD).Trim()
    $global:sweepTestOpen = @([pscustomobject]@{ headRefName = 'pr-207-open'; headRefOid = 'not-the-fixture-tip' }
        [pscustomobject]@{ headRefName = 'unrelated'; headRefOid = $openTip })
    $preserved['fresh branch on a merged main tip'] = New-Checkout 'issue-209-fresh'
    $preserved['fresh branch behind main, older than StaleDays'] = New-Checkout 'issue-211-behind'
    Invoke-TestGit -C $preserved['fresh branch behind main, older than StaleDays'] reset --quiet --hard $oldMain
    $preserved['unpublished follow-up'] = New-MergedCheckout 'pr-219-follow-up' 219
    $null = Add-Commit $preserved['unpublished follow-up']
    $preserved['git-locked'] = New-MergedCheckout 'pr-217-locked' 217
    Invoke-TestGit -C $repo worktree lock --reason 'keep for manual recovery' $preserved['git-locked']
    $preserved['recent setup lock'] = New-MergedCheckout 'pr-221-recent-setup' 221 -Detached
    Invoke-TestGit -C $repo worktree lock --reason 'PR checkout setup' $preserved['recent setup lock']
    $preserved['no recoverable identity'] = New-MergedCheckout 'renamed-detached' 220 -Detached -Root $elsewhere
    $preserved['harness-managed'] = Join-Path $repo '.claude/worktrees/harness'
    Invoke-TestGit -C $repo worktree add -b pr-223-harness $preserved['harness-managed'] origin/main
    $null = Add-Commit $preserved['harness-managed']
    $global:sweepTestMerged += [pscustomobject]@{ number = 223; mergedAt = '2020-01-01T00:00:00Z'; headRefName = 'pr-223-harness'
        headRefOid = (& $gitExecutable -C $preserved['harness-managed'] rev-parse HEAD).Trim() }
    $orphan = Join-Path $worktreeRoot 'pr-201-orphan'
    New-Item -ItemType Directory -Path $orphan | Out-Null
    Set-Content -LiteralPath (Join-Path $orphan '.git') -Value "gitdir: $(Join-Path $repo '.git/worktrees/missing-gitdir')"
    Set-Content -LiteralPath (Join-Path $orphan 'source.txt') -Value 'The deleted registration may have used another lock.'
    $preserved['orphan with lost identity'] = $orphan

    # --- -WhatIf reads lock status only and deletes nothing. ---------------------------
    Invoke-Sweep -Preview
    foreach ($path in @($released, $adHoc, $branchRace, $checkoutRace, $setup, $staleSnapshot) + @($preserved.Values)) {
        Assert (Test-Path -LiteralPath $path) "WhatIf removed $path"
    }
    Assert (Test-Path -LiteralPath (Join-Path $released 'bin/generated.dll')) 'WhatIf cleared generated output.'
    Assert (Test-Branch 'pr-201-released') 'WhatIf deleted a branch.'
    Assert (@($script:calls | Where-Object { $_ -notlike 'status *' }).Count -eq 0) "WhatIf acquired or released a lock: $($script:calls -join '; ')"
    Assert ($script:sweepOutput -match 'WOULD remove \S*pr-201-released') 'WhatIf did not report the removable checkout.'
    Assert (Test-Path -LiteralPath $setupLockFile) 'WhatIf unlocked a setup lock.'

    # --- Real sweep. -------------------------------------------------------------------
    Invoke-Sweep
    Assert (-not (Test-Path -LiteralPath $released)) 'A merged, released checkout was not removed.'
    Assert (-not (Test-Branch 'pr-201-released')) 'The merged PR branch was not deleted with its checkout.'
    Assert ((& $gitExecutable -C $repo worktree list --porcelain) -notcontains "worktree $($released -replace '\\', '/')") 'The removed checkout is still registered.'
    Assert ([bool]($script:calls -match '^acquire pr-201$')) 'Removal did not reserve the item lock.'
    Assert ([bool]($script:calls -match '^release pr-201$')) 'Removal did not release its reservation.'
    Assert ((Get-Lock 'pr-201') -eq 'FREE') 'Removal leaked a reservation.'
    Assert (-not (Test-Path -LiteralPath $setup)) 'A stale setup lock was not unlocked for removal.'
    Assert (-not (Test-Path -LiteralPath $staleSnapshot)) 'A canonical detached snapshot reachable from origin/main was not removed.'
    if ($IsWindows) {
        Assert (-not (Test-Path -LiteralPath $junction)) 'A merged checkout containing a junction was not removed.'
        Assert (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) 'Removal deleted content through a junction.'
    }
    Assert (-not (Test-Path -LiteralPath $branchRace)) 'Branch-race checkout was not removed.'
    $branchHead = & $gitExecutable -C $repo rev-parse --verify refs/heads/pr-215-branch-race
    Assert ($LASTEXITCODE -eq 0 -and $branchHead -eq $oldMain) 'A branch that moved after removal was deleted.'
    Assert (-not (Test-Path -LiteralPath $checkoutRace)) 'Checkout-race checkout was not removed.'
    Assert ((& $gitExecutable -C $replacementCheckout symbolic-ref HEAD) -eq 'refs/heads/pr-216-checkout-race') 'A branch checked out again after removal was deleted.'

    # A merged named branch at a non-canonical path takes its PR's identity.
    Assert (Test-Path -LiteralPath $adHoc) 'A merged branch checkout was removed while its PR lock was held.'
    Assert (Test-Branch 'feature/ad-hoc') 'The branch of a preserved checkout was deleted.'

    foreach ($entry in $preserved.GetEnumerator()) {
        Assert (Test-Path -LiteralPath $entry.Value) "Unsafe removal ($($entry.Key)): $($entry.Value)"
    }
    Assert ((Get-Content -LiteralPath (Join-Path $preserved['tracked change'] 'source.txt')) -eq 'dirty') 'A tracked edit was lost.'
    Assert (Test-Path -LiteralPath (Join-Path $preserved['unknown ignored file'] '.env')) 'An unknown ignored file was lost.'
    Assert (Test-Path -LiteralPath (Join-Path $orphan 'source.txt')) 'Orphan source was lost.'
    Assert ((Get-Lock 'issue-203') -eq 'FREE') 'A partial reservation leaked.'
    foreach ($name in @('pr-204', 'pr-205', 'pr-206')) {
        Assert ((Get-Lock $name) -eq 'FREE') "Preserving dirty work leaked the $name reservation."
    }
    foreach ($name in @('lock-HELD', 'lock-RACE', 'pr-203')) {
        Assert ([bool]($script:calls -match "^acquire $name$")) "Ownership of $name was checked without reserving it."
    }
    Assert (-not [bool]($script:calls -match '^acquire pr-202$')) 'Cleanup continued after a held identity.'

    Set-Lock 'pr-213' 'FREE'
    Invoke-Sweep
    Assert (-not (Test-Path -LiteralPath $adHoc)) 'A merged branch at a non-canonical path was not removed through its PR identity.'
    Assert ([bool]($script:calls -match '^acquire pr-213$')) 'Cleanup did not reserve the merged PR identity.'
    Assert (-not (Test-Branch 'feature/ad-hoc')) 'The merged branch was not deleted with its non-canonical checkout.'
    foreach ($entry in $preserved.GetEnumerator()) {
        Assert (Test-Path -LiteralPath $entry.Value) "Unsafe removal on a later sweep ($($entry.Key)): $($entry.Value)"
    }

    if ($IsWindows) {
        # A process whose current directory is inside the checkout blocks its deletion. The
        # sweep must leave a pending record carrying the lock names, and finish it later.
        $busy = New-MergedCheckout 'pr-214-busy' 214
        $holder = Start-Process pwsh -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 300' -WorkingDirectory $busy -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 2
        Invoke-Sweep
        Assert (Test-Path -LiteralPath $busy) 'The busy checkout was removed while in use; the test cannot reproduce the lock.'
        $records = @(Get-ChildItem -LiteralPath (Join-Path $repo '.git/agent-pending-removals') -Filter '*.json' -ErrorAction SilentlyContinue)
        Assert ($records.Count -eq 1) 'A failed removal did not leave a pending record.'
        $record = Get-Content -LiteralPath $records[0].FullName -Raw | ConvertFrom-Json
        Assert (@($record.lockNames) -contains 'pr-214') 'The pending record does not carry the lock names.'
        Assert ((Get-Lock 'pr-214') -eq 'FREE') 'A failed removal leaked its reservation.'
        Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
        $holder.WaitForExit()
        Set-Lock 'pr-214' 'HELD'
        Invoke-Sweep
        Assert (Test-Path -LiteralPath $busy) 'A pending removal finished while its lock was held.'
        Assert ($script:sweepOutput -notmatch 'preserving orphan[^\n]*pr-214-busy') 'A deferred pending removal was reported as an orphan.'
        Set-Lock 'pr-214' 'FREE'
        Invoke-Sweep
        Assert (-not (Test-Path -LiteralPath $busy)) 'A later sweep did not finish the pending removal.'
        Assert ([bool]($script:calls -match '^acquire pr-214$')) 'The pending removal was finished without a reservation.'
        Assert (-not (Test-Path -LiteralPath $records[0].FullName)) 'The finished pending record survived.'
        Assert (-not (Test-Branch 'pr-214-busy')) 'The pending removal did not delete the merged branch.'
    }

    # Another repository's checkout is never a target of the shared helper.
    . (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
    $foreignRepo = Join-Path $testRoot 'foreign'
    Invoke-TestGit init -b main $foreignRepo
    Invoke-TestGit -C $foreignRepo -c user.name=t -c user.email=t@example.invalid commit --allow-empty -m foreign
    $foreignWorktree = Join-Path $testRoot 'foreign-worktree'
    Invoke-TestGit -C $foreignRepo worktree add --detach $foreignWorktree HEAD
    $foreignHead = (& $gitExecutable -C $foreignWorktree rev-parse HEAD).Trim()
    $null = Remove-MergedWorktree -Repo $repo -Worktree $foreignWorktree -ExpectedHead $foreignHead
    Assert (Test-Path -LiteralPath $foreignWorktree) 'Another repository''s checkout was removed.'
    $null = Remove-MergedWorktree -Repo $repo -Worktree $repo -ExpectedHead $main
    Assert (Test-Path -LiteralPath (Join-Path $repo 'source.txt')) 'The main checkout was removed.'
    Write-Host 'OK merged worktree sweep removes released merged checkouts and preserves owned, dirty, open, fresh, locked and unidentified work.'
}
finally {
    if ($holder) { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }
    Pop-Location -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\git, Function:\gh -ErrorAction SilentlyContinue
    Remove-Variable -Name sweepTestGit, sweepTestMerged, sweepTestOpen, sweepTestAssociation, sweepTestAfterRemove -Scope Global -ErrorAction SilentlyContinue
    # The fixture root is uniquely created above; never operate on repository worktrees.
    if (-not ([IO.Path]::GetFullPath($testRoot)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath (Join-Path $testRoot '*-junction') -Force -Recurse -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
