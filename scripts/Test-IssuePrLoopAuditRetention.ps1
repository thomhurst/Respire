#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

# Run the lock script's real defer verb without Docker or Redis.
function docker {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'inspect') { return 'true redis-server' }
    if ($args[0] -eq 'exec') {
        $redisCalls.Add(($args[2..($args.Count - 1)] -join ' '))
        return 'OK'
    }
    throw "Unexpected Docker command: $($args -join ' ')"
}

$agentLocks = Join-Path $PSScriptRoot 'AgentLocks.ps1'
# The first AuditDays value above the former seven-day cap and the driver's maximum.
foreach ($days in @(8, 90)) {
    $redisCalls = [Collections.Generic.List[string]]::new()
    $seconds = $days * 86400
    & $agentLocks defer -LockName 'audit-1234' -Fingerprint 'completed' -DeferSeconds $seconds
    if ($LASTEXITCODE -ne 0) { throw "Recording a $days-day audit failed with exit $LASTEXITCODE." }

    $actual = $redisCalls -join [Environment]::NewLine
    $expected = "redis-cli -e SET respire:agent-defer:audit-1234 completed EX $seconds"
    if ($actual -cne $expected) { throw "Expected '$expected', got '$actual'." }
}

Write-Host 'Issue/PR loop audit retention tests passed.'
