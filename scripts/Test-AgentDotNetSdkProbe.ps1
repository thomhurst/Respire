$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-sdk-probe-{0}" -f [guid]::NewGuid())
$probeVariables = @('DOTNET_TEST_RUNNER', 'RESPIRE_SDK_PROBE_MODE', 'RESPIRE_SDK_PROBE_PID', 'RESPIRE_SDK_PROBE_CHILD_PID', 'RESPIRE_SDK_PROBE_WORKLOAD')
$previousEnvironment = @{}
foreach ($variable in $probeVariables) { $previousEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable) }
New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location $testRoot

function Test-RecordedProcess([object]$Identity, [switch]$Stop) {
    try {
        $process = [Diagnostics.Process]::GetProcessById($Identity.ProcessId)
        try {
            $alive = -not $process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq $Identity.StartTimeUtcTicks
            if ($alive) {
                if ($Stop) { $process.Kill($true) }
                # OS job/group termination is asynchronous. Wait on this verified
                # process handle, rather than sleeping or assuming instant disappearance.
                $alive = -not $process.WaitForExit(5000)
            }
            return $alive
        }
        finally { $process.Dispose() }
    }
    catch [ArgumentException] { return $false }
}

try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Destination (Join-Path $testRoot 'global.json')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'SdkProbe.csproj')
    @'
using System.Diagnostics;
using System.Text.Json;
if (args is ["child"]) { Thread.Sleep(60000); return 0; }
if (args is not ["--version"])
{
    File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_SDK_PROBE_WORKLOAD")!, "executed");
    return 0;
}
using var self = Process.GetCurrentProcess();
File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_SDK_PROBE_PID")!,
    JsonSerializer.Serialize(new { ProcessId = self.Id, StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks }));
if (Environment.GetEnvironmentVariable("RESPIRE_AGENT_DOTNET_INVOCATION") is not null) return 7;
var mode = Environment.GetEnvironmentVariable("RESPIRE_SDK_PROBE_MODE");
if (mode == "descendant")
{
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "child") { UseShellExecute = false, CreateNoWindow = true })!;
    File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_SDK_PROBE_CHILD_PID")!,
        JsonSerializer.Serialize(new { ProcessId = child.Id, StartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks }));
}
if (mode == "timeout") Thread.Sleep(60000);
if (mode == "memory")
{
    var retained = new List<byte[]>();
    for (var i = 0; i < 80; i++)
    {
        var block = new byte[8 * 1024 * 1024];
        for (var page = 0; page < block.Length; page += Environment.SystemPageSize) block[page] = 1;
        retained.Add(block);
        Thread.Sleep(25);
    }
    Thread.Sleep(60000);
    GC.KeepAlive(retained);
}
Console.WriteLine("11.0.100-preview.7.26381.103");
return 0;
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Program.cs')
    & $guardScript -SingleNode -DotNetArguments @('build', 'SdkProbe.csproj', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw "SDK probe fixture build failed: $LASTEXITCODE" }
    $probeName = if ($IsWindows) { 'SdkProbe.exe' } else { 'SdkProbe' }
    $probe = Join-Path $testRoot "bin/Debug/net10.0/$probeName"
    $env:DOTNET_TEST_RUNNER = 'VSTest'
    foreach ($mode in @('descendant', 'timeout', 'memory')) {
        $env:RESPIRE_SDK_PROBE_MODE = $mode
        $env:RESPIRE_SDK_PROBE_PID = Join-Path $testRoot "$mode-pid.json"
        $env:RESPIRE_SDK_PROBE_CHILD_PID = Join-Path $testRoot "$mode-child-pid.json"
        $env:RESPIRE_SDK_PROBE_WORKLOAD = Join-Path $testRoot "$mode-workload.txt"
        $timeout = if ($mode -eq 'timeout') { 4 } else { 30 }
        $expectedExit = switch ($mode) { 'timeout' { 124 } 'memory' { 137 } default { 0 } }
        & $guardScript -SingleNode -DotNetPath $probe -TimeoutSeconds $timeout -MemoryLimitMb 512 -PollIntervalMilliseconds 100 -DotNetArguments @('test', 'project')
        if ($LASTEXITCODE -ne $expectedExit) { throw "SDK probe $mode returned $LASTEXITCODE instead of $expectedExit." }
        $identity = Get-Content -LiteralPath $env:RESPIRE_SDK_PROBE_PID -Raw | ConvertFrom-Json
        if (Test-RecordedProcess $identity) { throw "SDK probe $mode is still running." }
        if ($mode -eq 'descendant') {
            $childIdentity = Get-Content -LiteralPath $env:RESPIRE_SDK_PROBE_CHILD_PID -Raw | ConvertFrom-Json
            if (Test-RecordedProcess $childIdentity) { throw 'SDK probe descendant is still running after normal command exit.' }
            if (-not (Test-Path -LiteralPath $env:RESPIRE_SDK_PROBE_WORKLOAD)) { throw 'Normal SDK probe did not launch the workload.' }
        }
        elseif (Test-Path -LiteralPath $env:RESPIRE_SDK_PROBE_WORKLOAD) { throw "SDK probe $mode launched a workload after exceeding its limit." }
    }
    Write-Output 'OK SDK probe normal-exit descendant cleanup, timeout, memory limit, and prevention of workload launch after probe failure.'
}
finally {
    foreach ($identityFile in Get-ChildItem -LiteralPath $testRoot -Filter '*-pid.json') {
        $identity = Get-Content -LiteralPath $identityFile.FullName -Raw | ConvertFrom-Json
        $null = Test-RecordedProcess $identity -Stop
    }
    Pop-Location
    foreach ($variable in $probeVariables) { [Environment]::SetEnvironmentVariable($variable, $previousEnvironment[$variable]) }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

exit 0
