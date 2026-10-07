<#
.SYNOPSIS
Runs a local dotnet command with workstation-safe limits for autonomous agents.

.DESCRIPTION
Disables reusable MSBuild/Roslyn servers, optionally limits MSBuild to one node,
uses below-normal priority, and kills the full process tree when time or memory
limits are exceeded. Exit 124 means timeout; exit 137 means memory limit.
For SingleNode tests, recognized DOTNET_TEST_RUNNER overrides apply on .NET 11
Preview 6 and later. A contained probe resolves the effective SDK; older SDKs,
empty overrides, and unknown values retain nearest-global.json runner selection.

.EXAMPLE
& scripts/Invoke-AgentDotNet.ps1 -SingleNode `
    -DotNetArguments @('build', 'Respire.slnx', '-c', 'Release')

.NOTES
SingleNode adds -m:1 for MSBuild commands and VSTest-mode dotnet test.
For Microsoft.Testing.Platform selected by the nearest global.json, build
separately with SingleNode, then invoke test --no-build; MTP test arguments
are forwarded unchanged. All modes retain the same process/resource guard.
Recognized DOTNET_TEST_RUNNER values override global.json on .NET 11 Preview 6
and later. A probe inside the containment boundary resolves the executable's
effective SDK; .NET 10 ignores the environment override.

.EXAMPLE
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'build', 'tests/Respire.Tests/Respire.Tests.csproj', '-c', 'Release', '-f', 'net10.0')
# After a successful build, run the focused MTP tests without another build:
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'test', '--project', 'tests/Respire.Tests/Respire.Tests.csproj', '-c', 'Release',
    '-f', 'net10.0', '--no-build', '--treenode-filter', '/*/*/RespireConnectionTests/*')
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateRange(1, 86400)]
    [int]$TimeoutSeconds = 600,

    [ValidateRange(64, 131072)]
    [int]$MemoryLimitMb = 2048,

    [ValidateRange(50, 10000)]
    [int]$PollIntervalMilliseconds = 500,

    [switch]$SingleNode,

    [ValidateNotNullOrEmpty()]
    [string]$DotNetPath = 'dotnet',

    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [AllowEmptyString()]
    [string[]]$DotNetArguments
)

$ErrorActionPreference = 'Stop'

if ($IsWindows) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class AgentDotNetWindowsJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const uint ProcThreadAttributeJobList = 0x0002000D;
    private const uint ProcThreadAttributeDesktopAppPolicy = 0x00020012;
    private const int DesktopAppBreakawayDisableProcessTree = 2;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

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
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int InitializeProcThreadAttributeList(IntPtr list, uint count, uint flags, ref UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateProcess(string application, [In, Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, int inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);

    public static Process StartWrapper(ProcessStartInfo info, IntPtr job)
    {
        // Associate at creation, before any managed runtime or child-process launch.
        // The named start gate still delays the workload until priority is lowered.
        uint packageNameLength = 0;
        int packageStatus = GetCurrentPackageFullName(ref packageNameLength, IntPtr.Zero);
        bool packaged = packageStatus == ErrorInsufficientBuffer;
        if (!packaged && packageStatus != AppModelErrorNoPackage) throw new Win32Exception(packageStatus);
        uint attributeCount = packaged ? 2u : 1u;
        UIntPtr attributeSize = UIntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attributeSize);
        int sizeError = Marshal.GetLastWin32Error();
        if (attributeSize == UIntPtr.Zero || sizeError != ErrorInsufficientBuffer) throw new Win32Exception(sizeError);
        IntPtr attributes = Marshal.AllocHGlobal(checked((int)attributeSize.ToUInt64()));
        IntPtr jobList = IntPtr.Zero;
        IntPtr desktopPolicy = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        bool initialized = false;
        try
        {
            if (InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref attributeSize) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job);
            if (UpdateProcThreadAttribute(attributes, 0, new UIntPtr(ProcThreadAttributeJobList), jobList,
                new UIntPtr((uint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (packaged)
            {
                // Store-installed PowerShell otherwise breaks native children away
                // from its job. Keep the entire guarded tree in the same environment.
                desktopPolicy = Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(desktopPolicy, DesktopAppBreakawayDisableProcessTree);
                if (UpdateProcThreadAttribute(attributes, 0, new UIntPtr(ProcThreadAttributeDesktopAppPolicy), desktopPolicy,
                    new UIntPtr(sizeof(uint)), IntPtr.Zero, IntPtr.Zero) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            string environmentBlock = string.Concat(info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value + '\0')) + '\0';
            environment = Marshal.StringToHGlobalUni(environmentBlock);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles,
                    StandardInput = GetStdHandle(-10), StandardOutput = GetStdHandle(-11), StandardError = GetStdHandle(-12),
                },
                Attributes = attributes,
            };
            // Only the generated wrapper path, fixed switches, and random gate name
            // enter this command line. Workload argv remains in the JSON payload.
            string commandLine = string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(QuoteWrapperArgument)) + '\0';
            if (CreateProcess(info.FileName, commandLine.ToCharArray(), IntPtr.Zero, IntPtr.Zero, 1,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, environment, info.WorkingDirectory,
                ref startup, out ProcessInformation created) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var processHandle = new SafeProcessHandle(created.Process, ownsHandle: true);
            using var threadHandle = new SafeWaitHandle(created.Thread, ownsHandle: true);
            Process result = Process.GetProcessById(checked((int)created.ProcessId));
            try { _ = result.SafeHandle; return result; }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(desktopPolicy);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static string QuoteWrapperArgument(string value)
    {
        // Windows filenames cannot contain quotes. These generated arguments do
        // not end in a separator; never use this helper for arbitrary workload argv.
        if (value.Contains('"') || value.EndsWith('\\')) throw new ArgumentException("Invalid wrapper launch argument.");
        return "\"" + value + "\"";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr CreateKillOnClose()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception();
        }

        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        IntPtr information = Marshal.AllocHGlobal(size);
        try
        {
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            Marshal.StructureToPtr(limits, information, false);
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    information,
                    (uint)size))
            {
                throw new Win32Exception();
            }

            return job;
        }
        catch
        {
            CloseHandle(job);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    public static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
        }
    }
}
'@
}
else {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

public static class AgentDotNetUnixNative
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int processId, int signal);
}

public sealed class AgentDotNetUnixProcessInfo
{
    public int ProcessId { get; set; }
    public int ParentProcessId { get; set; }
    public long WorkingSetBytes { get; set; }
    public string StartIdentity { get; set; }
}

public static class AgentDotNetLinuxProcessSnapshot
{
    public static AgentDotNetUnixProcessInfo[] Capture()
    {
        var processes = new List<AgentDotNetUnixProcessInfo>();
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            string name = Path.GetFileName(directory);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
            {
                continue;
            }

            AgentDotNetUnixProcessInfo process = CaptureProcess(processId);
            if (process != null)
            {
                processes.Add(process);
            }
        }

        return processes.ToArray();
    }

    public static AgentDotNetUnixProcessInfo CaptureProcess(int processId)
    {
        try
        {
            string stat = File.ReadAllText(
                Path.Combine("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat"));
            int commandEnd = stat.LastIndexOf(") ", StringComparison.Ordinal);
            if (commandEnd < 0)
            {
                return null;
            }

            string[] fields = stat.Substring(commandEnd + 2)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 22)
            {
                return null;
            }

            return new AgentDotNetUnixProcessInfo
            {
                ProcessId = processId,
                ParentProcessId = int.Parse(fields[1], CultureInfo.InvariantCulture),
                StartIdentity = fields[19],
                WorkingSetBytes =
                    long.Parse(fields[21], CultureInfo.InvariantCulture) * Environment.SystemPageSize,
            };
        }
        catch (IOException)
        {
            // The process exited while its snapshot was being read.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // The process cannot be inspected by this user.
            return null;
        }
    }
}

public static class AgentDotNetMacProcessSnapshot
{
    private const int ProcPidTBsdInfo = 3;
    private const int ProcPidTaskInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcBsdInfo
    {
        public uint Flags;
        public uint Status;
        public uint ExitStatus;
        public uint ProcessId;
        public uint ParentProcessId;
        public uint UserId;
        public uint GroupId;
        public uint RealUserId;
        public uint RealGroupId;
        public uint SavedUserId;
        public uint SavedGroupId;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Command;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Name;
        public uint OpenFileCount;
        public uint ProcessGroupId;
        public uint JobControlCount;
        public uint ControllingTerminalDevice;
        public uint TerminalProcessGroupId;
        public int Nice;
        public ulong StartTimeSeconds;
        public ulong StartTimeMicroseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcTaskInfo
    {
        public ulong VirtualSize;
        public ulong ResidentSize;
        public ulong TotalUserTime;
        public ulong TotalSystemTime;
        public ulong ThreadsUserTime;
        public ulong ThreadsSystemTime;
        public int Policy;
        public int Faults;
        public int PageIns;
        public int CopyOnWriteFaults;
        public int MessagesSent;
        public int MessagesReceived;
        public int MachSystemCalls;
        public int UnixSystemCalls;
        public int ContextSwitches;
        public int ThreadCount;
        public int RunningThreadCount;
        public int Priority;
    }

    [DllImport("/usr/lib/libproc.dylib")]
    private static extern int proc_listallpids([Out] int[] buffer, int bufferSize);

    [DllImport("/usr/lib/libproc.dylib")]
    private static extern int proc_pidinfo(
        int processId,
        int flavor,
        ulong argument,
        IntPtr buffer,
        int bufferSize);

    public static AgentDotNetUnixProcessInfo[] Capture()
    {
        var processIds = new int[131072];
        int processCount = proc_listallpids(processIds, processIds.Length * sizeof(int));
        var processes = new List<AgentDotNetUnixProcessInfo>(Math.Max(processCount, 0));

        for (int index = 0; index < processCount && index < processIds.Length; index++)
        {
            AgentDotNetUnixProcessInfo process = CaptureProcess(processIds[index]);
            if (process != null)
            {
                processes.Add(process);
            }
        }

        return processes.ToArray();
    }

    public static AgentDotNetUnixProcessInfo CaptureProcess(int processId)
    {
        if (processId <= 0 ||
            !TryRead(processId, ProcPidTBsdInfo, out ProcBsdInfo before))
        {
            return null;
        }

        TryRead(processId, ProcPidTaskInfo, out ProcTaskInfo task);
        if (!TryRead(processId, ProcPidTBsdInfo, out ProcBsdInfo after) ||
            before.ProcessId != after.ProcessId ||
            before.ParentProcessId != after.ParentProcessId ||
            before.StartTimeSeconds != after.StartTimeSeconds ||
            before.StartTimeMicroseconds != after.StartTimeMicroseconds)
        {
            return null;
        }

        return new AgentDotNetUnixProcessInfo
        {
            ProcessId = processId,
            ParentProcessId = (int)after.ParentProcessId,
            WorkingSetBytes = checked((long)task.ResidentSize),
            StartIdentity = string.Concat(
                after.StartTimeSeconds.ToString(CultureInfo.InvariantCulture),
                ":",
                after.StartTimeMicroseconds.ToString(CultureInfo.InvariantCulture)),
        };
    }

    private static bool TryRead<T>(int processId, int flavor, out T value)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (proc_pidinfo(processId, flavor, 0, buffer, size) != size)
            {
                value = default(T);
                return false;
            }

            value = Marshal.PtrToStructure<T>(buffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
'@
}

function Get-ProcessSnapshot {
    if ($IsWindows) {
        return Get-CimInstance Win32_Process `
            -Property ProcessId, ParentProcessId, WorkingSetSize, CreationDate |
            ForEach-Object {
                [pscustomobject]@{
                    ProcessId = [int]$_.ProcessId
                    ParentProcessId = [int]$_.ParentProcessId
                    WorkingSetBytes = [long]$_.WorkingSetSize
                    StartIdentity = $_.CreationDate.ToUniversalTime().Ticks.ToString(
                        [Globalization.CultureInfo]::InvariantCulture)
                }
            }
    }

    if ($IsMacOS) {
        return [AgentDotNetMacProcessSnapshot]::Capture()
    }

    return [AgentDotNetLinuxProcessSnapshot]::Capture()
}

function Get-LiveProcessIdentity([int]$ProcessId) {
    try {
        if ($IsWindows) {
            $process = Get-CimInstance Win32_Process `
                -Filter "ProcessId = $ProcessId" `
                -Property CreationDate
            if ($null -eq $process) {
                return $null
            }

            return $process.CreationDate.ToUniversalTime().Ticks.ToString(
                [Globalization.CultureInfo]::InvariantCulture)
        }

        $process = if ($IsMacOS) {
            [AgentDotNetMacProcessSnapshot]::CaptureProcess($ProcessId)
        }
        else {
            [AgentDotNetLinuxProcessSnapshot]::CaptureProcess($ProcessId)
        }

        return $process?.StartIdentity
    }
    catch {
        Write-Verbose "Live identity for process $ProcessId was unavailable: $_"
        return $null
    }
}

function Get-ProcessTreeState([int]$RootProcessId) {
    $snapshot = @(Get-ProcessSnapshot)
    $processIds = [System.Collections.Generic.HashSet[int]]::new()
    $null = $processIds.Add($RootProcessId)

    do {
        $added = $false
        foreach ($item in $snapshot) {
            if ($processIds.Contains($item.ParentProcessId) -and $processIds.Add($item.ProcessId)) {
                $added = $true
            }
        }
    }
    while ($added)

    $treeProcesses = @($snapshot | Where-Object { $processIds.Contains($_.ProcessId) })
    return [pscustomobject]@{
        Snapshot = $snapshot
        Processes = $treeProcesses
    }
}

function Sync-TrackedProcesses(
    [System.Collections.Generic.Dictionary[int, string]]$TrackedProcesses,
    [object[]]$Snapshot,
    [object[]]$TreeProcesses) {
    $currentProcesses = @{}
    foreach ($process in $Snapshot) {
        $currentProcesses[$process.ProcessId] = $process
    }

    foreach ($processId in @($TrackedProcesses.Keys)) {
        $currentProcess = $currentProcesses[$processId]
        if (($null -eq $currentProcess) -or
            ($currentProcess.StartIdentity -ne $TrackedProcesses[$processId])) {
            $null = $TrackedProcesses.Remove($processId)
        }
    }

    foreach ($process in $TreeProcesses) {
        $TrackedProcesses[$process.ProcessId] = $process.StartIdentity
    }
}

function Get-TrackedWorkingSetBytes(
    [System.Collections.Generic.Dictionary[int, string]]$TrackedProcesses,
    [object[]]$Snapshot) {
    $workingSetBytes = ($Snapshot |
        Where-Object {
            $TrackedProcesses.ContainsKey($_.ProcessId) -and
            $TrackedProcesses[$_.ProcessId] -eq $_.StartIdentity
        } |
        Measure-Object -Property WorkingSetBytes -Sum).Sum

    return [long]($workingSetBytes ?? 0)
}

function Stop-ProcessTree(
    [System.Diagnostics.Process]$RootProcess,
    [System.Collections.Generic.Dictionary[int, string]]$TrackedProcesses,
    [IntPtr]$WindowsJobHandle,
    [int]$UnixProcessGroupId) {
    if ($IsWindows -and $WindowsJobHandle -ne [IntPtr]::Zero) {
        # Closing a kill-on-close Job Object catches descendants that exited the
        # parent-PID tree before the final snapshot.
        [AgentDotNetWindowsJob]::Close($WindowsJobHandle)
    }
    elseif ((-not $IsWindows) -and $UnixProcessGroupId -gt 0) {
        # Signal the process group from the guard so timeout and memory-limit paths
        # have the same containment as normal exit.
        $killResult = [AgentDotNetUnixNative]::kill(-$UnixProcessGroupId, 9)
        if ($killResult -ne 0) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($errorCode -ne 3) {
                Write-Verbose "Unix process group $UnixProcessGroupId could not be stopped (errno $errorCode)."
            }
        }
    }

    try {
        if (-not $RootProcess.HasExited) {
            $RootProcess.Kill($true)
        }
    }
    catch [System.InvalidOperationException] {
        Write-Verbose 'Root process exited before process-tree cleanup completed.'
    }
    catch [System.ComponentModel.Win32Exception] {
        Write-Verbose "Root process-tree cleanup was unavailable: $_"
    }

    # The Job Object/process group is the containment boundary. Identity-checked
    # tracked cleanup is defense in depth for processes seen before reparenting.
    foreach ($trackedProcess in $TrackedProcesses.GetEnumerator()) {
        $processId = $trackedProcess.Key
        if ($processId -eq $PID) {
            continue
        }

        try {
            $candidate = [System.Diagnostics.Process]::GetProcessById($processId)
            try {
                # Pin the native process handle before checking identity so Windows
                # cleanup cannot reopen a recycled PID between verification and kill.
                $null = $candidate.SafeHandle

                # Re-read this PID immediately before acting. A bulk snapshot taken
                # before the loop can become stale and target a recycled PID.
                $liveIdentity = Get-LiveProcessIdentity -ProcessId $processId
                if (($null -eq $liveIdentity) -or
                    ($liveIdentity -ne $trackedProcess.Value)) {
                    continue
                }

                # Primary containment already handled descendants. Kill only this
                # identity-verified fallback process through the pinned handle.
                $candidate.Kill()
            }
            finally {
                $candidate.Dispose()
            }
        }
        catch [System.ArgumentException] {
            Write-Verbose "Tracked process $processId already exited."
        }
        catch [System.InvalidOperationException] {
            Write-Verbose "Tracked process $processId became unavailable during cleanup."
        }
        catch [System.ComponentModel.Win32Exception] {
            Write-Verbose "Tracked process $processId could not be stopped: $_"
        }
    }
}

function Test-MicrosoftTestingPlatform {
    # The CLI selects global.json from its working directory, not the project path.
    $directory = [IO.DirectoryInfo]::new((Get-Location).ProviderPath)
    while ($null -ne $directory) {
        $globalJson = Join-Path $directory.FullName 'global.json'
        if (Test-Path -LiteralPath $globalJson -PathType Leaf) {
            try {
                $configuration = Get-Content -LiteralPath $globalJson -Raw | ConvertFrom-Json
            }
            catch {
                throw "Cannot read test runner configuration '$globalJson': $($_.Exception.Message)"
            }
            return $configuration.test.runner -eq 'Microsoft.Testing.Platform'
        }
        $directory = $directory.Parent
    }
    return $false
}

function Add-SingleNodeArgument([string[]]$Arguments, [bool]$TestUsesMicrosoftTestingPlatform) {
    if (-not $SingleNode -or $Arguments.Count -eq 0) {
        return $Arguments
    }

    $verb = $Arguments[0]
    if ($verb -notin @('build', 'test', 'pack', 'publish', 'msbuild')) {
        return $Arguments
    }
    # MTP forwards unknown switches to test applications. -m:1 is not an MTP option.
    # Build separately with SingleNode, then use test --no-build in this mode.
    if ($verb -eq 'test' -and $TestUsesMicrosoftTestingPlatform) {
        return $Arguments
    }
    $separatorIndex = [Array]::IndexOf($Arguments, '--')
    $buildArguments = if ($separatorIndex -lt 0) { $Arguments } else { $Arguments[0..($separatorIndex - 1)] }
    $alreadyConfigured = $buildArguments |
        Where-Object { $_ -match '^(?:[-/]m(?:axcpucount)?|--maxcpucount)(?::|$)' } |
        Select-Object -First 1
    if ($alreadyConfigured) {
        return $Arguments
    }

    if ($separatorIndex -lt 0) {
        return @($Arguments) + '-m:1'
    }

    return @($Arguments[0..($separatorIndex - 1)]) +
        '-m:1' +
        @($Arguments[$separatorIndex..($Arguments.Count - 1)])
}

$isSingleNodeTest = $SingleNode -and $DotNetArguments.Count -gt 0 -and $DotNetArguments[0] -eq 'test'
$globalRunnerIsMtp = $isSingleNodeTest -and (Test-MicrosoftTestingPlatform)
$effectiveArguments = @(Add-SingleNodeArgument $DotNetArguments $globalRunnerIsMtp)
$probeSdk = $isSingleNodeTest -and $env:DOTNET_TEST_RUNNER -in @('VSTest', 'Microsoft.Testing.Platform')
$overrideArguments = if ($probeSdk) {
    @(Add-SingleNodeArgument $DotNetArguments ($env:DOTNET_TEST_RUNNER -eq 'Microsoft.Testing.Platform'))
}
else { @() }
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.UseShellExecute = $false
# Match the PowerShell location used to resolve global.json and relative paths.
$startInfo.WorkingDirectory = (Get-Location).ProviderPath
$startInfo.Environment['BuildInParallel'] = 'false'
$startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
$startInfo.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
$startInfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
$startInfo.Environment['UseSharedCompilation'] = 'false'
# -File reparses colon switches. Pass only a random payload path through the
# environment so the invocation retains the platform's native argv size limit.
$invocationPath = [IO.Path]::Combine(
    [IO.Path]::GetTempPath(), "agent-dotnet-invocation-$([guid]::NewGuid()).json")
$startInfo.Environment['RESPIRE_AGENT_DOTNET_INVOCATION'] = $invocationPath
$pwshPath = (Get-Process -Id $PID).Path
$wrapperPath = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "agent-dotnet-wrapper-$([guid]::NewGuid()).ps1"
)

if ($IsWindows) {
    # Job association is atomic at creation. The wrapper waits on a gate so the
    # guard can lower its priority before it launches dotnet or any descendants.
    $wrapperScript = @'
$startGate = [Threading.EventWaitHandle]::OpenExisting($args[0])
try {
    if (-not $startGate.WaitOne([TimeSpan]::FromSeconds(30))) {
        [Console]::Error.WriteLine('Agent dotnet guard launch gate timed out.')
        exit 126
    }
}
finally {
    $startGate.Dispose()
}


'@
    $startInfo.FileName = $pwshPath
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($wrapperPath)
    $startInfo.ArgumentList.Add('{WINDOWS_START_GATE}')
}
else {
    # PowerShell is already a guard prerequisite, so libc calls provide portable
    # process-group containment without requiring setsid(1) or Python on macOS.
    $wrapperScript = @'
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;

public static class AgentDotNetUnixChildNative
{
    [DllImport("libc", SetLastError = true)]
    public static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    public static extern int setpriority(int which, int who, int priority);
}
"@

if ([AgentDotNetUnixChildNative]::setsid() -lt 0) {
    $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    [Console]::Error.WriteLine("Agent dotnet guard could not create a Unix session (errno $errorCode).")
    exit 126
}

if ([AgentDotNetUnixChildNative]::setpriority(0, 0, 10) -ne 0) {
    $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    [Console]::Error.WriteLine("Agent dotnet guard could not lower Unix priority (errno $errorCode).")
}


'@
    $startInfo.FileName = $pwshPath
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-NonInteractive')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($wrapperPath)
}

# Each prefix establishes OS containment; this shared tail decodes data and launches the workload.
$wrapperScript += @'
$ErrorActionPreference = 'Stop'
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = [Environment]::CurrentDirectory
$invocationPath = $env:RESPIRE_AGENT_DOTNET_INVOCATION
$startInfo.Environment.Remove('RESPIRE_AGENT_DOTNET_INVOCATION') | Out-Null
try {
    # JsonDocument.GetString preserves date-like strings without PowerShell type inference.
    $invocation = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($invocationPath))
    try {
        $startInfo.FileName = $invocation.RootElement.GetProperty('Executable').GetString()
        foreach ($argument in $invocation.RootElement.GetProperty('Arguments').EnumerateArray()) {
            $startInfo.ArgumentList.Add($argument.GetString())
        }
        $probeSdk = $invocation.RootElement.GetProperty('ProbeSdk').GetBoolean()
        $overrideArguments = @($invocation.RootElement.GetProperty('RunnerOverrideArguments').EnumerateArray() |
            ForEach-Object { $_.GetString() })
    }
    finally {
        $invocation.Dispose()
    }
}
finally {
    # The workload never needs the payload. The parent also cleans up launch failures.
    [IO.File]::Delete($invocationPath)
}

if ($probeSdk) {
    # Run only after the wrapper has entered its Job Object/Unix session. The same
    # timeout, memory limit, priority, and descendant cleanup cover probe and command.
    # Ask the actual executable in the actual cwd; pins alone cannot resolve roll-forward.
    $probeInfo = [Diagnostics.ProcessStartInfo]::new()
    $probeInfo.FileName = $startInfo.FileName
    $probeInfo.UseShellExecute = $false
    $probeInfo.WorkingDirectory = $startInfo.WorkingDirectory
    $probeInfo.Environment.Remove('RESPIRE_AGENT_DOTNET_INVOCATION') | Out-Null
    $probeInfo.RedirectStandardOutput = $true
    $probeInfo.RedirectStandardError = $true
    $probeInfo.ArgumentList.Add('--version')
    $probe = [Diagnostics.Process]::Start($probeInfo)
    try {
        # --version emits one line. A surviving probe descendant can inherit the
        # pipe, so waiting for EOF would prevent the workload from starting.
        $output = $probe.StandardOutput.ReadLineAsync()
        $errorOutput = $probe.StandardError.ReadLineAsync()
        $probe.WaitForExit()
        if ($probe.ExitCode -ne 0) {
            $errorText = if ($errorOutput.IsCompletedSuccessfully) { $errorOutput.GetAwaiter().GetResult() } else { '' }
            throw "SDK runner probe failed with exit $($probe.ExitCode): $errorText"
        }
        $versionText = ($output.GetAwaiter().GetResult() ?? '').Trim()
        try { $sdkVersion = [semver]$versionText }
        catch { throw "Cannot determine effective SDK from '$versionText'." }
        if ($sdkVersion -ge [semver]'11.0.100-preview.6') {
            $startInfo.ArgumentList.Clear()
            foreach ($argument in $overrideArguments) { $startInfo.ArgumentList.Add($argument) }
        }
    }
    finally { $probe.Dispose() }
}

$child = [Diagnostics.Process]::Start($startInfo)
try {
    $child.WaitForExit()
    $exitCode = $child.ExitCode
}
finally {
    $child.Dispose()
}

exit $exitCode
'@

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$processStarted = $false
$trackedProcesses = [System.Collections.Generic.Dictionary[int, string]]::new()
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$memoryLimitBytes = [long]$MemoryLimitMb * 1MB
$guardExitCode = $null
$finalExitCode = 1
$windowsJobHandle = [IntPtr]::Zero
$windowsStartGate = $null
$unixProcessGroupId = 0

try {
    $invocationBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Compress -Depth 3 -InputObject @{
        Executable = $DotNetPath
        Arguments = $effectiveArguments
        ProbeSdk = [bool]$probeSdk
        RunnerOverrideArguments = @($overrideArguments)
    }))
    $payloadOptions = [IO.FileStreamOptions]::new()
    $payloadOptions.Mode = [IO.FileMode]::CreateNew
    $payloadOptions.Access = [IO.FileAccess]::Write
    $payloadOptions.Share = [IO.FileShare]::None
    if (-not $IsWindows) {
        $payloadOptions.UnixCreateMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
    }
    # Windows inherits the current user's temporary-directory ACL. Unix creates mode 0600.
    $payload = [IO.FileStream]::new($invocationPath, $payloadOptions)
    try {
        $payload.Write($invocationBytes, 0, $invocationBytes.Length)
    }
    finally {
        $payload.Dispose()
    }

    # -File works on supported PowerShell versions; -CommandWithArgs was
    # experimental before PowerShell 7.5.
    [System.IO.File]::WriteAllText(
        $wrapperPath,
        $wrapperScript,
        [System.Text.UTF8Encoding]::new($false))

    if ($IsWindows) {
        $windowsJobHandle = [AgentDotNetWindowsJob]::CreateKillOnClose()
        $windowsStartGateName = "agent-dotnet-guard-$([guid]::NewGuid())"
        $windowsStartGate = [Threading.EventWaitHandle]::new(
            $false,
            [Threading.EventResetMode]::ManualReset,
            $windowsStartGateName)
        $gateArgumentIndex = $startInfo.ArgumentList.IndexOf('{WINDOWS_START_GATE}')
        $startInfo.ArgumentList[$gateArgumentIndex] = $windowsStartGateName
    }

    if ($IsWindows) {
        $process.Dispose()
        $process = [AgentDotNetWindowsJob]::StartWrapper($startInfo, $windowsJobHandle)
    }
    else {
        if (-not $process.Start()) { throw "Failed to start '$DotNetPath'." }
        $unixProcessGroupId = $process.Id
    }
    $processStarted = $true

    try {
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
    }
    catch {
        Write-Verbose "Could not lower process priority: $_"
    }

    if ($IsWindows) {
        $null = $windowsStartGate.Set()
    }

    while ($true) {
        $treeState = Get-ProcessTreeState $process.Id
        Sync-TrackedProcesses $trackedProcesses @($treeState.Snapshot) @($treeState.Processes)
        $workingSetBytes = Get-TrackedWorkingSetBytes $trackedProcesses @($treeState.Snapshot)

        if ($workingSetBytes -gt $memoryLimitBytes) {
            $workingSetMb = [math]::Round($workingSetBytes / 1MB)
            [Console]::Error.WriteLine(
                "Agent dotnet command exceeded ${MemoryLimitMb} MB process-tree limit (${workingSetMb} MB).")
            $guardExitCode = 137
            break
        }

        $remaining = $deadline - [DateTimeOffset]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            [Console]::Error.WriteLine(
                "Agent dotnet command exceeded ${TimeoutSeconds}s timeout.")
            $guardExitCode = 124
            break
        }

        $waitMilliseconds = [math]::Min(
            $PollIntervalMilliseconds,
            [math]::Max(1, [math]::Ceiling($remaining.TotalMilliseconds)))
        if ($process.WaitForExit([int]$waitMilliseconds)) {
            $treeState = Get-ProcessTreeState $process.Id
            Sync-TrackedProcesses $trackedProcesses @($treeState.Snapshot) @($treeState.Processes)

            if ([DateTimeOffset]::UtcNow -gt $deadline) {
                [Console]::Error.WriteLine(
                    "Agent dotnet command exceeded ${TimeoutSeconds}s timeout.")
                $guardExitCode = 124
            }

            break
        }
    }

    if ($null -ne $guardExitCode) {
        $finalExitCode = $guardExitCode
    }
    else {
        $process.WaitForExit()
        $finalExitCode = $process.ExitCode
    }
}
finally {
    if ($processStarted) {
        Stop-ProcessTree `
            -RootProcess $process `
            -TrackedProcesses $trackedProcesses `
            -WindowsJobHandle $windowsJobHandle `
            -UnixProcessGroupId $unixProcessGroupId
        $windowsJobHandle = [IntPtr]::Zero
    }
    elseif ($windowsJobHandle -ne [IntPtr]::Zero) {
        [AgentDotNetWindowsJob]::Close($windowsJobHandle)
    }

    if ($null -ne $windowsStartGate) {
        $windowsStartGate.Dispose()
    }

    $process.Dispose()

    if (Test-Path -LiteralPath $wrapperPath) {
        Remove-Item -LiteralPath $wrapperPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $invocationPath) {
        Remove-Item -LiteralPath $invocationPath -Force -ErrorAction SilentlyContinue
    }
}

exit $finalExitCode
