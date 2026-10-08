# Start-IssuePrLoop.ps1
# Runs the issue-pr-loop skill one unit at a time, each in a fresh agent process.
# A long-lived agent session keeps growing its context, and model latency grows with
# it. Starting a new process per unit keeps every unit's context small.
#
# AGENT SELECTION (-Agent auto):
#   - CLAUDECODE=1        -> launched from Claude Code, so run `claude -p`.
#   - CODEX_THREAD_ID set -> launched from Codex, so run `codex exec`.
#   - otherwise           -> the only agent CLI on PATH; Codex when both are present.
#
# Each run gets its own RESPIRE_AGENT_LOCK_OWNER_ID, so AgentLocks.ps1 ownership never
# leaks between units. The agent ends with a `RESULT: ...` line (see the skill's
# single-unit mode). On `RESULT: queue-empty` the loop waits -IdleSeconds before the
# next survey, because pending CI and reviews make new work appear later.
#
# Run several copies in separate terminals for parallel workers; Redis locks keep
# them on different items.
#
# Stop:   create the stop file printed at startup (checked between units), or Ctrl+C.
# Usage:  pwsh scripts/Start-IssuePrLoop.ps1 [-Agent auto|claude|codex] [-IdleSeconds n] [-MaxUnits n]

[CmdletBinding()]
param(
    [ValidateSet('auto', 'claude', 'codex')]
    [string]$Agent = 'auto',
    [ValidateRange(0, 86400)]
    [int]$IdleSeconds = 600,
    # 0 means no limit.
    [ValidateRange(0, [int]::MaxValue)]
    [int]$MaxUnits = 0,
    [string]$LogDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'issue-pr-loop')
)

$ErrorActionPreference = 'Stop'

function Resolve-Agent([string]$Requested) {
    if ($Requested -ne 'auto') { return $Requested }
    if ($env:CLAUDECODE -eq '1') { return 'claude' }
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_THREAD_ID)) { return 'codex' }
    $hasCodex = [bool](Get-Command codex -ErrorAction SilentlyContinue)
    $hasClaude = [bool](Get-Command claude -ErrorAction SilentlyContinue)
    if ($hasCodex) { return 'codex' }
    if ($hasClaude) { return 'claude' }
    throw 'Neither codex nor claude is on PATH. Install one or pass -Agent.'
}

$repo = git -C $PSScriptRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'Start-IssuePrLoop.ps1 must run from a Git checkout.' }
$repo = $repo.Trim()
$stopFile = Join-Path (Resolve-Path (git -C $repo rev-parse --git-common-dir)) 'issue-pr-loop.stop'
$selected = Resolve-Agent $Agent
New-Item -ItemType Directory -Force $LogDirectory | Out-Null

Write-Host "issue-pr-loop: agent=$selected repo=$repo"
Write-Host "issue-pr-loop: logs in $LogDirectory"
Write-Host "issue-pr-loop: create $stopFile to stop after the current unit"

# A nested `claude -p` refuses to start when it inherits CLAUDECODE from a parent session.
Remove-Item Env:CLAUDECODE -ErrorAction SilentlyContinue

$units = 0
while (-not (Test-Path -LiteralPath $stopFile)) {
    if ($MaxUnits -gt 0 -and $units -ge $MaxUnits) { break }
    $units++

    $runId = '{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $log = Join-Path $LogDirectory "$runId.log"
    $env:RESPIRE_AGENT_LOCK_OWNER_ID = "issue-pr-loop-$runId"

    Write-Host "issue-pr-loop: unit $units ($runId) started"
    if ($selected -eq 'codex') {
        $prompt = 'Run the $issue-pr-loop skill in single-unit mode.'
        codex exec --dangerously-bypass-approvals-and-sandbox -C $repo -o $log $prompt
    }
    else {
        $prompt = '/issue-pr-loop Run in single-unit mode.'
        Push-Location $repo
        try { claude -p $prompt --dangerously-skip-permissions *> $log }
        finally { Pop-Location }
    }
    $exitCode = $LASTEXITCODE

    $result = $null
    if (Test-Path -LiteralPath $log) {
        $result = Select-String -LiteralPath $log -Pattern '^\s*RESULT:\s*(.+)$' |
            Select-Object -Last 1 |
            ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() }
    }
    if (-not $result) {
        # A crashed or misconfigured agent must not spin; back off before retrying.
        Write-Host "issue-pr-loop: unit $units ($runId) no RESULT line (agent exit $exitCode), see $log"
        Start-Sleep -Seconds ([Math]::Max(60, [Math]::Min($IdleSeconds, 300)))
        continue
    }
    Write-Host "issue-pr-loop: unit $units ($runId) $result"

    if ($result -eq 'queue-empty' -and $IdleSeconds -gt 0) {
        Write-Host "issue-pr-loop: queue empty, next survey in $IdleSeconds s"
        Start-Sleep -Seconds $IdleSeconds
    }
}

# Clear the stop request so the next start runs.
Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
Remove-Item Env:RESPIRE_AGENT_LOCK_OWNER_ID -ErrorAction SilentlyContinue
Write-Host "issue-pr-loop: stopped after $units unit(s)"
