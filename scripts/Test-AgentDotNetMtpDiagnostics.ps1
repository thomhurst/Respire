param([string]$SmokeScript = (Join-Path $PSScriptRoot 'Test-AgentDotNetMtp.ps1'))

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('mtp-diagnostics-test-' + [guid]::NewGuid())
$originalLocation = Get-Location
$environmentNames = @('RESPIRE_MTP_GUARD_TEST_MARKER', 'TUNIT_DISABLE_HTML_REPORTER', 'DOTNET_TEST_RUNNER')
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $scripts = New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts')
    Copy-Item -LiteralPath $SmokeScript -Destination (Join-Path $scripts 'Test-AgentDotNetMtp.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Destination $testRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Directory.Packages.props') -Destination $testRoot
    # Fail at the guard boundary without launching dotnet or changing its limits.
    @'
param([int]$TimeoutSeconds, [switch]$SingleNode, [string[]]$DotNetArguments)
$expectedTimeout = if ($DotNetArguments[0] -eq 'build') { 180 } else { 60 }
if ($TimeoutSeconds -ne $expectedTimeout) { throw "Expected timeout $expectedTimeout for $($DotNetArguments[0]), received $TimeoutSeconds" }
$global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'exit-code.txt'))
'@ | Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1')

    foreach ($testCase in @(
        @{ ExitCode = 73; FailPhaseWrites = $false },
        @{ ExitCode = 73; FailPhaseWrites = $true },
        @{ ExitCode = 124; FailPhaseWrites = $false },
        @{ ExitCode = 124; FailPhaseWrites = $true }
    )) {
        $guardFailureExitCode = $testCase.ExitCode
        $failPhaseWrites = $testCase.FailPhaseWrites
        $guardFailureExitCode | Set-Content -LiteralPath (Join-Path $scripts 'exit-code.txt')
        if ($failPhaseWrites) {
            function Add-Content {
                [CmdletBinding()]
                param([string]$LiteralPath, [object]$Value)
                throw 'phase write failure control'
            }
        }
        else {
            function Add-Content {
                [CmdletBinding()]
                param([string]$LiteralPath, [object]$Value)
                Microsoft.PowerShell.Management\Add-Content -LiteralPath $LiteralPath -Value $Value
            }
        }
        foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, "sentinel-$name") }
        $diagnostics = Join-Path $testRoot "diagnostics-$guardFailureExitCode-$failPhaseWrites"
        $messages = [Collections.Generic.List[string]]::new()
        $failure = $null
        try {
            & (Join-Path $scripts 'Test-AgentDotNetMtp.ps1') -DiagnosticsDirectory $diagnostics *>&1 |
                ForEach-Object { $messages.Add([string]$_) }
        }
        catch { $failure = $_ }
        if ([string]$failure -notlike "*MTP fixture build failed: $guardFailureExitCode*") { throw "Original failure lost: $failure" }
        if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Fixture location was not restored.' }
        foreach ($name in $environmentNames) {
            if ([Environment]::GetEnvironmentVariable($name) -ne "sentinel-$name") { throw "Environment was not restored: $name" }
        }
        $fixture = @(Get-ChildItem -LiteralPath $diagnostics -Directory)
        if ($fixture.Count -ne 1) { throw 'Expected one diagnostics directory.' }
        if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $fixture[0].Name)) { throw 'Fixture was not deleted.' }
        foreach ($marker in @("FAILED: MTP fixture build failed: $guardFailureExitCode", 'fixture cleanup started')) {
            if (-not ($messages | Where-Object { $_.Contains($marker) })) { throw "Missing live marker: $marker" }
        }
        if ($failPhaseWrites) {
            if (-not ($messages | Where-Object { $_.Contains('phase write failure control') })) { throw 'Phase write fault was not exercised.' }
        }
        else {
            $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
            if (-not $saved.Contains('fixture cleanup started')) { throw 'Healthy phase logging did not persist cleanup.' }
        }
    }

    # Exercise the actual generated discovery host with an unwritable phase path.
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
        foreach ($marker in @('discovery host entered guard', "discovery guard returned; exit=$guardFailureExitCode")) {
            if (-not ($output | Where-Object { ([string]$_).Contains($marker) })) { throw "Missing discovery marker: $marker" }
        }
    }

    # A host can stall outside the inner guard's timeout, including while
    # collecting startup-failure evidence. Exercise the actual smoke boundary.
    Remove-Item -LiteralPath Function:Add-Content
    @'
param([int]$TimeoutSeconds, [switch]$SingleNode, [string[]]$DotNetArguments)
if ($DotNetArguments[0] -eq 'build') { $global:LASTEXITCODE = 0; return }
$global:LASTEXITCODE = [int](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'exit-code.txt'))
'@ | Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1')
    foreach ($guardFailureExitCode in @(73, 124)) {
        $guardFailureExitCode | Set-Content -LiteralPath (Join-Path $scripts 'exit-code.txt')
        $failure = $null
        try {
            & (Join-Path $scripts 'Test-AgentDotNetMtp.ps1') -DiagnosticsDirectory (Join-Path $testRoot "discovery-exit-$guardFailureExitCode") *>&1 | Out-Null
        }
        catch { $failure = $_ }
        if ([string]$failure -notlike "*MTP discovery failed (separator=False): $guardFailureExitCode.*") { throw "Discovery exit lost: $failure" }
    }
    @'
param([int]$TimeoutSeconds, [switch]$SingleNode, [string[]]$DotNetArguments)
if ($DotNetArguments[0] -eq 'build') { $global:LASTEXITCODE = 0; return }
Start-Sleep -Seconds 4
$PID | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'host-pid.txt')
Write-Output 'partial discovery output control'
[Console]::Error.WriteLine('partial discovery error control')
Start-Sleep -Seconds 8
$global:LASTEXITCODE = 73
'@ | Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1')
    (Get-Content -LiteralPath $SmokeScript -Raw).Replace('[TimeSpan]::FromSeconds(90)', '[TimeSpan]::FromSeconds(3)') |
        Set-Content -LiteralPath (Join-Path $scripts 'Test-AgentDotNetMtp.ps1')
    # Keep cold process startup outside the shortened test deadline. The real
    # script retains its full 90-second startup/output-drainage allowance.
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
        & (Join-Path $scripts 'Test-AgentDotNetMtp.ps1') -DiagnosticsDirectory $diagnostics *>&1 | Out-Null
    }
    catch { $failure = $_ }
    if ([string]$failure -notlike '*MTP discovery host exceeded 90s timeout*') { throw "Host timeout lost: $failure" }
    $hostPid = [int](Get-Content -LiteralPath (Join-Path $scripts 'host-pid.txt'))
    if (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) { throw "Stalled discovery host survived: $hostPid" }
    $fixture = @(Get-ChildItem -LiteralPath $diagnostics -Directory)
    foreach ($log in @('discovery-False.log', 'discovery-False.stderr.log')) {
        $saved = Get-Content -LiteralPath (Join-Path $fixture[0].FullName $log) -Raw
        if ($saved -notlike '*partial discovery*control*') { throw "Timed-out discovery output was lost: $log" }
    }
    $phases = Get-Content -LiteralPath (Join-Path $fixture[0].FullName 'phases.log') -Raw
    if (-not $phases.Contains('fixture cleanup started')) { throw 'Timed-out cleanup was not logged.' }
    if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $fixture[0].Name)) { throw 'Timed-out fixture was not deleted.' }
    if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Timed-out location was not restored.' }
    foreach ($name in $environmentNames) {
        if ([Environment]::GetEnvironmentVariable($name) -ne "sentinel-$name") { throw "Timed-out environment was not restored: $name" }
    }
    Write-Output 'OK MTP diagnostics preserve guard exits and live markers; stalled discovery terminates with retained output, restored state and fixture cleanup.'
}
finally {
    Set-Location -LiteralPath $originalLocation.Path
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

# The expected native failure control must not escape into the Actions pwsh step.
exit 0
