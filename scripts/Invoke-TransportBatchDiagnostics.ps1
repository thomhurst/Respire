<#
.SYNOPSIS
Captures two separate allocation diagnostic launches per standalone batch case and metric mode.
.NOTES
Build the benchmark project first through Invoke-AgentDotNet.ps1. This is not a timing
benchmark. No guard limits, runtime tiering policy, or transport limits are overridden.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('net8.0', 'net10.0')][string]$Framework = 'net10.0',
    [ValidateRange(128, 4096)][int]$Iterations = 1024
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$revision = (git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the diagnostic revision.' }
if (git -C $repo status --porcelain -- benchmarks/Respire.Benchmarks src Directory.Build.props Directory.Packages.props) {
    throw 'Commit measured source before capturing revision-stamped evidence.'
}
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$binary = Join-Path $repo "benchmarks/Respire.Benchmarks/bin/Release/$Framework/Respire.Benchmarks.dll"
if (!(Test-Path -LiteralPath $binary)) { throw "Build the $Framework benchmark project first." }
$previousMetrics = $env:RESPIRE_BENCH_ERRORS
try {
    foreach ($metrics in @('disabled', 'enabled')) {
        $env:RESPIRE_BENCH_ERRORS = $metrics
        foreach ($connections in @(1, 2)) {
            foreach ($count in @(32, 100)) {
                foreach ($launch in @(1, 2)) {
                    $destination = Join-Path $outputRoot "$metrics/c$connections-n$count-l$launch"
                    & (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1') -DotNetArguments @(
                        $binary, '--transport-batch-diagnostics', $destination, $revision, 'local',
                        $connections.ToString(), $count.ToString(), $launch.ToString(), $Iterations.ToString())
                    if ($LASTEXITCODE -ne 0) { throw "Diagnostic launch failed: $destination" }
                    & (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1') -DotNetArguments @(
                        $binary, '--transport-batch-trace-report', (Join-Path $destination 'allocations.nettrace'),
                        (Join-Path $destination 'allocations.jsonl'))
                    if ($LASTEXITCODE -ne 0) { throw "Trace conversion failed: $destination" }
                }
            }
        }
    }
}
finally { $env:RESPIRE_BENCH_ERRORS = $previousMetrics }
