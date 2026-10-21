param(
    [ValidateRange(1, 100)][int]$Iterations = 30,
    [ValidateRange(1, 30)][int]$LaunchTimeoutSeconds = 15,
    [ValidateRange(1, 300)][int]$TotalTimeoutSeconds = 180,
    [string]$DiagnosticsDirectory,
    [string]$GuardScript = (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'),
    # Override only for failure controls; both launch modes use the same payload.
    [string]$ProbeCommand = '[Console]::WriteLine("startup-probe entered childPid=$PID"); exit 0'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { Write-Output 'SKIP Windows startup comparison.'; exit 0 }
$phaseLog = $null
if ($DiagnosticsDirectory) {
    try {
        $root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DiagnosticsDirectory)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        $phaseLog = Join-Path $root 'startup-comparison.log'
    }
    catch { [Console]::Error.WriteLine("Startup comparison file unavailable: $_") }
}
function Write-ComparisonPhase([string]$Message) {
    $line = '{0:o} startup-comparison hostPid={1} {2}' -f [DateTimeOffset]::UtcNow, $PID, $Message
    [Console]::WriteLine($line)
    if ($phaseLog) { try { [IO.File]::AppendAllText($phaseLog, $line + [Environment]::NewLine) } catch { } }
}
function Write-ComparisonHost {
    try {
        $hostSnapshot = [Diagnostics.Process]::GetCurrentProcess()
        try { Write-ComparisonPhase "host session=$($hostSnapshot.SessionId) handles=$($hostSnapshot.HandleCount) workingSetBytes=$($hostSnapshot.WorkingSet64)" }
        finally { $hostSnapshot.Dispose() }
    }
    catch { Write-ComparisonPhase "host snapshot unavailable: $_" }
}

# A direct PowerShell host failed in run 39528792779 before it could enter
# Invoke-AgentDotNet. Compare that boundary with the exact same harmless payload
# inside containment, in one surviving host. This is an opt-in investigation,
# never a retry of a failed smoke test and never a substitute for those tests.
$pwsh = (Get-Process -Id $PID).Path
$arguments = @('-NoProfile', '-NonInteractive', '-Command', $ProbeCommand)
$clock = [Diagnostics.Stopwatch]::StartNew()
$result = 0
try {
    Write-ComparisonPhase "begin iterations=$Iterations pwsh=$pwsh os=$([Environment]::OSVersion.Version)"
    Write-ComparisonHost
    foreach ($iteration in 1..$Iterations) {
        foreach ($mode in @('direct', 'guarded')) {
            $remaining = $TotalTimeoutSeconds - $clock.Elapsed.TotalSeconds
            if ($remaining -le 0) { $result = 124; Write-ComparisonPhase 'total deadline exceeded'; break }
            $timeout = [math]::Max(1, [math]::Min($LaunchTimeoutSeconds, [math]::Floor($remaining)))
            Write-ComparisonPhase "iteration=$iteration mode=$mode start timeoutSeconds=$timeout"
            if ($mode -eq 'direct') {
                $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
                $child = [Diagnostics.Process]::Start($info)
                try {
                    Write-ComparisonPhase "iteration=$iteration mode=$mode childPid=$($child.Id) childStartTicks=$($child.StartTime.ToUniversalTime().Ticks)"
                    $stdout = $child.StandardOutput.ReadToEndAsync()
                    $stderr = $child.StandardError.ReadToEndAsync()
                    $deadline = [Diagnostics.Stopwatch]::StartNew()
                    if (-not $child.WaitForExit($timeout * 1000)) { $result = 124 }
                    else {
                        $result = $child.ExitCode
                        # Drainage shares the launch deadline, even if a descendant
                        # keeps a pipe open. Never let diagnostics hang the host.
                        $drainTime = [TimeSpan]::FromMilliseconds([math]::Max(1, $timeout * 1000 - $deadline.ElapsedMilliseconds))
                        try {
                            [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr)).WaitAsync($drainTime).GetAwaiter().GetResult()
                            $output = @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult())
                            foreach ($line in $output) { if ($line) { Write-ComparisonPhase "direct output=$($line.Trim())" } }
                            if ($result -eq 0 -and $output[0].Trim() -notmatch '^startup-probe entered childPid=\d+$') {
                                $result = 1
                                Write-ComparisonPhase 'direct process did not reach the payload marker'
                            }
                        }
                        catch {
                            if ($result -eq 0) { $result = 124 }
                            Write-ComparisonPhase "direct drainage unavailable: $_"
                        }
                    }
                }
                finally {
                    if (-not $child.HasExited) { $child.Kill($true); $null = $child.WaitForExit(5000) }
                    $child.Dispose()
                }
            }
            else {
                & $GuardScript -DotNetPath $pwsh -TimeoutSeconds $timeout -DotNetArguments $arguments
                $result = $LASTEXITCODE
            }
            Write-ComparisonPhase "iteration=$iteration mode=$mode exit=$result elapsedSeconds=$($clock.Elapsed.TotalSeconds)"
            Write-ComparisonHost
            if ($result -ne 0) {
                if ($result -eq -1073741502) {
                    try { & (Join-Path $PSScriptRoot 'Write-AgentWindowsStartupDiagnostics.ps1') }
                    catch { [Console]::Error.WriteLine("Startup comparison evidence unavailable: $_") }
                }
                break
            }
        }
        if ($result -ne 0) { break }
    }
}
catch {
    if ($result -eq 0) { $result = 1 }
    Write-ComparisonPhase "exception: $_"
    throw
}
finally { Write-ComparisonPhase "end exit=$result elapsedSeconds=$($clock.Elapsed.TotalSeconds)" }
exit $result
