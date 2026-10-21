param([string]$SdkVersion, [switch]$RollForwardAcrossMajor, [string]$DiagnosticsDirectory)

$ErrorActionPreference = 'Stop'
$guardScript = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$buildTimeoutSeconds = 180
$smokeTimeoutSeconds = 60
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-dotnet-mtp-{0}" -f [guid]::NewGuid())
$previousMarker = $env:RESPIRE_MTP_GUARD_TEST_MARKER
$previousHtmlReporter = $env:TUNIT_DISABLE_HTML_REPORTER
$previousTestRunner = $env:DOTNET_TEST_RUNNER
New-Item -ItemType Directory -Path $testRoot | Out-Null
# Keep CI diagnostics outside the disposable fixture. Each invocation gets its
# own directory because the SDK override job runs this script more than once.
$diagnosticsRoot = if ($DiagnosticsDirectory) {
    Join-Path ([IO.Path]::GetFullPath($DiagnosticsDirectory)) (Split-Path $testRoot -Leaf)
} else {
    Join-Path $testRoot 'diagnostics'
}
New-Item -ItemType Directory -Path $diagnosticsRoot -Force | Out-Null
$phaseLog = Join-Path $diagnosticsRoot 'phases.log'
function Write-MtpPhase([string]$Message) {
    $line = '{0:o} pid={1} {2}' -f [DateTimeOffset]::UtcNow, $PID, $Message
    Write-Host $line
    try {
        Add-Content -LiteralPath $phaseLog -Value $line -ErrorAction Stop
    }
    catch {
        # Diagnostics must never hide the test failure or interrupt finally.
        Write-Warning "Could not append MTP phase log '${phaseLog}': $_" -WarningAction Continue
    }
}
Push-Location $testRoot
try {
    Write-MtpPhase "fixture setup started; diagnostics=$diagnosticsRoot"
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
    Write-MtpPhase "fixture build started; SDK pin=$($configuration.sdk.version); rollForward=$($configuration.sdk.rollForward); runner=$env:DOTNET_TEST_RUNNER"
    # Allow a cold restore more time than the no-build smoke operations. Bound both
    # so a stalled child leaves time for cleanup and upload before the CI deadline.
    & $guardScript -TimeoutSeconds $buildTimeoutSeconds -SingleNode -DotNetArguments @('build', $project, '-c', 'Release', '--nologo')
    Write-MtpPhase "fixture build returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { throw "MTP fixture build failed: $LASTEXITCODE" }

    $baseArguments = @('test', '--project', $project, '-c', 'Release', '-f', 'net10.0', '--no-build')
    # Child console output bypasses PowerShell streams in the guard. Capture a host
    # process whose script still invokes the guard in-process, preserving its limits.
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
        Write-Warning "Could not append MTP discovery phase log: $_" -WarningAction Continue
    }
}
Write-DiscoveryPhase 'discovery host entered guard'
& $payload.GuardScript -TimeoutSeconds $payload.TimeoutSeconds -SingleNode -DotNetArguments @($payload.Arguments)
$guardExitCode = $LASTEXITCODE
Write-DiscoveryPhase "discovery guard returned; exit=$guardExitCode"
exit $guardExitCode
'@ | Set-Content -LiteralPath $captureScript
    $pwshPath = (Get-Process -Id $PID).Path
    $filter = '/*/*/GuardMtpTests/SelectedTestExecutes'
    # .NET 11 accepts an optional discovery format; specify it so the following
    # extension switch cannot be parsed as the format value.
    $listArguments = if ($supportsOverride) { @('--list-tests', 'text') } else { @('--list-tests') }
    foreach ($separator in @($false, $true)) {
        $extensionSeparator = if ($separator) { @('--') } else { @() }
        $payloadPath = Join-Path $testRoot 'discovery.json'
        @{ GuardScript = $guardScript; PhaseLog = $phaseLog; TimeoutSeconds = $smokeTimeoutSeconds; Arguments = $baseArguments + $listArguments + $extensionSeparator + @('--treenode-filter', $filter) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadPath
        $discoveryLog = Join-Path $diagnosticsRoot "discovery-$separator.log"
        $discoveryErrorLog = Join-Path $diagnosticsRoot "discovery-$separator.stderr.log"
        Write-MtpPhase "discovery host starting (separator=$separator); output=$discoveryLog; errors=$discoveryErrorLog"
        # Bound startup/teardown and diagnostic collection outside the inner
        # guard too. Redirect to disk so partial output survives host termination.
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
            Write-MtpPhase "discovery host started (separator=$separator); child pid=$($discoveryHost.Id)"
            try {
                # Include redirected-output drainage in the host deadline.
                $discoveryHost.WaitForExitAsync($hostTimeout.Token).GetAwaiter().GetResult()
            }
            catch [OperationCanceledException] {
                throw "MTP discovery host exceeded 90s timeout (separator=$separator)."
            }
            $discoveryExitCode = $discoveryHost.ExitCode
            Write-MtpPhase "discovery host returned (separator=$separator); child pid=$($discoveryHost.Id); exit=$discoveryExitCode"
        }
        finally {
            $hostTimeout.Dispose()
            if (-not $discoveryHost.HasExited) {
                $discoveryHost.Kill($true)
                if (-not $discoveryHost.WaitForExit(5000)) {
                    Write-Warning 'MTP discovery host did not exit within 5s of tree termination.' -WarningAction Continue
                }
            }
            $discoveryHost.Dispose()
        }
        $discovery = @(Get-Content -LiteralPath $discoveryLog) + @(Get-Content -LiteralPath $discoveryErrorLog)
        if ($discoveryExitCode -ne 0) { throw "MTP discovery failed (separator=$separator): $discoveryExitCode. $($discovery -join [Environment]::NewLine)" }
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
        Write-MtpPhase "filtered execution started (separator=$separator)"
        & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -SingleNode -DotNetArguments ($baseArguments + @('--results-directory', $results) + $extensionSeparator + @('--treenode-filter', $filter, '--report-trx'))
        Write-MtpPhase "filtered execution returned (separator=$separator); exit=$LASTEXITCODE"
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
        Write-MtpPhase "discovery and execution assertions passed (separator=$separator)"
    }

    Write-MtpPhase 'failure control started'
    & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -SingleNode -DotNetArguments ($baseArguments + @('--treenode-filter', '/*/*/GuardMtpTests/UnselectedTestFails'))
    Write-MtpPhase "failure control returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 2) { throw "Expected test-failure exit 2, received $LASTEXITCODE" }
    Write-MtpPhase 'zero-test control started'
    & $guardScript -TimeoutSeconds $smokeTimeoutSeconds -SingleNode -DotNetArguments ($baseArguments + @('--treenode-filter', '/*/*/GuardMtpTests/NoSuchTest'))
    Write-MtpPhase "zero-test control returned; exit=$LASTEXITCODE"
    if ($LASTEXITCODE -ne 8) { throw "Expected zero-tests exit 8, received $LASTEXITCODE" }
    Write-MtpPhase 'all MTP assertions passed'
    Write-Output 'OK MTP discovery and execution preserve filtering/counts with both separator forms; failure and zero-test exits remain visible.'
}
catch {
    Write-MtpPhase "FAILED: $_"
    throw
}
finally {
    Write-MtpPhase 'fixture cleanup started'
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
