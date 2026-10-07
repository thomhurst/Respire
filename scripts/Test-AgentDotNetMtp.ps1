param([string]$SdkVersion, [switch]$RollForwardAcrossMajor)

$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-mtp-{0}" -f [guid]::NewGuid())
$previousMarker = $env:RESPIRE_MTP_GUARD_TEST_MARKER
$previousHtmlReporter = $env:TUNIT_DISABLE_HTML_REPORTER
$previousTestRunner = $env:DOTNET_TEST_RUNNER
New-Item -ItemType Directory -Path $testRoot | Out-Null
Push-Location $testRoot
try {
    $configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Raw | ConvertFrom-Json
    if ($SdkVersion) {
        $configuration.sdk.version = $SdkVersion
        $configuration.sdk.rollForward = 'disable'
    }
    if ($RollForwardAcrossMajor) {
        $configuration.sdk.rollForward = 'latestMajor'
        $configuration.sdk | Add-Member -NotePropertyName allowPrerelease -NotePropertyValue $true -Force
    }
    $supportsOverride = $RollForwardAcrossMajor -or [semver]$configuration.sdk.version -ge [semver]'11.0.100-preview.6'
    $configuration.test.runner = if ($supportsOverride) { 'VSTest' } else { 'Microsoft.Testing.Platform' }
    $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'global.json')
    [xml]$packages = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Packages.props')
    $tunitVersion = ($packages.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'TUnit').Version
    $project = Join-Path $testRoot 'GuardMtp.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="TUnit" Version="TUNIT_VERSION" /></ItemGroup>
</Project>
'@.Replace('TUNIT_VERSION', $tunitVersion) | Set-Content -LiteralPath $project
    @'
using TUnit.Core;
public class GuardMtpTests
{
    [Test]
    public void SelectedTestExecutes()
        => File.AppendAllText(Environment.GetEnvironmentVariable("RESPIRE_MTP_GUARD_TEST_MARKER")!, "executed\n");

    [Test]
    public void UnselectedTestFails() => throw new InvalidOperationException("guard failure control");
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'GuardMtpTests.cs')
    $env:RESPIRE_MTP_GUARD_TEST_MARKER = Join-Path $testRoot 'executed.txt'
    $env:TUNIT_DISABLE_HTML_REPORTER = 'true'
    # Use conflicting settings on both SDKs: .NET 10 follows global.json;
    # supporting SDKs follow the case-insensitive environment override.
    $env:DOTNET_TEST_RUNNER = if ($supportsOverride) { 'mIcRoSoFt.TeStInG.PLaTfOrM' } else { 'VSTest' }
    & $guardScript -SingleNode -DotNetArguments @('build', $project, '-c', 'Release', '--nologo')
    if ($LASTEXITCODE -ne 0) { throw "MTP fixture build failed: $LASTEXITCODE" }

    $baseArguments = @('test', '--project', $project, '-c', 'Release', '-f', 'net10.0', '--no-build')
    # Child console output bypasses PowerShell streams in the guard. Capture a host
    # process whose script still invokes the guard in-process, preserving its limits.
    $captureScript = Join-Path $testRoot 'Capture-Guard.ps1'
    @'
param([string]$PayloadPath)
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
& $payload.GuardScript -SingleNode -DotNetArguments @($payload.Arguments)
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $captureScript
    $pwshPath = (Get-Process -Id $PID).Path
    $filter = '/*/*/GuardMtpTests/SelectedTestExecutes'
    # .NET 11 accepts an optional discovery format; specify it so the following
    # extension switch cannot be parsed as the format value.
    $listArguments = if ($supportsOverride) { @('--list-tests', 'text') } else { @('--list-tests') }
    foreach ($separator in @($false, $true)) {
        $extensionSeparator = if ($separator) { @('--') } else { @() }
        $payloadPath = Join-Path $testRoot 'discovery.json'
        @{ GuardScript = $guardScript; Arguments = $baseArguments + $listArguments + $extensionSeparator + @('--treenode-filter', $filter) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadPath
        $discovery = @(& $pwshPath -NoProfile -File $captureScript $payloadPath 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "MTP discovery failed (separator=$separator): $LASTEXITCODE. $($discovery -join [Environment]::NewLine)" }
        # CI enables ANSI color even on redirected SDK output. Remove presentation
        # sequences before checking the same exact method name and global count.
        $discovery = @($discovery | ForEach-Object { [regex]::Replace([string]$_, '\x1b\[[0-?]*[ -/]*[@-~]', '') })
        $listed = @($discovery | Where-Object { $_ -match '^\s*SelectedTestExecutes\s*$' })
        $countSummary = @($discovery | Where-Object { $_ -match '^\s*Discovered 1 tests\.\s*$' })
        if ($listed.Count -ne 1 -or $countSummary.Count -ne 1) {
            throw "Expected exactly the selected test in discovery: $($discovery -join [Environment]::NewLine)"
        }
        if (Test-Path -LiteralPath $env:RESPIRE_MTP_GUARD_TEST_MARKER) { throw 'Discovery executed a test.' }
        $results = Join-Path $testRoot "results-$separator"
        & $guardScript -SingleNode -DotNetArguments ($baseArguments + @('--results-directory', $results) + $extensionSeparator + @('--treenode-filter', $filter, '--report-trx'))
        if ($LASTEXITCODE -ne 0) { throw "MTP execution failed (separator=$separator): $LASTEXITCODE" }
        $reports = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -Recurse)
        if ($reports.Count -ne 1) { throw "Expected one TRX report, found $($reports.Count)." }
        [xml]$trx = Get-Content -LiteralPath $reports[0].FullName
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($counters.total -ne '1' -or $counters.executed -ne '1' -or $counters.passed -ne '1' -or $counters.failed -ne '0') {
            throw 'Expected exactly one executed, passing test.'
        }
        if ((Get-Content -LiteralPath $env:RESPIRE_MTP_GUARD_TEST_MARKER).Count -ne 1) { throw 'Selected test did not execute exactly once.' }
        Remove-Item -LiteralPath $env:RESPIRE_MTP_GUARD_TEST_MARKER
    }

    & $guardScript -SingleNode -DotNetArguments ($baseArguments + @('--treenode-filter', '/*/*/GuardMtpTests/UnselectedTestFails'))
    if ($LASTEXITCODE -ne 2) { throw "Expected test-failure exit 2, received $LASTEXITCODE" }
    & $guardScript -SingleNode -DotNetArguments ($baseArguments + @('--treenode-filter', '/*/*/GuardMtpTests/NoSuchTest'))
    if ($LASTEXITCODE -ne 8) { throw "Expected zero-tests exit 8, received $LASTEXITCODE" }
    Write-Output 'OK MTP discovery and execution preserve filtering/counts with both separator forms; failure and zero-test exits remain visible.'
}
finally {
    Pop-Location
    $env:RESPIRE_MTP_GUARD_TEST_MARKER = $previousMarker
    $env:TUNIT_DISABLE_HTML_REPORTER = $previousHtmlReporter
    $env:DOTNET_TEST_RUNNER = $previousTestRunner
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

# All unexpected outcomes throw above. The expected failure/empty-filter native
# exits must not leak into the Actions pwsh check after the script succeeds.
exit 0
