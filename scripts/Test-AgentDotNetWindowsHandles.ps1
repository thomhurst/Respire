$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { Write-Output 'SKIP Windows launcher handle controls.'; exit 0 }

Add-Type -Path (Join-Path $PSScriptRoot 'AgentDotNetWindowsJob.cs')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-handles-{0}" -f [guid]::NewGuid())
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $nativeSource = Join-Path $testRoot 'HandleProbe.cs'
    @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AgentGuardHandleProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int SetStdHandle(int identifier, IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEvent(uint access, int inherit, string name);
    [DllImport("kernelbase.dll")]
    private static extern int CompareObjectHandles(IntPtr first, SafeWaitHandle second);
    public static bool Inherited(long value, string name)
    {
        var handle = new IntPtr(value);
        if (GetHandleInformation(handle, out _) == 0) return false;
        // Open only after checking the original number, so a new handle cannot
        // occupy an unused number and create a false inheritance result.
        using var expected = OpenEvent(0x00100000, 0, name);
        if (expected.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return CompareObjectHandles(handle, expected) != 0;
    }
    public static bool StandardHandlesValid()
        => GetHandleInformation(GetStdHandle(-10), out _) != 0
        && GetHandleInformation(GetStdHandle(-11), out _) != 0
        && GetHandleInformation(GetStdHandle(-12), out _) != 0;
}
'@ | Set-Content -LiteralPath $nativeSource
    Add-Type -Path $nativeSource
    $childScript = Join-Path $testRoot 'Capture-Handles.ps1'
    @'
param([string]$NativeSource, [long]$Sentinel, [string]$EventName, [string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -Path $NativeSource
$result = @{
    Inherited = [AgentGuardHandleProbe]::Inherited($Sentinel, $EventName)
    StandardHandlesValid = [AgentGuardHandleProbe]::StandardHandlesValid()
    Input = [Console]::In.ReadLine()
}
[Console]::Out.WriteLine('guard-output')
[Console]::Error.WriteLine('guard-error')
[IO.File]::WriteAllText($Output, (ConvertTo-Json -Compress -InputObject $result))
'@ | Set-Content -LiteralPath $childScript

    $eventName = "agent-dotnet-sentinel-$([guid]::NewGuid())"
    $sentinel = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)
    try {
        if ([AgentGuardHandleProbe]::SetHandleInformation($sentinel.SafeWaitHandle, 1, 1) -eq 0) {
            throw 'Could not mark the sentinel inheritable.'
        }
        # Verify the probe recognizes the exact event, rather than an arbitrary valid handle.
        if (-not [AgentGuardHandleProbe]::Inherited($sentinel.SafeWaitHandle.DangerousGetHandle().ToInt64(), $eventName)) {
            throw 'Sentinel identity positive control failed.'
        }
        foreach ($missingHandles in @($false, $true)) {
            $inputPipe = [IO.Pipes.AnonymousPipeServerStream]::new([IO.Pipes.PipeDirection]::Out, [IO.HandleInheritability]::Inheritable)
            $outputPipe = [IO.Pipes.AnonymousPipeServerStream]::new([IO.Pipes.PipeDirection]::In, [IO.HandleInheritability]::Inheritable)
            $errorPipe = [IO.Pipes.AnonymousPipeServerStream]::new([IO.Pipes.PipeDirection]::In, [IO.HandleInheritability]::Inheritable)
            $job = [AgentDotNetWindowsJob]::CreateKillOnClose()
            $process = $null
            $original = @(-10, -11, -12 | ForEach-Object { [AgentGuardHandleProbe]::GetStdHandle($_) })
            try {
                $writer = [IO.StreamWriter]::new($inputPipe, [Text.UTF8Encoding]::new($false), 1024, $true)
                try { $writer.WriteLine('guard-input'); $writer.Flush() }
                finally { $writer.Dispose() }
                $handles = if ($missingHandles) { @([IntPtr]::Zero, [IntPtr]::new(-1), [IntPtr]::Zero) }
                    else { @($inputPipe.ClientSafePipeHandle.DangerousGetHandle(), $outputPipe.ClientSafePipeHandle.DangerousGetHandle(), $errorPipe.ClientSafePipeHandle.DangerousGetHandle()) }
                $report = Join-Path $testRoot "result-$missingHandles.json"
                $info = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
                $info.UseShellExecute = $false
                $info.WorkingDirectory = $testRoot
                foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $childScript, $nativeSource,
                    $sentinel.SafeWaitHandle.DangerousGetHandle().ToInt64().ToString(), $eventName, $report)) {
                    $info.ArgumentList.Add($argument)
                }
                try {
                    for ($index = 0; $index -lt 3; $index++) {
                        if ([AgentGuardHandleProbe]::SetStdHandle(-10 - $index, $handles[$index]) -eq 0) {
                            throw 'Could not install the standard-handle control.'
                        }
                    }
                    $process = [AgentDotNetWindowsJob]::StartWrapper($info, $job)
                }
                finally {
                    for ($index = 0; $index -lt 3; $index++) {
                        if ([AgentGuardHandleProbe]::SetStdHandle(-10 - $index, $original[$index]) -eq 0) {
                            throw 'Could not restore the original standard handle.'
                        }
                    }
                }
                $inputPipe.DisposeLocalCopyOfClientHandle()
                $outputPipe.DisposeLocalCopyOfClientHandle()
                $errorPipe.DisposeLocalCopyOfClientHandle()
                if (-not $process.WaitForExit(15000)) { throw 'Windows launcher control exceeded its timeout.' }
                if ($process.ExitCode -ne 0) { throw "Windows launcher control failed: $($process.ExitCode)" }
                $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
                if ($result.Inherited) { throw 'Unrelated inheritable sentinel leaked into the Windows wrapper.' }
                if (-not $result.StandardHandlesValid) { throw 'Child standard handles are invalid.' }
                if (-not $missingHandles) {
                    if ($result.Input -ne 'guard-input') { throw 'Redirected standard input changed.' }
                    $outputReader = [IO.StreamReader]::new($outputPipe)
                    $errorReader = [IO.StreamReader]::new($errorPipe)
                    try {
                        if ($outputReader.ReadToEnd().Trim() -ne 'guard-output') { throw 'Redirected standard output changed.' }
                        if ($errorReader.ReadToEnd().Trim() -ne 'guard-error') { throw 'Redirected standard error changed.' }
                    }
                    finally { $outputReader.Dispose(); $errorReader.Dispose() }
                }
                elseif ($null -ne $result.Input) { throw 'Missing standard input did not read EOF.' }
            }
            finally {
                [AgentDotNetWindowsJob]::Close($job)
                if ($null -ne $process) { $null = $process.WaitForExit(5000); $process.Dispose() }
                $inputPipe.Dispose(); $outputPipe.Dispose(); $errorPipe.Dispose()
            }
        }
    }
    finally { $sentinel.Dispose() }
    Write-Output 'OK unrelated inheritable handles excluded; redirected stdin/stdout/stderr and missing console handles preserved.'
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
exit 0
