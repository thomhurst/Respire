param([int]$WrapperProcessId, [long]$PeakWorkingSetBytes, [switch]$SnapshotOnly)

# Run in the surviving guard host: after STATUS_DLL_INIT_FAILED even the
# runner's Node-based artifact action may be unable to start. Never launch a
# diagnostic child, enumerate environment variables, or replace the guard exit.
$ErrorActionPreference = 'Stop'
function Write-StartupEvidence([string]$Message) {
    $prefix = if ($SnapshotOnly) { 'startup-snapshot' } else { 'startup-failure' }
    try { [Console]::Error.WriteLine(('{0} {1:o} {2}' -f $prefix, [DateTimeOffset]::UtcNow, $Message)) } catch { }
}

if (-not $SnapshotOnly) {
    Write-StartupEvidence "exit=0xC0000142 guardPid=$PID wrapperPid=$WrapperProcessId peakWorkingSetBytes=$PeakWorkingSetBytes os=$([Environment]::OSVersion.Version)"
}
try {
    $current = [Diagnostics.Process]::GetCurrentProcess()
    try {
        Write-StartupEvidence "host session=$($current.SessionId) handles=$($current.HandleCount) workingSetBytes=$($current.WorkingSet64)"
    }
    finally { $current.Dispose() }
}
catch { Write-StartupEvidence "host snapshot unavailable: $($_.Exception.Message)" }

try {
    # Add-Type compiles in this host, without a compiler child. Normally the
    # real guard already loaded this type before a workload or discovery failed.
    Add-Type -Path (Join-Path $PSScriptRoot 'AgentDotNetWindowsJob.cs')
    try { Write-StartupEvidence ([AgentDotNetWindowsJob]::DescribeSystemResources()) }
    catch { Write-StartupEvidence "system snapshot unavailable: $($_.Exception.Message)" }
    try { Write-StartupEvidence ([AgentDotNetWindowsJob]::DescribeDesktopResources()) }
    catch { Write-StartupEvidence "desktop snapshot unavailable: $($_.Exception.Message)" }
}
catch { Write-StartupEvidence "native snapshot unavailable: $($_.Exception.Message)" }
if ($SnapshotOnly) { return }

# Restrict queries to crash, application-popup and resource-exhaustion events
# near the failure. ReadEvent has an OS timeout; cap records and output as well.
# The query/reader constructor and OS calls remain best-effort: a lost runner or
# hung event-log service can still prevent complete evidence from reaching CI.
$queries = @{
    Application = '*[System[(EventID=1000 or EventID=1001 or EventID=26) and TimeCreated[timediff(@SystemTime) <= 120000]]]'
    System = '*[System[(EventID=2004 or EventID=26) and TimeCreated[timediff(@SystemTime) <= 120000]]]'
}
foreach ($log in @('Application', 'System')) {
    $reader = $null
    try {
        $query = [Diagnostics.Eventing.Reader.EventLogQuery]::new($log, [Diagnostics.Eventing.Reader.PathType]::LogName, $queries[$log])
        $query.ReverseDirection = $true
        $reader = [Diagnostics.Eventing.Reader.EventLogReader]::new($query)
        $reader.BatchSize = 1
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
        $count = 0
        while ($count -lt 5 -and [DateTimeOffset]::UtcNow -lt $deadline) {
            $event = $reader.ReadEvent([TimeSpan]::FromMilliseconds(200))
            if ($null -eq $event) { break }
            try {
                # XML retains the faulting module/status even if localized
                # message-resource formatting is unavailable on the runner.
                $xml = $event.ToXml().Replace("`r", ' ').Replace("`n", ' ')
                Write-StartupEvidence "event log=$log xml=$($xml.Substring(0, [math]::Min(4096, $xml.Length)))"
                $count++
            }
            finally { $event.Dispose() }
        }
        Write-StartupEvidence "event log=$log records=$count"
    }
    catch { Write-StartupEvidence "event log=$log unavailable: $($_.Exception.Message)" }
    finally { if ($null -ne $reader) { $reader.Dispose() } }
}
