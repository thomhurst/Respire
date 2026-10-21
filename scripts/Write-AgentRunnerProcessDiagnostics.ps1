param([string]$DiagnosticsDirectory)

# Node action exits are recorded by Runner.Sdk.ProcessInvoker even when the
# action emits no output. Read them without launching another executable or
# copying raw Worker logs (which contain arguments and credential context).
# Formats: actions/runner src/Runner.Sdk/ProcessInvoker.cs and
# src/Runner.Worker/Handlers/NodeScriptActionHandler.cs.
$ErrorActionPreference = 'Stop'
$savedExitCode = $global:LASTEXITCODE
try {
    $workerStartedUtc = [DateTime]::MinValue
    if (-not $DiagnosticsDirectory) {
        # Find this shell's runner ancestor, rather than another job's Worker.
        $ancestorId = $PID
        for ($depth = 0; $depth -lt 8; $depth++) {
            $ancestor = Get-CimInstance Win32_Process -Filter "ProcessId = $ancestorId" -Property ParentProcessId, Name, ExecutablePath
            if ($null -eq $ancestor) { break }
            if ($ancestor.Name -eq 'Runner.Worker.exe') {
                $worker = [Diagnostics.Process]::GetProcessById($ancestorId)
                try { $workerStartedUtc = $worker.StartTime.ToUniversalTime() }
                finally { $worker.Dispose() }
                $runnerRoot = Split-Path (Split-Path $ancestor.ExecutablePath -Parent) -Parent
                $DiagnosticsDirectory = Join-Path $runnerRoot '_diag'
                break
            }
            $ancestorId = $ancestor.ParentProcessId
        }
        if (-not $DiagnosticsDirectory) { throw 'Runner.Worker ancestor unavailable.' }
    }

    $records = [Collections.Generic.Queue[string]]::new()
    $files = @(Get-ChildItem -LiteralPath $DiagnosticsDirectory -Filter 'Worker_*.log' -File |
        Where-Object { $_.LastWriteTimeUtc -ge $workerStartedUtc } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 4 | Sort-Object LastWriteTimeUtc)
    if ($files.Count -eq 0) { throw 'Worker trace unavailable.' }
    foreach ($file in $files) {
        $stream = $null
        $reader = $null
        try {
            # Runner keeps the trace open. Bound input to the last MiB of each
            # of at most four rotated files, and discard any initial partial line.
            $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $offset = [math]::Max(0L, $stream.Length - 1MB)
            $null = $stream.Seek($offset, [IO.SeekOrigin]::Begin)
            $bytes = [byte[]]::new([int]([math]::Min(1MB, $stream.Length - $offset)))
            $read = 0
            while ($read -lt $bytes.Length) {
                $count = $stream.Read($bytes, $read, $bytes.Length - $read)
                if ($count -eq 0) { break }
                $read += $count
            }
            $content = [Text.Encoding]::UTF8.GetString($bytes, 0, $read)
            $reader = [IO.StringReader]::new($content)
            if ($offset -gt 0) { $null = $reader.ReadLine() }
            while ($null -ne ($line = $reader.ReadLine())) {
                # Reconstruct only an allowlisted numeric record. Never emit
                # raw lines, paths, command arguments, environment or exceptions.
                $record = $null
                if ($line -match '^\[([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}Z) INFO ProcessInvoker(?:Wrapper)?\] Process started with process id ([0-9]{1,10}), waiting for process exit\.$') {
                    $record = "time=$($Matches[1]) kind=start pid=$($Matches[2])"
                }
                elseif ($line -match '^\[([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}Z) INFO ProcessInvoker(?:Wrapper)?\] Finished process ([0-9]{1,10}) with exit code (-?[0-9]{1,10}), and elapsed time [0-9:.]+\.$') {
                    $record = "time=$($Matches[1]) kind=exit pid=$($Matches[2]) exit=$($Matches[3])"
                }
                if ($null -ne $record) {
                    $records.Enqueue($record)
                    if ($records.Count -gt 80) { $null = $records.Dequeue() }
                }
            }
        }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $stream) { $stream.Dispose() }
        }
    }
    foreach ($record in $records) { [Console]::Error.WriteLine("runner-process $record") }
    [Console]::Error.WriteLine("runner-process files=$($files.Count) records=$($records.Count) inputLimitBytes=4194304 recordLimit=80")
}
catch {
    # Do not print the exception: a filesystem/trace error can contain raw data.
    try { [Console]::Error.WriteLine('runner-process evidence unavailable; no raw trace emitted.') } catch { }
}
finally { $global:LASTEXITCODE = $savedExitCode }
