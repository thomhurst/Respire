# Dot-source to start a child PowerShell with its own Unix startup profile directory.
function Invoke-IsolatedPowerShell {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $PWD.ProviderPath,
        [int]$TimeoutSeconds = 0
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -CommandType Application | Select-Object -First 1).Source)
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $ScriptPath) + $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $cache = $null
    $process = $null
    $timeout = [Threading.CancellationTokenSource]::new()
    try {
        # Even -NoProfile uses a startup profile shared by concurrent hosts. Override
        # its location before pwsh starts and leave the caller's environment alone.
        if (-not $IsWindows) {
            $cache = [IO.Directory]::CreateTempSubdirectory('respire-agent-pwsh-')
            $startInfo.Environment['XDG_CACHE_HOME'] = $cache.FullName
        }
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($TimeoutSeconds -gt 0) { $timeout.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds)) }
        $stdout = $process.StandardOutput.ReadToEndAsync($timeout.Token)
        $stderr = $process.StandardError.ReadToEndAsync($timeout.Token)
        $process.WaitForExitAsync($timeout.Token).GetAwaiter().GetResult()
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout.GetAwaiter().GetResult()
            StdErr = $stderr.GetAwaiter().GetResult()
        }
    }
    catch [OperationCanceledException] {
        [pscustomobject]@{ ExitCode = 124; StdOut = ''; StdErr = "timed out after $TimeoutSeconds s" }
    }
    finally {
        try {
            if ($process) {
                try {
                    if (-not $process.HasExited) {
                        try { $process.Kill($true) }
                        catch { if (-not $process.HasExited) { throw } }
                        $process.WaitForExit()
                    }
                }
                finally { $process.Dispose() }
            }
        }
        finally {
            $timeout.Dispose()
            if ($cache) {
                try { $cache.Delete($true) }
                catch { Write-Warning "Could not remove PowerShell startup cache '$($cache.FullName)': $_" }
            }
        }
    }
}
