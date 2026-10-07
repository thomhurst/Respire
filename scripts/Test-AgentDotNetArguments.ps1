$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-arguments-{0}" -f [guid]::NewGuid())
$previousCapturePath = $env:RESPIRE_GUARD_TEST_CAPTURE
$previousDirectoryCapturePath = $env:RESPIRE_GUARD_TEST_DIRECTORY
$previousTestRunner = $env:DOTNET_TEST_RUNNER
$previousSdkVersion = $env:RESPIRE_GUARD_TEST_SDK_VERSION
$env:DOTNET_TEST_RUNNER = $null
New-Item -ItemType Directory -Path $testRoot | Out-Null
$configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Raw | ConvertFrom-Json
$configuration.test.runner = 'VSTest'
$configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
Push-Location $testRoot
try {
    $project = Join-Path $testRoot 'ArgumentProbe.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $project
    @'
using System.Text.Json;
if (Environment.GetEnvironmentVariable("RESPIRE_AGENT_DOTNET_INVOCATION") is not null)
    return 7;
if (args is ["--version"])
{
    Console.WriteLine(Environment.GetEnvironmentVariable("RESPIRE_GUARD_TEST_SDK_VERSION") ?? "10.0.401");
    return 0;
}
File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_GUARD_TEST_CAPTURE")!, JsonSerializer.Serialize(args));
File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_GUARD_TEST_DIRECTORY")!, Environment.CurrentDirectory);
return 0;
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Program.cs')
    # Build the native probe without relying on SingleNode injection under test.
    & $guardScript -TimeoutSeconds 60 -DotNetArguments @('build', $project, '/m:1', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw 'Argument probe build failed.' }
    $probeName = if ($IsWindows) { 'ArgumentProbe.exe' } else { 'ArgumentProbe' }
    $probe = Join-Path $testRoot "bin/Debug/net10.0/$probeName"
    $env:RESPIRE_GUARD_TEST_CAPTURE = Join-Path $testRoot 'arguments.json'
    $env:RESPIRE_GUARD_TEST_DIRECTORY = Join-Path $testRoot 'directory.txt'
    $directoryMarkerName = "guard-location-$([guid]::NewGuid()).txt"

    function Assert-Arguments([string[]]$InputArguments, [string[]]$Expected, [switch]$SingleNode) {
        $markerPath = Join-Path (Get-Location).ProviderPath $directoryMarkerName
        [IO.File]::WriteAllText($markerPath, $directoryMarkerName)
        & $guardScript -TimeoutSeconds 30 -DotNetPath $probe -SingleNode:$SingleNode -DotNetArguments $InputArguments
        if ($LASTEXITCODE -ne 0) { throw "Argument probe failed: $LASTEXITCODE" }
        $actualDirectory = [IO.File]::ReadAllText($env:RESPIRE_GUARD_TEST_DIRECTORY)
        # Native cwd may resolve aliases such as macOS /var to /private/var.
        # A unique marker proves directory identity without assuming path spelling.
        $actualMarkerPath = Join-Path $actualDirectory $directoryMarkerName
        if (-not [IO.File]::Exists($actualMarkerPath) -or [IO.File]::ReadAllText($actualMarkerPath) -ne $directoryMarkerName) {
            throw "Child directory: expected '$((Get-Location).ProviderPath)', actual '$actualDirectory'."
        }
        Remove-Item -LiteralPath $markerPath
        $capture = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($env:RESPIRE_GUARD_TEST_CAPTURE))
        try {
            $actual = @($capture.RootElement.EnumerateArray() | ForEach-Object { $_.GetString() })
        }
        finally {
            $capture.Dispose()
        }
        if ($actual.Count -ne $Expected.Count) {
            throw "Argument count: expected $($Expected.Count), actual $($actual.Count): $($actual -join '|')"
        }
        for ($index = 0; $index -lt $Expected.Count; $index++) {
            if ($actual[$index] -cne $Expected[$index]) {
                throw "Argument $index changed: expected '$($Expected[$index])', actual '$($actual[$index])'."
            }
        }
    }

    $special = @('build', '-p:Version=1.2.3', '-p:Label=two words', '', 'quote"value', "apostrophe'value", 'literal$(never-run)', 'C:\path with spaces\', '--flag:false', '2024-01-01T00:00:00Z', '2024-01-01T03:04:05.1234567+02:00')
    Assert-Arguments $special $special
    foreach ($verb in @('build', 'test', 'pack', 'publish', 'msbuild')) {
        Assert-Arguments @($verb, 'project') @($verb, 'project', '-m:1') -SingleNode
    }
    foreach ($argument in @('-m:1', '/m:1', '-maxcpucount:1', '/maxcpucount:1', '--maxcpucount:1', '-M:1', '-m')) {
        Assert-Arguments @('build', $argument) @('build', $argument) -SingleNode
    }
    Assert-Arguments @('test', '--', '-m:4') @('test', '-m:1', '--', '-m:4') -SingleNode
    Assert-Arguments @('run', '--', '-m:4') @('run', '--', '-m:4') -SingleNode
    Assert-Arguments @('version') @('version') -SingleNode
    Assert-Arguments @('--', '-m:4') @('--', '-m:4') -SingleNode
    $mtpRoot = Join-Path $testRoot 'mtp'
    $nestedRoot = Join-Path $mtpRoot 'nested'
    $overrideRoot = Join-Path $nestedRoot 'override'
    New-Item -ItemType Directory -Path $overrideRoot | Out-Null
    @'
{
  // Match SDK-supported global.json comments and trailing commas.
  "test": { "runner": "Microsoft.Testing.Platform", },
}
'@ | Set-Content -LiteralPath (Join-Path $mtpRoot 'global.json')
    Push-Location $nestedRoot
    try {
        Assert-Arguments @('test', '--project', $project, '--list-tests') @('test', '--project', $project, '--list-tests') -SingleNode
        Assert-Arguments @('test', '--', '-m:4') @('test', '--', '-m:4') -SingleNode
        Assert-Arguments @('test', '-m:4') @('test', '-m:4') -SingleNode
        Assert-Arguments @('build', $project) @('build', $project, '-m:1') -SingleNode
        '{}' | Set-Content -LiteralPath (Join-Path $overrideRoot 'global.json')
        Push-Location $overrideRoot
        try {
            Assert-Arguments @('test', 'project') @('test', 'project', '-m:1') -SingleNode
            $malformedPath = Join-Path $overrideRoot 'global.json'
            '{ invalid json' | Set-Content -LiteralPath $malformedPath
            Remove-Item -LiteralPath $env:RESPIRE_GUARD_TEST_CAPTURE
            $configurationRejected = $false
            try { & $guardScript -SingleNode -DotNetPath $probe -DotNetArguments @('test', 'project') }
            catch {
                if (-not $_.Exception.Message.StartsWith("Cannot read test runner configuration '$malformedPath':")) { throw }
                $configurationRejected = $true
            }
            if (-not $configurationRejected -or (Test-Path -LiteralPath $env:RESPIRE_GUARD_TEST_CAPTURE)) {
                throw 'Malformed runner configuration did not fail before launching the child.'
            }
        }
        finally { Pop-Location }
    }
    finally { Pop-Location }
    $aliasRoot = Join-Path $testRoot 'mtp-alias'
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $aliasRoot -Target $mtpRoot | Out-Null
    Push-Location $aliasRoot
    try { Assert-Arguments @('test', 'project') @('test', 'project') -SingleNode }
    finally { Pop-Location }

    # The custom executable reports its effective SDK. Its version, rather than the
    # .NET 10 global.json pin, must decide whether the runner override is supported.
    foreach ($sdkCase in @(
        @{ Version = '10.0.401'; SupportsOverride = $false },
        @{ Version = '11.0.100-preview.5.26301.1'; SupportsOverride = $false },
        @{ Version = '11.0.100-preview.6'; SupportsOverride = $true },
        @{ Version = '11.0.100-preview.7.26381.103'; SupportsOverride = $true },
        @{ Version = '11.0.100-preview.10.1'; SupportsOverride = $true },
        @{ Version = '11.0.100-rc.1.1'; SupportsOverride = $true },
        @{ Version = '11.0.100'; SupportsOverride = $true },
        @{ Version = '12.0.100-preview.1'; SupportsOverride = $true }
    )) {
        foreach ($globalMtp in @($false, $true)) {
            $configuration.test.runner = if ($globalMtp) { 'Microsoft.Testing.Platform' } else { 'VSTest' }
            $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
            $env:RESPIRE_GUARD_TEST_SDK_VERSION = $sdkCase.Version
            $env:DOTNET_TEST_RUNNER = if ($globalMtp) { 'vStEsT' } else { 'mIcRoSoFt.TeStInG.PLaTfOrM' }
            $effectiveMtp = if ($sdkCase.SupportsOverride) { -not $globalMtp } else { $globalMtp }
            $expected = if ($effectiveMtp) { @('test', 'project') } else { @('test', 'project', '-m:1') }
            Assert-Arguments @('test', 'project') $expected -SingleNode
        }
    }
    # Empty/unrecognized overrides need no SDK process and use the nearest file.
    $env:RESPIRE_GUARD_TEST_SDK_VERSION = 'not-a-version'
    foreach ($globalMtp in @($false, $true)) {
        $configuration.test.runner = if ($globalMtp) { 'Microsoft.Testing.Platform' } else { 'VSTest' }
        $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
        foreach ($runnerValue in @('', 'unrecognized')) {
            $env:DOTNET_TEST_RUNNER = $runnerValue
            $expected = if ($globalMtp) { @('test', 'project') } else { @('test', 'project', '-m:1') }
            Assert-Arguments @('test', 'project') $expected -SingleNode
        }
    }
    $env:DOTNET_TEST_RUNNER = 'VSTest'
    $env:RESPIRE_GUARD_TEST_SDK_VERSION = '11.0.100-preview.7.26381.103'
    Assert-Arguments @('test', '-m:4') @('test', '-m:4') -SingleNode
    Assert-Arguments @('test', '--', '-m:4') @('test', '-m:1', '--', '-m:4') -SingleNode
    Assert-Arguments @('build', 'project') @('build', 'project', '-m:1') -SingleNode
    $env:RESPIRE_GUARD_TEST_SDK_VERSION = 'not-a-version'
    Remove-Item -LiteralPath $env:RESPIRE_GUARD_TEST_CAPTURE
    & $guardScript -TimeoutSeconds 30 -SingleNode -DotNetPath $probe -DotNetArguments @('test', 'project')
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $env:RESPIRE_GUARD_TEST_CAPTURE)) {
        throw 'Unrecognized effective SDK did not fail before launching the workload.'
    }
    $configuration.test.runner = 'VSTest'
    $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
    $env:DOTNET_TEST_RUNNER = $null
    $longArgument = 'x' * 8192
    Assert-Arguments @('build', $longArgument) @('build', $longArgument, '-m:1') -SingleNode

    if (-not $IsWindows) {
        # Each argument stays small, but their aggregate exceeds Linux's 128 KiB
        # per-environment-string ceiling. Windows has its own 32K command-line limit.
        $manyArguments = @('build') + @(1..160 | ForEach-Object { ('x' * 1024) + $_ })
        Assert-Arguments $manyArguments ($manyArguments + '-m:1') -SingleNode
    }

    # Exercise real MSBuild with the injected switch, not only the probe.
    & $guardScript -SingleNode -TimeoutSeconds 60 -DotNetArguments @('pack', $project, '--no-restore', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw 'SingleNode pack failed.' }
    Write-Output 'OK native arguments, SDK-aware runner overrides, nearest MTP configuration, existing switches, separator, and pack passed.'
}
finally {
    Pop-Location
    $env:RESPIRE_GUARD_TEST_CAPTURE = $previousCapturePath
    $env:RESPIRE_GUARD_TEST_DIRECTORY = $previousDirectoryCapturePath
    $env:DOTNET_TEST_RUNNER = $previousTestRunner
    $env:RESPIRE_GUARD_TEST_SDK_VERSION = $previousSdkVersion
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside temp root: $resolvedRoot"
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

exit 0

