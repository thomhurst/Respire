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
param([switch]$SingleNode, [string[]]$DotNetArguments)
$global:LASTEXITCODE = 73
'@ | Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1')

    foreach ($failPhaseWrites in @($false, $true)) {
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
        $diagnostics = Join-Path $testRoot "diagnostics-$failPhaseWrites"
        $messages = [Collections.Generic.List[string]]::new()
        $failure = $null
        try {
            & (Join-Path $scripts 'Test-AgentDotNetMtp.ps1') -DiagnosticsDirectory $diagnostics *>&1 |
                ForEach-Object { $messages.Add([string]$_) }
        }
        catch { $failure = $_ }
        if ([string]$failure -notlike '*MTP fixture build failed: 73*') { throw "Original failure lost: $failure" }
        if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Fixture location was not restored.' }
        foreach ($name in $environmentNames) {
            if ([Environment]::GetEnvironmentVariable($name) -ne "sentinel-$name") { throw "Environment was not restored: $name" }
        }
        $fixture = @(Get-ChildItem -LiteralPath $diagnostics -Directory)
        if ($fixture.Count -ne 1) { throw 'Expected one diagnostics directory.' }
        if (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) $fixture[0].Name)) { throw 'Fixture was not deleted.' }
        foreach ($marker in @('FAILED: MTP fixture build failed: 73', 'fixture cleanup started')) {
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
    @{ PhaseLog = $scripts.FullName; GuardScript = (Join-Path $scripts 'Invoke-AgentDotNet.ps1'); Arguments = @('build') } |
        ConvertTo-Json | Set-Content -LiteralPath $payloadPath
    $output = & (Get-Process -Id $PID).Path -NoProfile -File $capturePath $payloadPath 2>&1
    if ($LASTEXITCODE -ne 73) { throw "Discovery host lost guard exit: $LASTEXITCODE" }
    foreach ($marker in @('discovery host entered guard', 'discovery guard returned; exit=73')) {
        if (-not ($output | Where-Object { ([string]$_).Contains($marker) })) { throw "Missing discovery marker: $marker" }
    }
    Write-Output 'OK MTP diagnostic write failures preserve live markers, guard exits, original errors, location/environment restoration and fixture cleanup.'
}
finally {
    Set-Location -LiteralPath $originalLocation.Path
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name]) }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

# The expected native failure control must not escape into the Actions pwsh step.
exit 0
