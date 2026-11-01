# Offline regression tests for the agent evidence sweep in WorktreeCleanup.ps1.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorktreeCleanup.ps1')
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase "evidence-cleanup-$([Guid]::NewGuid().ToString('N'))"))
if (-not $testRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path.' }

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Evidence([string]$Path, [int]$AgeDays) {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value 'evidence'
    (Get-Item -LiteralPath $Path).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-$AgeDays)
}

try {
    $root = Get-AgentEvidenceRoot -MainCheckout (Join-Path $testRoot 'Repo')
    Assert-True ($root -ceq (($testRoot -replace '\\', '/') + '/Repo-evidence')) "Unexpected evidence root: $root"
    Assert-True ((Get-AgentEvidenceRoot -MainCheckout 'C:\git\Repo\') -ceq 'C:/git/Repo-evidence') 'Trailing separator changed the evidence root.'

    New-Evidence (Join-Path $root 'issue-1/build.log') 10
    New-Evidence (Join-Path $root 'issue-2/old.log') 10
    New-Evidence (Join-Path $root 'issue-2/nested/fresh.log') 1
    New-Evidence (Join-Path $root 'pr-3-body.md') 10
    New-Evidence (Join-Path $root 'pr-4-body.md') 1
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Recurse) {
        $directory.LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-10)
    }

    $wouldRemove = Remove-StaleAgentEvidence -Root $root -RetentionDays 7 -WhatIf
    Assert-True ($wouldRemove -eq 2) "Dry run counted $wouldRemove stale entries."
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'issue-1')) 'Dry run removed evidence.'

    $removed = Remove-StaleAgentEvidence -Root $root -RetentionDays 7
    Assert-True ($removed -eq 2) "Removed $removed stale entries."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'issue-1'))) 'Stale evidence folder survived.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'pr-3-body.md'))) 'Stale evidence file survived.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'issue-2/old.log')) 'Folder with recent writes was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-4-body.md')) 'Recent evidence file was removed.'

    # A link inside a stale folder is detached, never followed: its target survives.
    $outside = Join-Path $testRoot 'outside'
    New-Evidence (Join-Path $outside 'keep.txt') 10
    New-Evidence (Join-Path $root 'issue-5/run.log') 10
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path (Join-Path $root 'issue-5/link') -Target $outside | Out-Null
    (Get-Item -LiteralPath (Join-Path $root 'issue-5/link')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-10)
    (Get-Item -LiteralPath (Join-Path $root 'issue-5')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-10)
    $removed = Remove-StaleAgentEvidence -Root $root -RetentionDays 7
    Assert-True ($removed -eq 1) "Removed $removed stale entries with a link inside."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'issue-5'))) 'Stale folder holding a link survived.'
    Assert-True (Test-Path -LiteralPath (Join-Path $outside 'keep.txt')) 'Sweep deleted a link target outside the evidence root.'

    Assert-True ((Remove-StaleAgentEvidence -Root (Join-Path $testRoot 'missing') -RetentionDays 7) -eq 0) 'Missing root reported removals.'

    # Finished evidence: closed items without a worktree go, whatever their age, under a lease.
    $locks = Join-Path $testRoot 'AgentLocks.ps1'
    $lockLog = Join-Path $testRoot 'locks.log'
    Set-Content -LiteralPath $locks -Value @"
param(`$Verb, `$LockName, `$OwnerId)
Add-Content -LiteralPath '$lockLog' -Value "`$Verb `$LockName"
if (`$LockName -eq 'pr-13') { if (`$Verb -eq 'status') { 'HELD'; exit 0 }; [Console]::Error.WriteLine('HELD'); exit 3 }
if (`$Verb -eq 'status') { 'FREE' }
if (`$Verb -eq 'acquire') { 'token' }
exit 0
"@
    foreach ($name in @('pr-10', 'issue-11', 'pr-12', 'pr-13', 'pr-14', 'pr-15', 'issue-16', 'triage', 'proto-2191-fast-rescue')) {
        New-Evidence (Join-Path $root "$name/build.log") 0
    }
    $closed = { param($LockName)
        if ($LockName -eq 'pr-15') { throw 'state unavailable' }
        return $LockName -notin @('pr-12', 'issue-2')
    }
    $finishedArguments = @{ Root = $root; AgentLocks = $locks; IsClosed = $closed; ActiveLockNames = @('pr-14', 'issue-16', $null) }

    $wouldRemove = @(Remove-FinishedAgentEvidence @finishedArguments -WhatIf | Sort-Object)
    Assert-True (($wouldRemove -join ',') -ceq 'issue-11,pr-10') "Dry run listed finished entries: $($wouldRemove -join ', ')"
    # A dry run leaves finished entries on disk; the stale pass must not count them again.
    (Get-Item -LiteralPath (Join-Path $root 'pr-10/build.log')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-10)
    (Get-Item -LiteralPath (Join-Path $root 'pr-10')).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-10)
    $staleAfterDryRun = Remove-StaleAgentEvidence -Root $root -RetentionDays 7 -Skip $wouldRemove -WhatIf
    Assert-True ($staleAfterDryRun -eq 0) "Stale dry run recounted $staleAfterDryRun finished entries."
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-10')) 'Dry run removed finished evidence.'
    Assert-True (-not (Select-String -LiteralPath $lockLog -Pattern '^(acquire|release) ' -Quiet)) 'Dry run acquired or released a lease.'
    Remove-Item -LiteralPath $lockLog

    $removed = @(Remove-FinishedAgentEvidence @finishedArguments)
    Assert-True ($removed.Count -eq 2) "Removed $($removed.Count) finished entries."
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'pr-10'))) 'Closed PR evidence survived.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $root 'issue-11'))) 'Closed issue evidence survived.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-12')) 'Open item evidence was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-13')) 'Evidence of a held item was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-14')) 'Evidence of an item with a worktree was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-15')) 'Evidence with unreadable state was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'issue-16')) 'Evidence of an issue with a worktree was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'triage')) 'Non-item evidence was removed as finished.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'proto-2191-fast-rescue')) 'Prefixed non-item evidence was removed as finished.'
    $calls = @(Get-Content -LiteralPath $lockLog | Sort-Object)
    $expected = @('acquire issue-11', 'acquire pr-10', 'acquire pr-13', 'release issue-11', 'release pr-10')
    Assert-True (($calls -join '|') -ceq ($expected -join '|')) "Unexpected lease calls: $($calls -join ', ')"

    Assert-True (@(Remove-FinishedAgentEvidence -Root $root -AgentLocks (Join-Path $testRoot 'missing.ps1') -IsClosed { $true }).Count -eq 0) 'Missing lock script removed evidence.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'pr-15')) 'Missing lock script removed evidence.'
    Write-Host 'OK agent evidence cleanup tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
