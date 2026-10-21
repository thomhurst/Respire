$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { Write-Output 'SKIP Windows startup comparison controls.'; exit 0 }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('startup-comparison-control-' + [guid]::NewGuid())
$pwsh = (Get-Process -Id $PID).Path
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    foreach ($name in @('Test-AgentWindowsStartupComparison.ps1', 'Write-AgentWindowsStartupDiagnostics.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $testRoot
    }
    $guard = Join-Path $testRoot 'Invoke-AgentDotNet.ps1'
    @'
param($DotNetPath, $TimeoutSeconds, $DotNetArguments)
if ($TimeoutSeconds -gt 15 -or $TimeoutSeconds -lt 1) { throw 'Launch deadline changed.' }
[IO.File]::AppendAllText((Join-Path $PSScriptRoot 'calls.txt'), "guard called`n")
exit ([int][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'exit.txt')))
'@ | Set-Content -LiteralPath $guard
    # Diagnostic failures must not replace the original startup status.
    "throw 'comparison diagnostic failure sentinel'" | Set-Content -LiteralPath (Join-Path $testRoot 'Write-AgentWindowsStartupDiagnostics.ps1')
    foreach ($case in @(
        @{ Exit = 0; Calls = 2 },
        @{ Exit = 73; Calls = 1 },
        @{ Exit = 124; Calls = 1 },
        @{ Exit = -1073741502; Calls = 1 },
        @{ Exit = 73; Calls = 0; Probe = 'exit 73' },
        @{ Exit = -1073741502; Calls = 0; Probe = 'exit -1073741502' },
        @{ Exit = 124; Calls = 0; Probe = '[Threading.Thread]::Sleep(30000)' }
    )) {
        $expected = $case.Exit
        $mode = if ($case.Calls -eq 0) { 'direct' } else { 'guarded' }
        $logs = Join-Path $testRoot "logs-$expected-$mode"
        $expected | Set-Content -LiteralPath (Join-Path $testRoot 'exit.txt')
        Remove-Item -LiteralPath (Join-Path $testRoot 'calls.txt') -ErrorAction SilentlyContinue
        $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $testRoot 'Test-AgentWindowsStartupComparison.ps1'),
            '-Iterations', '2', '-DiagnosticsDirectory', $logs)) { $info.ArgumentList.Add($argument) }
        if ($case.Probe) {
            foreach ($argument in @('-ProbeCommand', $case.Probe, '-LaunchTimeoutSeconds', '2')) { $info.ArgumentList.Add($argument) }
        }
        $hostProcess = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $hostProcess.StandardOutput.ReadToEndAsync()
            $stderr = $hostProcess.StandardError.ReadToEndAsync()
            if (-not $hostProcess.WaitForExit(60000)) { throw 'Comparison control exceeded its deadline.' }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            if ($hostProcess.ExitCode -ne $expected) { throw "Comparison lost exit $expected : $output" }
            $calls = if (Test-Path -LiteralPath (Join-Path $testRoot 'calls.txt')) { @(Get-Content -LiteralPath (Join-Path $testRoot 'calls.txt')).Count } else { 0 }
            if ($calls -ne $case.Calls) { throw "Comparison retried exit $expected or launched containment after a direct failure." }
            if (-not $case.Probe -and $output -notmatch 'direct output=startup-probe entered') { throw "Direct startup marker missing: $output" }
            if ($expected -eq -1073741502 -and $output -notmatch 'comparison diagnostic failure sentinel') { throw 'Diagnostic failure control did not run.' }
            $saved = Get-Content -LiteralPath (Join-Path $logs 'startup-comparison.log') -Raw
            if ($saved -notmatch "end exit=$expected ") { throw 'Comparison lost its final live/file marker.' }
            if ($saved -match 'childPid=(\d+) childStartTicks=(\d+)') {
                $identity = $Matches.Clone()
                $child = Get-Process -Id ([int]$identity[1]) -ErrorAction SilentlyContinue
                if ($child) {
                    try {
                        if ($child.StartTime.ToUniversalTime().Ticks -eq [long]$identity[2] -and -not $child.HasExited) { throw 'Direct probe survived comparison cleanup.' }
                    }
                    finally { $child.Dispose() }
                }
            }
            else { throw 'Direct probe identity was not retained.' }
            Write-Output "OK comparison mode=$mode exit=$expected; calls=$calls; no retry, cleanup, live/file evidence."
        }
        finally {
            if (-not $hostProcess.HasExited) { $hostProcess.Kill($true); $null = $hostProcess.WaitForExit(5000) }
            $hostProcess.Dispose()
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing fixture cleanup outside temp.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
