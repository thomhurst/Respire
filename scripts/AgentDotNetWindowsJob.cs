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
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ProcThreadAttributeDesktopAppPolicy = 0x00020012;
    private const int DesktopAppBreakawayDisableProcessTree = 2;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint DuplicateSameAccess = 2;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
        out SafeFileHandle targetHandle, uint access, int inherit, uint options);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

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
        using var input = DuplicateStandardHandle(-10, GenericRead);
        using var output = DuplicateStandardHandle(-11, GenericWrite);
        using var error = DuplicateStandardHandle(-12, GenericWrite);
        uint attributeCount = packaged ? 3u : 2u;
        UIntPtr attributeSize = UIntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attributeSize);
        int sizeError = Marshal.GetLastWin32Error();
        if (attributeSize == UIntPtr.Zero || sizeError != ErrorInsufficientBuffer) throw new Win32Exception(sizeError);
        IntPtr attributes = Marshal.AllocHGlobal(checked((int)attributeSize.ToUInt64()));
        IntPtr jobList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
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
            // Explicitly inherit only owned standard-handle duplicates. Other
            // inheritable handles in this process must never enter the wrapper.
            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, error.DangerousGetHandle());
            if (UpdateProcThreadAttribute(attributes, 0, new UIntPtr(ProcThreadAttributeHandleList), handleList,
                new UIntPtr((uint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
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
                    StandardInput = input.DangerousGetHandle(), StandardOutput = output.DangerousGetHandle(), StandardError = error.DangerousGetHandle(),
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
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(desktopPolicy);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static SafeFileHandle DuplicateStandardHandle(int identifier, uint access)
    {
        IntPtr original = GetStdHandle(identifier);
        using var fallback = original == IntPtr.Zero || original == new IntPtr(-1)
            ? CreateFile("NUL", access, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero)
            : null;
        if (fallback is not null)
        {
            if (fallback.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            original = fallback.DangerousGetHandle();
        }
        IntPtr currentProcess = GetCurrentProcess();
        if (DuplicateHandle(currentProcess, original, currentProcess, out SafeFileHandle inherited,
            0, 1, DuplicateSameAccess) == 0)
        {
            int duplicateError = Marshal.GetLastWin32Error();
            inherited?.Dispose();
            throw new Win32Exception(duplicateError);
        }
        return inherited;
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
