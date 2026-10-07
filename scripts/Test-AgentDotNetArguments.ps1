$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-arguments-{0}" -f [guid]::NewGuid())
$previousCapturePath = $env:RESPIRE_GUARD_TEST_CAPTURE
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
File.WriteAllText(Environment.GetEnvironmentVariable("RESPIRE_GUARD_TEST_CAPTURE")!, JsonSerializer.Serialize(args));
return 0;
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'Program.cs')
    # Build the native probe without relying on SingleNode injection under test.
    & $guardScript -TimeoutSeconds 60 -DotNetArguments @('build', $project, '/m:1', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw 'Argument probe build failed.' }
    $probeName = if ($IsWindows) { 'ArgumentProbe.exe' } else { 'ArgumentProbe' }
    $probe = Join-Path $testRoot "bin/Debug/net10.0/$probeName"
    $env:RESPIRE_GUARD_TEST_CAPTURE = Join-Path $testRoot 'arguments.json'

    function Assert-Arguments([string[]]$InputArguments, [string[]]$Expected, [switch]$SingleNode) {
        & $guardScript -TimeoutSeconds 30 -DotNetPath $probe -SingleNode:$SingleNode -DotNetArguments $InputArguments
        if ($LASTEXITCODE -ne 0) { throw "Argument probe failed: $LASTEXITCODE" }
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
        try { Assert-Arguments @('test', 'project') @('test', 'project', '-m:1') -SingleNode }
        finally { Pop-Location }
    }
    finally { Pop-Location }
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
    Write-Output 'OK native argument preservation, VSTest SingleNode injection, nearest MTP configuration, existing switches, separator, and pack passed.'
}
finally {
    Pop-Location
    $env:RESPIRE_GUARD_TEST_CAPTURE = $previousCapturePath
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside temp root: $resolvedRoot"
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

