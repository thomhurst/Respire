param([string]$SmokeScript = (Join-Path $PSScriptRoot 'Test-AgentDotNetVstest.ps1'))

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('vstest-diagnostics-test-' + [guid]::NewGuid())
$originalLocation = Get-Location
$environmentNames = @('RESPIRE_VSTEST_GUARD_TEST_MARKER', 'DOTNET_TEST_RUNNER')
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $scripts = New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts')
    Copy-Item -LiteralPath $SmokeScript -Destination (Join-Path $scripts 'Test-AgentDotNetVstest.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Destination $testRoot
    # Inject failures at real smoke boundaries without launching dotnet.
    @'
param([int]$TimeoutSeconds, [switch]$SingleNode, [string[]]$DotNetArguments)
$verb = $DotNetArguments[0]
$expectedTimeout = if ($verb -eq 'build') { 180 } else { 60 }
if ($TimeoutSeconds -ne $expectedTimeout) { throw "Expected timeout $expectedTimeout for $verb, received $TimeoutSeconds" }
if ($verb -eq 'new') { New-Item -ItemType Directory -Path fixture -Force | Out-Null }
$failureVerb = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'failure-verb.txt')
if ($failureVerb -eq 'failure-control' -and $verb -eq 'test') {
    if ($DotNetArguments -contains '--list-tests') {
        Write-Output 'GuardVstestTests.SelectedTestExecutes'
        $global:LASTEXITCODE = 0
        return
    }
    $isFailureControl = $DotNetArguments -contains 'FullyQualifiedName=GuardVstestTests.UnselectedTestFails'
    if (-not $isFailureControl) { 'executed' | Set-Content -LiteralPath $env:RESPIRE_VSTEST_GUARD_TEST_MARKER }
    $mode = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'failure-report-mode.txt')
    $results = $DotNetArguments[[Array]::IndexOf($DotNetArguments, '--results-directory') + 1]
    if (-not $isFailureControl -or $mode -ne 'missing') {
        New-Item -ItemType Directory -Path $results -Force | Out-Null
        $name = if ($isFailureControl) { 'GuardVstestTests.UnselectedTestFails' } else { 'GuardVstestTests.SelectedTestExecutes' }
        $outcome = if ($isFailureControl) { 'Failed' } else { 'Passed' }
        $executed = 1
        if ($isFailureControl -and $mode -eq 'aborted') { $executed = 0 }
        if ($isFailureControl -and $mode -eq 'wrong-test') { $name = 'AnotherTest' }
        if ($isFailureControl -and $mode -eq 'wrong-outcome') { $outcome = 'NotExecuted' }
        $message = if ($isFailureControl -and $mode -eq 'wrong-error') { 'test host aborted' } else { 'guard failure control' }
        $failed = if ($isFailureControl) { 1 } else { 0 }
        $passed = 1 - $failed
        '<TestRun><ResultSummary><Counters total="1" executed="{0}" passed="{1}" failed="{2}" /></ResultSummary><Results><UnitTestResult testName="{3}" outcome="{4}"><Output><ErrorInfo><Message>{5}</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>' -f $executed, $passed, $failed, $name, $outcome, $message |
            Set-Content -LiteralPath (Join-Path $results 'report.trx')
    }
    $global:LASTEXITCODE = if ($isFailureControl) { 1 } else { 0 }
    return
}
if ($verb -eq $failureVerb) {
    if ($verb -eq 'test') { [Console]::WriteLine('partial discovery output control') }
    if ($verb -eq 'test' -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'stall.txt'))) {
        # Deliberately exceed the short stall timer before announcing readiness.
        [Threading.Thread]::Sleep(4000)
        $PID | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'host-pid.txt')
        [Threading.Thread]::Sleep(30000)
    }
    $global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'exit-code.txt'))
} else {
    $global:LASTEXITCODE = 0
}
'@ | Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1')

    foreach ($boundary in @(
        @{ Verb = 'new'; Phase = 'fixture creation' },
        @{ Verb = 'build'; Phase = 'fixture build' },
        @{ Verb = 'test'; Phase = 'discovery' }
    )) {
        $boundary.Verb | Set-Content -LiteralPath (Join-Path $scripts 'failure-verb.txt')
        foreach ($guardFailureExitCode in @(73, 124)) {
            $guardFailureExitCode | Set-Content -LiteralPath (Join-Path $scripts 'exit-code.txt')
            foreach ($failPhaseWrites in @($false, $true)) {
                if ($failPhaseWrites) {
                    function Add-Content {
                        [CmdletBinding()]
                        param([string]$LiteralPath, [object]$Value)
                        throw 'phase write failure control'
                    }
                } else {
                    function Add-Content {
                        [CmdletBinding()]
                        param([string]$LiteralPath, [object]$Value)
                        Microsoft.PowerShell.Management\Add-Content -LiteralPath $LiteralPath -Value $Value
                    }
                }
                foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, "sentinel-$name") }
                $diagnostics = Join-Path $testRoot "diagnostics-$($boundary.Verb)-$guardFailureExitCode-$failPhaseWrites"
                $messages = [Collections.Generic.List[string]]::new()
                $failure = $null
                try {
                    & (Join-Path $scripts 'Test-AgentDotNetVstest.ps1') -DiagnosticsDirectory $diagnostics *>&1 |
                        ForEach-Object { $messages.Add([string]$_) }
                }
                catch { $failure = $_ }
                $expected = "VSTest $($boundary.Phase) failed: $guardFailureExitCode"
                if ([string]$failure -notlike "*$expected*") { throw "Original failure lost: $failure" }
                if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Fixture location was not restored.' }
                foreach ($name in $environmentNames) {
                    if ([Environment]::GetEnvironmentVariable($name) -ne "sentinel-$name") { throw "Environment was not restored: $name" }
                }
                $fixture = @(Get-ChildItem -LiteralPath $diagnostics -Directory)
                if ($fixture.Count -ne 1) { throw 'Expected one diagnostics directory.' }
                if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $fixture[0].Name)) { throw 'Fixture was not deleted.' }
                foreach ($marker in @("FAILED: $expected", 'fixture cleanup started')) {
                    if (-not ($messages | Where-Object { $_.Contains($marker) })) { throw "Missing live marker: $marker" }
                }
                if ($failPhaseWrites) {
                    if (-not ($messages | Where-Object { $_.Contains('phase write failure control') })) { throw 'Phase write fault was not exercised.' }
                } else {
                    $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
                    if (-not $saved.Contains('fixture cleanup started')) { throw 'Healthy phase logging did not persist cleanup.' }
                }
                if ($boundary.Verb -eq 'test') {
                    $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'discovery.log') -Raw
                    foreach ($marker in @('partial discovery output control', 'discovery host entered guard', "discovery guard returned; exit=$guardFailureExitCode")) {
                        if (-not $saved.Contains($marker)) { throw "Partial discovery output lost: $marker" }
                    }
                }
                Remove-Item -LiteralPath Function:\Add-Content
            }
        }
    }

    # Relative paths follow PowerShell's location even when the process differs.
    'new' | Set-Content -LiteralPath (Join-Path $scripts 'failure-verb.txt')
    $processDirectory = [Environment]::CurrentDirectory
    $failure = $null
    try {
        [Environment]::CurrentDirectory = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'process-location')).FullName
        Set-Location -LiteralPath $testRoot
        try {
            & (Join-Path $scripts 'Test-AgentDotNetVstest.ps1') -DiagnosticsDirectory 'relative-diagnostics' *>&1 | Out-Null
        }
        catch { $failure = $_ }
        if ((Get-Location).Path -ne $testRoot) { throw 'Relative-path fixture location was not restored.' }
    }
    finally {
        Set-Location -LiteralPath $originalLocation.Path
        [Environment]::CurrentDirectory = $processDirectory
    }
    if ([string]$failure -notlike '*VSTest fixture creation failed: 124*') { throw "Relative-path failure lost: $failure" }
    $relativeDiagnostics = Join-Path $testRoot 'relative-diagnostics'
    $fixture = @(Get-ChildItem -LiteralPath $relativeDiagnostics -Directory)
    if ($fixture.Count -ne 1) { throw 'Expected diagnostics under the PowerShell location.' }
    $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
    if (-not $saved.Contains('fixture cleanup started')) { throw 'Relative diagnostics did not retain cleanup.' }

    'failure-control' | Set-Content -LiteralPath (Join-Path $scripts 'failure-verb.txt')
    foreach ($mode in @('valid', 'missing', 'aborted', 'wrong-test', 'wrong-outcome', 'wrong-error')) {
        $mode | Set-Content -LiteralPath (Join-Path $scripts 'failure-report-mode.txt')
        $failure = $null
        try {
            & (Join-Path $scripts 'Test-AgentDotNetVstest.ps1') -DiagnosticsDirectory (Join-Path $testRoot "failure-report-$mode") *>&1 | Out-Null
        }
        catch { $failure = $_ }
        if ($mode -eq 'valid') {
            if ($failure) { throw "Valid failure-control report was rejected: $failure" }
            $fixture = @(Get-ChildItem -LiteralPath (Join-Path $testRoot "failure-report-$mode") -Directory)
            $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
            if (-not $saved.Contains('all VSTest assertions passed')) { throw 'Failure control did not exercise healthy phase logging.' }
        } else {
            $expected = if ($mode -eq 'missing') { 'Expected one failure-control TRX report' } else { 'Expected exactly the failure-control VSTest test' }
            if ([string]$failure -notlike "*$expected*") { throw "Invalid failure-control report ($mode) was not rejected: $failure" }
        }
    }
    'test' | Set-Content -LiteralPath (Join-Path $scripts 'failure-verb.txt')

    # Exercise the generated discovery host with an unwritable phase path, too.
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($SmokeScript, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Smoke script has parse errors: $parseErrors" }
    $capture = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value.StartsWith('param([string]$PayloadPath)')
    }, $true)
    if (-not $capture) { throw 'Discovery host script was not found.' }
    $capturePath = Join-Path $testRoot 'Capture-Guard.ps1'
    $capture.Value | Set-Content -LiteralPath $capturePath
    $payloadPath = Join-Path $testRoot 'discovery.json'
    @{ PhaseLog = $scripts.FullName; GuardScript = (Join-Path $scripts 'Invoke-AgentDotNet.ps1'); TimeoutSeconds = 60; Arguments = @('test') } |
        ConvertTo-Json | Set-Content -LiteralPath $payloadPath
    foreach ($guardFailureExitCode in @(73, 124)) {
        $guardFailureExitCode | Set-Content -LiteralPath (Join-Path $scripts 'exit-code.txt')
        $output = & (Get-Process -Id $PID).Path -NoProfile -File $capturePath $payloadPath 2>&1
        if ($LASTEXITCODE -ne $guardFailureExitCode) { throw "Discovery host lost guard exit: $LASTEXITCODE" }
        foreach ($marker in @('discovery host entered guard', "discovery guard returned; exit=$guardFailureExitCode", 'Could not append VSTest discovery phase log')) {
            if (-not ($output | Where-Object { ([string]$_).Contains($marker) })) { throw "Missing discovery marker: $marker" }
        }
    }

    # Shorten only the copied host deadline to exercise a real stalled capture
    # without spending the production 90-second allowance in each CI matrix leg.
    $smokeSource = Get-Content -LiteralPath $SmokeScript -Raw
    if (-not $smokeSource.Contains('[TimeSpan]::FromSeconds(90)')) { throw 'Host deadline was not found.' }
    $smokeSource.Replace('[TimeSpan]::FromSeconds(90)', '[TimeSpan]::FromSeconds(3)') |
        Set-Content -LiteralPath (Join-Path $scripts 'Test-AgentDotNetVstest.ps1')
    New-Item -ItemType File -Path (Join-Path $scripts 'stall.txt') | Out-Null
    # Keep process startup separate from the shortened stall deadline. The smoke
    # retains its production 90-second startup/teardown bound; only this test
    # waits for the fake guard's PID before returning the real process handle.
    function Start-Process {
        [CmdletBinding()]
        param([string]$FilePath, [string[]]$ArgumentList, [string]$RedirectStandardOutput,
            [string]$RedirectStandardError, [switch]$PassThru, [string]$WindowStyle)
        $hostProcess = Microsoft.PowerShell.Management\Start-Process @PSBoundParameters
        $startup = [Diagnostics.Stopwatch]::StartNew()
        try {
            while (-not (Test-Path -LiteralPath (Join-Path $scripts 'host-pid.txt'))) {
                if ($hostProcess.WaitForExit(50)) { throw 'Stall host exited before readiness.' }
                if ($startup.Elapsed.TotalSeconds -ge 30) { throw 'Stall host did not become ready within 30s.' }
            }
            return $hostProcess
        }
        catch {
            if (-not $hostProcess.HasExited) {
                $hostProcess.Kill($true)
                $null = $hostProcess.WaitForExit(5000)
            }
            $hostProcess.Dispose()
            throw
        }
    }
    $diagnostics = Join-Path $testRoot 'diagnostics-stall'
    $failure = $null
    try {
        & (Join-Path $scripts 'Test-AgentDotNetVstest.ps1') -DiagnosticsDirectory $diagnostics *>&1 | Out-Null
    }
    catch { $failure = $_ }
    if ([string]$failure -notlike '*VSTest discovery host exceeded 90s timeout.*') { throw "Host timeout lost: $failure" }
    $hostPid = [int](Get-Content -LiteralPath (Join-Path $scripts 'host-pid.txt'))
    if (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) { throw "Stalled discovery host survived: $hostPid" }
    $fixture = @(Get-ChildItem -LiteralPath $diagnostics -Directory)
    $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
    if (-not $saved.Contains('FAILED: VSTest discovery host exceeded 90s timeout.')) { throw 'Stall control did not exercise healthy phase logging.' }
    $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'discovery.log') -Raw
    if (-not $saved.Contains('partial discovery output control')) { throw 'Timed-out discovery output was lost.' }
    if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $fixture[0].Name)) { throw 'Timed-out fixture was not deleted.' }
    if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Timed-out location was not restored.' }
    foreach ($name in $environmentNames) {
        if ([Environment]::GetEnvironmentVariable($name) -ne "sentinel-$name") { throw "Timed-out environment was not restored: $name" }
    }
    Write-Output 'OK VSTest diagnostics retain partial discovery output and preserve guard exits, original errors, location/environment restoration and fixture cleanup when phase writes fail.'
}
finally {
    Set-Location -LiteralPath $originalLocation.Path
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}

exit 0
