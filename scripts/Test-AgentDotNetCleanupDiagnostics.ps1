$ErrorActionPreference = 'Stop'

# Exercise cleanup decisions without terminating any real process. In particular,
# a recycled PID must be reported as skipped, rather than as a successful kill.
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Guard script did not parse.' }
foreach ($name in @('Write-AgentCleanupEvidence', 'Stop-ProcessTree')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if ($null -eq $definition) { throw "Missing cleanup evidence helper: $name" }
    $source = $definition.Extent.Text.Replace('[System.Diagnostics.Process]', '[CleanupProcessFixture]').
        Replace('[AgentDotNetWindowsJob]', '[CleanupJobFixture]').Replace('$IsWindows', '$true')
    . ([scriptblock]::Create($source))
}

class CleanupProcessFixture {
    static [Collections.Generic.List[int]] $Killed = [Collections.Generic.List[int]]::new()
    static [Collections.Generic.List[int]] $Disposed = [Collections.Generic.List[int]]::new()
    static [int] $ThrowOnKillId
    [int] $Id
    [bool] $HasExited = $true
    [IntPtr] $SafeHandle = [IntPtr]::new(1)
    static [CleanupProcessFixture] GetProcessById([int]$ProcessId) {
        if ($ProcessId -eq 73) { throw [ComponentModel.Win32Exception]::new('private-error-sentinel') }
        return [CleanupProcessFixture]@{ Id = $ProcessId }
    }
    [void] Kill() {
        if ($this.Id -eq [CleanupProcessFixture]::ThrowOnKillId) {
            throw [UnauthorizedAccessException]::new('private-kill-sentinel')
        }
        [CleanupProcessFixture]::Killed.Add($this.Id)
    }
    [void] Kill([bool]$Tree) { $this.Kill() }
    [void] Dispose() { [CleanupProcessFixture]::Disposed.Add($this.Id) }
}
class CleanupJobFixture {
    static [int] $Closed
    static [bool] $FailQuery
    static [int[]] GetProcessIds([IntPtr]$Job) {
        if ([CleanupJobFixture]::FailQuery) { throw 'private-query-sentinel' }
        return @(10, 12)
    }
    static [void] Close([IntPtr]$Job) { [CleanupJobFixture]::Closed++ }
}
class CleanupWriterFixture : IO.StringWriter {
    static [bool] $PrematureWrite
    [void] WriteLine([string]$Value) {
        if ([CleanupJobFixture]::Closed -ne 1 -or [CleanupProcessFixture]::Killed -notcontains 12) {
            [CleanupWriterFixture]::PrematureWrite = $true
        }
        ([IO.StringWriter]$this).Write($Value + [Environment]::NewLine)
    }
}
function Get-LiveProcessIdentity([int]$ProcessId) {
    if ($ProcessId -eq 12) { return '100' }
    return '200'
}

$savedTrace = $env:RESPIRE_AGENT_CLEANUP_DIAGNOSTICS
$originalError = [Console]::Error
$tracked = [Collections.Generic.Dictionary[int, string]]::new()
$tracked[12] = '100'
$tracked[21] = 'private-identity-sentinel'
$tracked[73] = '100'
try {
    foreach ($enabled in @($false, $true)) {
        $env:RESPIRE_AGENT_CLEANUP_DIAGNOSTICS = if ($enabled) { '1' } else { $null }
        [CleanupProcessFixture]::Killed.Clear()
        [CleanupJobFixture]::Closed = 0
        [CleanupWriterFixture]::PrematureWrite = $false
        $writer = [CleanupWriterFixture]::new()
        try {
            [Console]::SetError($writer)
            Stop-ProcessTree -RootProcess ([CleanupProcessFixture]@{ Id = 10 }) -TrackedProcesses $tracked -WindowsJobHandle 1 -UnixProcessGroupId 0
        }
        finally { [Console]::SetError($originalError) }
        $output = $writer.ToString()
        $writer.Dispose()
        if ([CleanupJobFixture]::Closed -ne 1 -or ([CleanupProcessFixture]::Killed -join ',') -ne '12') {
            throw 'Cleanup evidence changed containment or killed an unverified process.'
        }
        if ([CleanupWriterFixture]::PrematureWrite) { throw 'Console evidence ran before containment and fallback cleanup finished.' }
        if (-not $enabled -and $output) { throw 'Cleanup evidence is not opt-in.' }
        if ($enabled) {
            foreach ($expected in @('guard-cleanup .*guardPid=\d+ rootPid=10', 'job-members pids=10,12',
                'fallback pid=12 action=kill', 'fallback pid=21 action=skip-identity', 'fallback pid=73 action=unavailable')) {
                if ($output -notmatch $expected) { throw "Missing cleanup decision: $expected" }
            }
            if ($output -match 'private-.*sentinel') { throw 'Cleanup evidence leaked raw data.' }
        }
    }
    $env:RESPIRE_AGENT_CLEANUP_DIAGNOSTICS = '1'
    [CleanupJobFixture]::FailQuery = $true
    $tracked.Clear()
    foreach ($processId in 100..199) { $tracked[$processId] = '100' }
    $writer = [IO.StringWriter]::new()
    try {
        [Console]::SetError($writer)
        Stop-ProcessTree -RootProcess ([CleanupProcessFixture]@{ Id = 10 }) -TrackedProcesses $tracked -WindowsJobHandle 1 -UnixProcessGroupId 0
    }
    finally { [Console]::SetError($originalError) }
    $output = $writer.ToString()
    $writer.Dispose()
    if ($output -notmatch 'job-members unavailable=true' -or $output -match 'private-query-sentinel' -or
        ([regex]::Matches($output, 'fallback pid=')).Count -ne 64 -or $output -notmatch 'truncated=True') {
        throw 'Unavailable or bounded cleanup evidence was misleading.'
    }
    if ([CleanupJobFixture]::Closed -ne 2) { throw 'Failed diagnostics interrupted Job Object closure.' }

    # A failing console must not escape the writer or change cleanup decisions.
    $writer = [IO.StringWriter]::new()
    $writer.Dispose()
    try {
        [Console]::SetError($writer)
        Stop-ProcessTree -RootProcess ([CleanupProcessFixture]@{ Id = 10 }) -TrackedProcesses $tracked -WindowsJobHandle 1 -UnixProcessGroupId 0
    }
    finally { [Console]::SetError($originalError) }
    if ([CleanupJobFixture]::Closed -ne 3) { throw 'Failed console interrupted containment.' }

    # Unexpected failures still propagate, but must not discard decisions already
    # captured. Emission follows stack unwinding and pinned-handle disposal.
    foreach ($failureAt in @('root', 'fallback')) {
        foreach ($failConsole in @($false, $true)) {
            [CleanupProcessFixture]::Killed.Clear()
            [CleanupProcessFixture]::Disposed.Clear()
            [CleanupJobFixture]::Closed = 0
            [CleanupJobFixture]::FailQuery = $false
            $tracked.Clear()
            $tracked[12] = '100'
            [CleanupProcessFixture]::ThrowOnKillId = if ($failureAt -eq 'root') { 10 } else { 12 }
            $writer = [IO.StringWriter]::new()
            if ($failConsole) { $writer.Dispose() }
            $observedFailure = $null
            try {
                [Console]::SetError($writer)
                Stop-ProcessTree -RootProcess ([CleanupProcessFixture]@{ Id = 10; HasExited = $false }) -TrackedProcesses $tracked -WindowsJobHandle 1 -UnixProcessGroupId 0
            }
            catch { $observedFailure = $_ }
            finally { [Console]::SetError($originalError) }
            $output = $writer.ToString()
            $writer.Dispose()
            if ($null -eq $observedFailure -or
                $observedFailure.Exception.GetBaseException() -isnot [UnauthorizedAccessException] -or
                $observedFailure.Exception.Message -notmatch 'private-kill-sentinel') {
                throw 'Cleanup evidence replaced or suppressed the unexpected kill failure.'
            }
            if ([CleanupJobFixture]::Closed -ne 1 -or
                [CleanupProcessFixture]::Killed -contains [CleanupProcessFixture]::ThrowOnKillId) {
                throw 'Unexpected failure changed containment or recorded a successful failed kill.'
            }
            if ($failureAt -eq 'fallback' -and [CleanupProcessFixture]::Disposed -notcontains 12) {
                throw 'Unexpected fallback failure leaked the pinned process handle.'
            }
            if (-not $failConsole -and ($output -notmatch 'job-members pids=10,12' -or
                $output -notmatch 'containment action=close-job' -or
                $output -notmatch 'root pid=10 action=kill-tree')) {
                throw 'Unexpected cleanup failure discarded buffered evidence.'
            }
            if ($output -match 'private-.*sentinel' -or $output -match 'fallback pid=12 action=kill') {
                throw 'Unexpected cleanup failure leaked raw data or claimed a successful kill.'
            }
        }
    }
}
finally {
    [CleanupProcessFixture]::ThrowOnKillId = 0
    [Console]::SetError($originalError)
    $env:RESPIRE_AGENT_CLEANUP_DIAGNOSTICS = $savedTrace
}

if ($IsWindows) {
    Add-Type -Path (Join-Path $PSScriptRoot 'AgentDotNetWindowsJob.cs')
    $job = [AgentDotNetWindowsJob]::CreateKillOnClose()
    $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
    $events = [Collections.Generic.List[Threading.EventWaitHandle]]::new()
    try {
        if (@([AgentDotNetWindowsJob]::GetProcessIds($job)).Count -ne 0) { throw 'An empty guard job included unrelated processes.' }
        try {
            $null = [AgentDotNetWindowsJob]::GetProcessIds([IntPtr]::Zero)
            throw 'A null handle queried the enclosing runner job.'
        }
        catch [ArgumentException] { }
        foreach ($index in 1..2) {
            $readyName = "agent-cleanup-ready-$([guid]::NewGuid())"
            $releaseName = "agent-cleanup-release-$([guid]::NewGuid())"
            $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $readyName)
            $release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $releaseName)
            $events.Add($ready); $events.Add($release)
            $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
            $info.UseShellExecute = $false
            $info.WorkingDirectory = (Get-Location).ProviderPath
            $info.Environment['RESPIRE_CLEANUP_READY'] = $readyName
            $info.Environment['RESPIRE_CLEANUP_RELEASE'] = $releaseName
            foreach ($argument in @('-NoProfile', '-NonInteractive', '-Command',
                '$ready = [Threading.EventWaitHandle]::OpenExisting($env:RESPIRE_CLEANUP_READY); $release = [Threading.EventWaitHandle]::OpenExisting($env:RESPIRE_CLEANUP_RELEASE); $null = $ready.Set(); if (-not $release.WaitOne(30000)) { exit 1 }; exit 0')) {
                $info.ArgumentList.Add($argument)
            }
            $processes.Add([AgentDotNetWindowsJob]::StartWrapper($info, $job))
            if (-not $ready.WaitOne(15000)) { throw 'Job membership fixture did not enter its child.' }
        }
        $members = @([AgentDotNetWindowsJob]::GetProcessIds($job) | Sort-Object)
        if (@($processes.Id | Where-Object { $_ -notin $members }).Count -ne 0 -or $PID -in $members) {
            throw "Native Job Object membership missed contained children or included the caller: members=$($members -join ',') children=$($processes.Id -join ',')."
        }
        # Querying membership must leave both children running; closure still
        # terminates them, even though they have not received their release event.
        if ($processes.HasExited -contains $true) { throw 'Read-only membership evidence stopped a child.' }
    }
    finally {
        [AgentDotNetWindowsJob]::Close($job)
        foreach ($process in $processes) {
            try { if (-not $process.WaitForExit(5000)) { throw 'Membership fixture survived Job Object closure.' } }
            finally { $process.Dispose() }
        }
        foreach ($event in $events) { $event.Dispose() }
    }
    Write-Output 'OK native Job Object query includes both contained children, excludes the caller and preserves kill-on-close.'

    # Keep exact native membership without console-host descendants, 65 managed
    # runtimes or startup timing. These test-only children are never resumed.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class CleanupBoundaryChild
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateProcess(string application, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, int inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int AssignProcessToJobObject(IntPtr job, SafeProcessHandle process);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    public static Process Create(string executable, IntPtr job)
    {
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        // CREATE_SUSPENDED | DETACHED_PROCESS: no user code or console host runs.
        // lpCommandLine is null, so CreateProcessW uses the explicit module name.
        if (CreateProcess(executable, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0x0000000C,
            IntPtr.Zero, null, ref startup, out ProcessInformation created) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var thread = new SafeWaitHandle(created.Thread, ownsHandle: true);
        try
        {
            // The borrowed job is closed by the caller. The child cannot escape
            // before assignment because its only thread stays suspended.
            if (AssignProcessToJobObject(job, process) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Process result = Process.GetProcessById(checked((int)created.ProcessId));
            try { _ = result.SafeHandle; return result; }
            catch { result.Dispose(); throw; }
        }
        catch
        {
            // Creation succeeded, but the caller has not received ownership yet.
            if (TerminateProcess(process, 1) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(process, 5000) != 0)
                throw new InvalidOperationException("Suspended boundary fixture did not terminate.");
            throw;
        }
    }
}
'@
    $job = [AgentDotNetWindowsJob]::CreateKillOnClose()
    $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
    try {
        foreach ($index in 1..65) {
            $processes.Add([CleanupBoundaryChild]::Create(
                (Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), $job))
            if ($index -eq 64) {
                $members = @([AgentDotNetWindowsJob]::GetProcessIds($job) | Sort-Object)
                if ($members.Count -ne 64 -or
                    @($processes.Id | Where-Object { $_ -notin $members }).Count -ne 0) {
                    throw 'Native membership query did not return the complete 64-member boundary.'
                }
            }
        }
        $unavailable = $false
        try { $null = [AgentDotNetWindowsJob]::GetProcessIds($job) }
        catch [ComponentModel.Win32Exception] {
            # ERROR_MORE_DATA is the native buffer-overflow result.
            if ($_.Exception.NativeErrorCode -ne 234) { throw }
            $unavailable = $true
        }
        catch [InvalidOperationException] { $unavailable = $true }
        if (-not $unavailable) { throw 'Native membership query published a partial 65-member boundary.' }
        if ($processes.HasExited -contains $true) { throw 'A native boundary fixture exited before job closure.' }
    }
    finally {
        [AgentDotNetWindowsJob]::Close($job)
        foreach ($process in $processes) {
            try { if (-not $process.WaitForExit(5000)) { throw 'Native boundary fixture survived Job Object closure.' } }
            finally { $process.Dispose() }
        }
    }
    Write-Output 'OK native Job Object query returns all 64 members and rejects a 65-member partial boundary.'
}
Write-Output 'OK cleanup evidence is opt-in, bounded and sanitized; identity skips and diagnostic failures preserve containment.'
exit 0
