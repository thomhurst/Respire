param([string]$DiagnosticsScript = (Join-Path $PSScriptRoot 'Write-AgentRunnerProcessDiagnostics.ps1'))

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('runner-process-diagnostics-' + [guid]::NewGuid())
$pwsh = (Get-Process -Id $PID).Path
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $trace = Join-Path $testRoot 'Worker_fixture.log'
    @'
[2026-10-21 05:33:42Z INFO ProcessInvoker]   Arguments: 'secret-sentinel'
[2026-10-21 05:33:42Z INFO ProcessInvokerWrapper] Process started with process id 1234, waiting for process exit.
[2026-10-21 05:33:43Z INFO ProcessInvokerWrapper] Finished process 1234 with exit code -1073741502, and elapsed time 00:00:00.0123456.
[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process 1235 with exit code 73, and elapsed time 00:00:01.
[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process 1236 with exit code 0, and elapsed time 00:00:01.
[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process 1237 with exit code 0, and elapsed time 00:00:01. secret-sentinel
[2026-10-21 05:33:43Z INFO OtherLogger] Process started with process id 1238, waiting for process exit.
secret-sentinel
'@ | Set-Content -LiteralPath $trace
    # Invoke in a child so Console.Error is captured, just as it is by Actions.
    $caller = Join-Path $testRoot 'Invoke-Case.ps1'
    @'
param([string]$Script, [string]$Directory, [switch]$Discover)
$global:LASTEXITCODE = 73
if ($Discover) {
    function Get-CimInstance {
        param([string]$ClassName, [string]$Filter, [string[]]$Property)
        # This child stands in for its own Worker ancestor; no other process
        # is inspected or stopped by the discovery fixture.
        @{ Name = 'Runner.Worker.exe'; ExecutablePath = [IO.Path]::GetFullPath((Join-Path $Directory '../bin/Runner.Worker.exe')) }
    }
    & $Script
}
else { & $Script -DiagnosticsDirectory $Directory }
if ($LASTEXITCODE -ne 73) { throw 'Diagnostics changed the original native exit.' }
exit 0
'@ | Set-Content -LiteralPath $caller
    function Invoke-Case([string]$Directory = $testRoot) {
        $output = & $pwsh -NoProfile -NonInteractive -File $caller $DiagnosticsScript $Directory 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Diagnostic fixture failed: $output" }
        return ($output -join "`n")
    }
    # A live Worker holds its trace open for writing.
    $writer = [IO.FileStream]::new($trace, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try { $output = Invoke-Case }
    finally { $writer.Dispose() }
    foreach ($marker in @('kind=start pid=1234', 'kind=exit pid=1234 exit=-1073741502', 'kind=exit pid=1235 exit=73', 'kind=exit pid=1236 exit=0', 'files=1 records=4')) {
        if (-not $output.Contains($marker)) { throw "Missing process evidence: $marker; $output" }
    }
    if ($output -match 'secret-sentinel|pid=1237|pid=1238') { throw 'Reader disclosed a non-allowlisted trace record.' }

    # No unbounded scan, even for a huge log or a flood of accepted records.
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('x' * (1MB + 100))
    foreach ($id in 1..100) { $lines.Add("[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process $id with exit code 0, and elapsed time 00:00:01.") }
    [IO.File]::WriteAllLines($trace, $lines)
    $output = Invoke-Case
    if ($output -notmatch 'files=1 records=80' -or $output -match 'pid=20 exit=' -or $output -notmatch 'pid=21 exit=0' -or $output -notmatch 'pid=100 exit=0') { throw "Record/input limits failed: $output" }
    foreach ($id in 1..5) {
        $path = Join-Path $testRoot "Worker_rotated-$id.log"
        "[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process $id with exit code 73, and elapsed time 00:00:01." | Set-Content -LiteralPath $path
        [IO.File]::SetLastWriteTimeUtc($path, [DateTime]::UtcNow.AddMinutes($id))
    }
    $output = Invoke-Case
    if ($output -notmatch 'files=4 records=4' -or $output -match 'pid=1 exit=73' -or $output -notmatch 'pid=5 exit=73') { throw "Rotation limit failed: $output" }

    $output = Invoke-Case (Join-Path $testRoot 'secret-sentinel-missing')
    if ($output -notmatch 'evidence unavailable' -or $output -match 'secret-sentinel') { throw "Missing trace handling leaked data or lost status: $output" }
    $empty = New-Item -ItemType Directory -Path (Join-Path $testRoot 'empty')
    $output = Invoke-Case $empty.FullName
    if ($output -notmatch 'evidence unavailable') { throw 'Missing trace was not reported.' }
    $mockRoot = New-Item -ItemType Directory -Path (Join-Path $testRoot 'runner/_diag') -Force
    $oldTrace = Join-Path $mockRoot 'Worker_previous-job.log'
    '[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process 999 with exit code 73, and elapsed time 00:00:01.' | Set-Content -LiteralPath $oldTrace
    [IO.File]::SetLastWriteTimeUtc($oldTrace, [DateTime]::UtcNow.AddDays(-1))
    '[2026-10-21 05:33:43Z INFO ProcessInvoker] Finished process 123 with exit code 0, and elapsed time 00:00:01.' | Set-Content -LiteralPath (Join-Path $mockRoot 'Worker_current-job.log')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $mockRoot 'Worker_current-job.log'), [DateTime]::UtcNow.AddMinutes(1))
    $output = (& $pwsh -NoProfile -NonInteractive -File $caller $DiagnosticsScript $mockRoot.FullName -Discover 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $output -notmatch 'files=1 records=1' -or $output -notmatch 'pid=123 exit=0' -or $output -match 'pid=999') { throw "Worker ancestor/previous-job filtering failed: $output" }
    Write-Output 'OK runner process evidence preserves native exits, reads shared/rotated traces, bounds input/output, and excludes raw trace data.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing fixture cleanup outside temp.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
