param([string]$SdkVersion, [string]$DiagnosticsDirectory)

$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$buildTimeoutSeconds = 180
$smokeTimeoutSeconds = 60
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-vstest-{0}" -f [guid]::NewGuid())
$previousMarker = $env:RESPIRE_VSTEST_GUARD_TEST_MARKER
$previousTestRunner = $env:DOTNET_TEST_RUNNER
New-Item -ItemType Directory -Path $testRoot | Out-Null
# Keep retained diagnostics outside the disposable fixture, as in the MTP smoke.
$diagnosticsRoot = if ($DiagnosticsDirectory) {
    Join-Path ([IO.Path]::GetFullPath($DiagnosticsDirectory)) (Split-Path $testRoot -Leaf)
} else {
    Join-Path $testRoot 'diagnostics'
}
New-Item -ItemType Directory -Path $diagnosticsRoot -Force | Out-Null
$phaseLog = Join-Path $diagnosticsRoot 'phases.log'
function Write-VstestPhase([string]$Message) {
    $line = '{0:o} pid={1} {2}' -f [DateTimeOffset]::UtcNow, $PID, $Message
    Write-Host $line
    try {
        Add-Content -LiteralPath $phaseLog -Value $line -ErrorAction Stop
    }
    catch {
        Write-Warning "Could not append VSTest phase log '${phaseLog}': $_" -WarningAction Continue
    }
}
Push-Location $testRoot
try {
    Write-VstestPhase "fixture setup started; diagnostics=$diagnosticsRoot"
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
    Write-VstestPhase "fixture creation started; SDK pin=$($configuration.sdk.version); runner=$env:DOTNET_TEST_RUNNER"
    & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -DotNetArguments @('new', 'xunit', '--name', 'GuardVstest', '--output', 'fixture', '--framework', 'net10.0', '--no-restore')
    Write-VstestPhase "fixture creation returned; exit=$LASTEXITCODE"
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
    Write-VstestPhase 'fixture build started'
    & $guardScript -TimeoutSeconds $buildTimeoutSeconds -SingleNode -DotNetArguments @('build', $project, '-c', 'Release', '--nologo')
    Write-VstestPhase "fixture build returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { throw "VSTest fixture build failed: $LASTEXITCODE" }
    $baseArguments = @('test', $project, '-c', 'Release', '-f', 'net10.0', '--no-build')
    $filter = 'FullyQualifiedName=GuardVstestTests.SelectedTestExecutes'
    $captureScript = Join-Path $testRoot 'Capture-Guard.ps1'
    @'
param([string]$PayloadPath)
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
function Write-DiscoveryPhase([string]$Message) {
    $line = '{0:o} pid={1} {2}' -f [DateTimeOffset]::UtcNow, $PID, $Message
    Write-Host $line
    try {
        Add-Content -LiteralPath $payload.PhaseLog -Value $line -ErrorAction Stop
    }
    catch {
        Write-Warning "Could not append VSTest discovery phase log: $_" -WarningAction Continue
    }
}
Write-DiscoveryPhase 'discovery host entered guard'
& $payload.GuardScript -TimeoutSeconds $payload.TimeoutSeconds -SingleNode -DotNetArguments @($payload.Arguments)
$guardExitCode = $LASTEXITCODE
Write-DiscoveryPhase "discovery guard returned; exit=$guardExitCode"
exit $guardExitCode
'@ | Set-Content -LiteralPath $captureScript
    $payloadPath = Join-Path $testRoot 'discovery.json'
    @{ GuardScript = $guardScript; PhaseLog = $phaseLog; TimeoutSeconds = $smokeTimeoutSeconds; Arguments = $baseArguments + @('--list-tests', '--filter', $filter) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadPath
    $pwshPath = (Get-Process -Id $PID).Path
    $discoveryLog = Join-Path $diagnosticsRoot 'discovery.log'
    $discoveryErrorLog = Join-Path $diagnosticsRoot 'discovery-error.log'
    Write-VstestPhase "discovery host starting; output=$discoveryLog; errors=$discoveryErrorLog"
    # Redirect straight to files so partial native output survives a stalled host.
    # The outer deadline also bounds startup/teardown outside the inner guard.
    $hostOptions = @{
        FilePath = $pwshPath
        ArgumentList = @('-NoProfile', '-File', ('"{0}"' -f $captureScript), ('"{0}"' -f $payloadPath))
        RedirectStandardOutput = $discoveryLog
        RedirectStandardError = $discoveryErrorLog
        PassThru = $true
    }
    if ($IsWindows) { $hostOptions.WindowStyle = 'Hidden' }
    $discoveryHost = Start-Process @hostOptions
    $hostTimeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try {
        Write-VstestPhase "discovery host started; child pid=$($discoveryHost.Id)"
        try {
            # Include redirected-output drainage in the host deadline.
            $discoveryHost.WaitForExitAsync($hostTimeout.Token).GetAwaiter().GetResult()
        }
        catch [OperationCanceledException] {
            throw 'VSTest discovery host exceeded 90s timeout.'
        }
        $discoveryExitCode = $discoveryHost.ExitCode
        Write-VstestPhase "discovery host returned; child pid=$($discoveryHost.Id); exit=$discoveryExitCode"
    }
    finally {
        $hostTimeout.Dispose()
        if (-not $discoveryHost.HasExited) {
            $discoveryHost.Kill($true)
            if (-not $discoveryHost.WaitForExit(5000)) {
                Write-Warning 'VSTest discovery host did not exit within 5s of tree termination.' -WarningAction Continue
            }
        }
        $discoveryHost.Dispose()
    }
    $discovery = @(Get-Content -LiteralPath $discoveryLog) + @(Get-Content -LiteralPath $discoveryErrorLog)
    if ($discoveryExitCode -ne 0) { throw "VSTest discovery failed: $discoveryExitCode. $($discovery -join [Environment]::NewLine)" }
    $discovery = @($discovery | ForEach-Object { [regex]::Replace([string]$_, '\x1b\[[0-?]*[ -/]*[@-~]', '') })
    $listed = @($discovery | Where-Object { $_ -match '^\s*GuardVstestTests.SelectedTestExecutes\s*$' })
    if ($listed.Count -ne 1 -or @($discovery | Where-Object { $_ -match 'UnselectedTestFails' }).Count -ne 0) {
        throw "Expected exactly the selected VSTest test: $($discovery -join [Environment]::NewLine)"
    }
    if (Test-Path -LiteralPath $env:RESPIRE_VSTEST_GUARD_TEST_MARKER) { throw 'Discovery executed a test.' }
    $results = Join-Path $testRoot 'results'
    Write-VstestPhase 'filtered execution started'
    & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -SingleNode -DotNetArguments ($baseArguments + @('--filter', $filter, '--logger', 'trx', '--results-directory', $results))
    Write-VstestPhase "filtered execution returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { throw "VSTest execution failed: $LASTEXITCODE" }
    $reports = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -Recurse)
    if ($reports.Count -ne 1) { throw "Expected one TRX report, found $($reports.Count)." }
    [xml]$trx = Get-Content -LiteralPath $reports[0].FullName
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($counters.total -ne '1' -or $counters.executed -ne '1' -or $counters.passed -ne '1' -or $counters.failed -ne '0') {
        throw 'Expected exactly one executed, passing VSTest test.'
    }
    if ((Get-Content -LiteralPath $env:RESPIRE_VSTEST_GUARD_TEST_MARKER).Count -ne 1) { throw 'Selected test did not execute exactly once.' }
    Write-VstestPhase 'discovery and execution assertions passed'
    Write-VstestPhase 'failure control started'
    $failureResults = Join-Path $testRoot 'failure-results'
    & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -SingleNode -DotNetArguments ($baseArguments + @('--filter', 'FullyQualifiedName=GuardVstestTests.UnselectedTestFails', '--logger', 'trx', '--results-directory', $failureResults))
    Write-VstestPhase "failure control returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 1) { throw "Expected test-failure exit 1, received $LASTEXITCODE" }
    $failureReports = @(Get-ChildItem -LiteralPath $failureResults -Filter '*.trx' -Recurse -ErrorAction SilentlyContinue)
    if ($failureReports.Count -ne 1) { throw "Expected one failure-control TRX report, found $($failureReports.Count)." }
    [xml]$failureTrx = Get-Content -LiteralPath $failureReports[0].FullName
    $failureCounters = $failureTrx.TestRun.ResultSummary.Counters
    $failureTests = @($failureTrx.TestRun.Results.UnitTestResult)
    if ($failureCounters.total -ne '1' -or $failureCounters.executed -ne '1' -or $failureCounters.passed -ne '0' -or $failureCounters.failed -ne '1' -or
        $failureTests.Count -ne 1 -or $failureTests[0].testName -ne 'GuardVstestTests.UnselectedTestFails' -or $failureTests[0].outcome -ne 'Failed' -or
        $failureTests[0].Output.ErrorInfo.Message -notlike '*guard failure control*') {
        throw 'Expected exactly the failure-control VSTest test to execute and fail with its deliberate exception.'
    }
    Write-VstestPhase 'all VSTest assertions passed'
    Write-Output 'OK VSTest discovery and execution select exactly one test with conflicting global/environment runner settings.'
}
catch {
    Write-VstestPhase "FAILED: $_"
    throw
}
finally {
    Write-VstestPhase 'fixture cleanup started'
    Pop-Location
    $env:RESPIRE_VSTEST_GUARD_TEST_MARKER = $previousMarker
    $env:DOTNET_TEST_RUNNER = $previousTestRunner
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

# Do not leak the expected native failure-control exit into the Actions step.
exit 0
