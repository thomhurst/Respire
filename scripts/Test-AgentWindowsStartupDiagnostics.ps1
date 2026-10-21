$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { Write-Output 'SKIP Windows startup diagnostics.'; exit 0 }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-startup-diagnostics-{0}" -f [guid]::NewGuid())
New-Item -ItemType Directory -Path $testRoot | Out-Null
$pwshPath = (Get-Process -Id $PID).Path
try {
    foreach ($name in @('Invoke-AgentDotNet.ps1', 'AgentDotNetWindowsJob.cs', 'Write-AgentWindowsStartupDiagnostics.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $testRoot
    }
    $helper = Join-Path $testRoot 'Write-AgentWindowsStartupDiagnostics.ps1'
    $originalHelper = [IO.File]::ReadAllText($helper)
    $failureScript = Join-Path $testRoot 'Fail-Startup.ps1'
    @'
param([int]$ExitCode)
$info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60')) { $info.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($info)
try {
    [IO.File]::WriteAllText($env:RESPIRE_STARTUP_CHILD_RECORD, (ConvertTo-Json -Compress -InputObject @{
        Id = $child.Id; StartTicks = $child.StartTime.ToUniversalTime().Ticks
    }))
}
finally { $child.Dispose() }
exit $ExitCode
'@ | Set-Content -LiteralPath $failureScript
    $caller = Join-Path $testRoot 'Invoke-Case.ps1'
    @'
param([string]$Guard, [string]$Pwsh, [string]$FailureScript, [string]$ExitCode)
& $Guard -DotNetPath $Pwsh -TimeoutSeconds 30 -DotNetArguments @('-NoProfile', '-NonInteractive', '-File', $FailureScript, '-ExitCode', $ExitCode)
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $caller

    foreach ($mode in @('real', 'throwing', 'missing', 'unrelated')) {
        if ($mode -eq 'throwing') { Set-Content -LiteralPath $helper -Value "throw 'diagnostic failure sentinel'" }
        elseif ($mode -eq 'missing') { Remove-Item -LiteralPath $helper }
        else { [IO.File]::WriteAllText($helper, $originalHelper) }
        $expected = if ($mode -eq 'unrelated') { 73 } else { -1073741502 }
        $recordPath = Join-Path $testRoot "$mode-child.json"
        $scratch = Join-Path $testRoot "$mode-temp"
        New-Item -ItemType Directory -Path $scratch | Out-Null
        $info = [Diagnostics.ProcessStartInfo]::new($pwshPath)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Environment['RESPIRE_STARTUP_CHILD_RECORD'] = $recordPath
        $info.Environment['TEMP'] = $scratch
        $info.Environment['TMP'] = $scratch
        foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $caller,
            (Join-Path $testRoot 'Invoke-AgentDotNet.ps1'), $pwshPath, $failureScript, '-ExitCode', $expected.ToString())) {
            $info.ArgumentList.Add($argument)
        }
        $hostProcess = [Diagnostics.Process]::Start($info)
        try {
            $stdout = $hostProcess.StandardOutput.ReadToEndAsync()
            $stderr = $hostProcess.StandardError.ReadToEndAsync()
            if (-not $hostProcess.WaitForExit(60000)) { $hostProcess.Kill($true); throw "$mode case exceeded its deadline." }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            if ($hostProcess.ExitCode -ne $expected) { throw "$mode changed exit to $($hostProcess.ExitCode): $output" }
            if ($mode -eq 'real' -and $output -notmatch 'startup-failure .*exit=0xC0000142 guardPid=\d+ wrapperPid=\d+') {
                throw "Startup status lacked live evidence: $output"
            }
            if ($mode -eq 'real' -and $output -notmatch 'startup-failure workloadPid=\d+ executable=pwsh.exe exit=0xC0000142') {
                throw "Workload status lacked process identity: $output"
            }
            if ($mode -eq 'real' -and $output -notmatch 'event log=Application (records=\d+|unavailable:)') {
                throw "Event-log outcome was not retained: $output"
            }
            if ($mode -in @('throwing', 'missing') -and $output -notmatch 'Windows startup diagnostics unavailable:') {
                throw "Diagnostic failure was not reported: $output"
            }
            if ($mode -eq 'unrelated' -and $output -match 'startup-failure|Windows startup diagnostics') {
                throw "Unrelated exit triggered Windows startup evidence: $output"
            }
            $identity = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
            $child = Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
            if ($null -ne $child) {
                try {
                    if ($child.StartTime.ToUniversalTime().Ticks -eq $identity.StartTicks -and -not $child.WaitForExit(5000)) {
                        throw "$mode diagnostic handling skipped descendant cleanup."
                    }
                }
                finally { $child.Dispose() }
            }
            if (Get-ChildItem -LiteralPath $scratch -Filter 'agent-dotnet-*') { throw "$mode left guard temporary files." }
            Write-Output "OK $mode preserves exit $expected and descendant/fixture cleanup."
        }
        finally { $hostProcess.Dispose() }
    }
}
finally {
    # Only identities recorded by this fixture may be stopped.
    foreach ($record in Get-ChildItem -LiteralPath $testRoot -Filter '*-child.json') {
        $identity = Get-Content -LiteralPath $record.FullName -Raw | ConvertFrom-Json
        $child = Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
        if ($null -ne $child) {
            try { if ($child.StartTime.ToUniversalTime().Ticks -eq $identity.StartTicks -and -not $child.HasExited) { $child.Kill($true) } }
            finally { $child.Dispose() }
        }
    }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
exit 0
