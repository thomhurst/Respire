param([string]$SdkVersion)

$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-vstest-{0}" -f [guid]::NewGuid())
$previousMarker = $env:RESPIRE_VSTEST_GUARD_TEST_MARKER
$previousTestRunner = $env:DOTNET_TEST_RUNNER
New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location $testRoot
try {
    $configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Raw | ConvertFrom-Json
    if ($SdkVersion) {
        $configuration.sdk.version = $SdkVersion
        $configuration.sdk.rollForward = 'disable'
    }
    $supportsOverride = [semver]$configuration.sdk.version -ge [semver]'11.0.100-preview.6'
    $configuration.test.runner = if ($supportsOverride) { 'Microsoft.Testing.Platform' } else { 'VSTest' }
    $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
    $env:DOTNET_TEST_RUNNER = if ($supportsOverride) { 'vStEsT' } else { 'Microsoft.Testing.Platform' }
    $env:RESPIRE_VSTEST_GUARD_TEST_MARKER = Join-Path $testRoot 'executed.txt'
    & $guardScript -DotNetArguments @('new', 'xunit', '--name', 'GuardVstest', '--output', 'fixture', '--framework', 'net10.0', '--no-restore')
    if ($LASTEXITCODE -ne 0) { throw "VSTest fixture creation failed: $LASTEXITCODE" }
    @'
using Xunit;
public class GuardVstestTests
{
    [Fact]
    public void SelectedTestExecutes()
        => File.AppendAllText(Environment.GetEnvironmentVariable("RESPIRE_VSTEST_GUARD_TEST_MARKER")!, "executed\n");

    [Fact]
    public void UnselectedTestFails() => throw new InvalidOperationException("guard failure control");
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'fixture/UnitTest1.cs')
    $project = Join-Path $testRoot 'fixture/GuardVstest.csproj'
    & $guardScript -SingleNode -DotNetArguments @('build', $project, '-c', 'Release', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw "VSTest fixture build failed: $LASTEXITCODE" }
    $baseArguments = @('test', $project, '-c', 'Release', '-f', 'net10.0', '--no-build')
    $filter = 'FullyQualifiedName=GuardVstestTests.SelectedTestExecutes'
    $captureScript = Join-Path $testRoot 'Capture-Guard.ps1'
    @'
param([string]$PayloadPath)
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
& $payload.GuardScript -SingleNode -DotNetArguments @($payload.Arguments)
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $captureScript
    $payloadPath = Join-Path $testRoot 'discovery.json'
    @{ GuardScript = $guardScript; Arguments = $baseArguments + @('--list-tests', '--filter', $filter) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadPath
    $pwshPath = (Get-Process -Id $PID).Path
    $discovery = @(& $pwshPath -NoProfile -File $captureScript $payloadPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "VSTest discovery failed: $LASTEXITCODE" }
    $discovery = @($discovery | ForEach-Object { [regex]::Replace([string]$_, '\x1b\[[0-?]*[ -/]*[@-~]', '') })
    $listed = @($discovery | Where-Object { $_ -match '^\s*GuardVstestTests.SelectedTestExecutes\s*$' })
    if ($listed.Count -ne 1 -or @($discovery | Where-Object { $_ -match 'UnselectedTestFails' }).Count -ne 0) {
        throw "Expected exactly the selected VSTest test: $($discovery -join [Environment]::NewLine)"
    }
    if (Test-Path -LiteralPath $env:RESPIRE_VSTEST_GUARD_TEST_MARKER) { throw 'Discovery executed a test.' }
    $results = Join-Path $testRoot 'results'
    & $guardScript -SingleNode -DotNetArguments ($baseArguments + @('--filter', $filter, '--logger', 'trx', '--results-directory', $results))
    if ($LASTEXITCODE -ne 0) { throw "VSTest execution failed: $LASTEXITCODE" }
    $reports = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -Recurse)
    if ($reports.Count -ne 1) { throw "Expected one TRX report, found $($reports.Count)." }
    [xml]$trx = Get-Content -LiteralPath $reports[0].FullName
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($counters.total -ne '1' -or $counters.executed -ne '1' -or $counters.passed -ne '1' -or $counters.failed -ne '0') {
        throw 'Expected exactly one executed, passing VSTest test.'
    }
    if ((Get-Content -LiteralPath $env:RESPIRE_VSTEST_GUARD_TEST_MARKER).Count -ne 1) { throw 'Selected test did not execute exactly once.' }
    Write-Output 'OK VSTest discovery and execution select exactly one test with conflicting global/environment runner settings.'
}
finally {
    Pop-Location
    $env:RESPIRE_VSTEST_GUARD_TEST_MARKER = $previousMarker
    $env:DOTNET_TEST_RUNNER = $previousTestRunner
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

exit 0
