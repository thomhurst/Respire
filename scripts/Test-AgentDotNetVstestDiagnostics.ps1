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
if ($verb -eq $failureVerb) {
    if ($verb -eq 'test') { [Console]::WriteLine('partial discovery output control') }
    if ($verb -eq 'test' -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'stall.txt'))) {
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
            }
        }
    }

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
