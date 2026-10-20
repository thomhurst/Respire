$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("agent-arguments-diagnostics-{0}" -f [guid]::NewGuid())
$originalDirectory = (Get-Location).ProviderPath
$variables = @('RESPIRE_GUARD_TEST_CAPTURE', 'RESPIRE_GUARD_TEST_DIRECTORY', 'DOTNET_TEST_RUNNER',
    'RESPIRE_GUARD_TEST_SDK_VERSION', 'RESPIRE_GUARD_TEST_PHASE_LOG')
$previousEnvironment = @{}
foreach ($variable in $variables) { $previousEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable) }
try {
    $fixtureScripts = Join-Path $testRoot 'scripts'
    New-Item -ItemType Directory -Path $fixtureScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-AgentDotNetArguments.ps1') -Destination $fixtureScripts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Destination $testRoot
    $smoke = Join-Path $fixtureScripts 'Test-AgentDotNetArguments.ps1'
    $stub = Join-Path $fixtureScripts 'Invoke-AgentDotNet.ps1'
    # Fail at the first guard boundary without launching a native process. This
    # checks that diagnostic I/O cannot hide the failure or skip fixture cleanup.
    @'
param($TimeoutSeconds, $DotNetPath, [switch]$SingleNode, [string[]]$DotNetArguments)
if ($TimeoutSeconds -ne 60 -or $DotNetPath -ne 'dotnet' -or $SingleNode -or
    $DotNetArguments.Count -ne 4 -or $DotNetArguments[0] -ne 'build' -or
    $DotNetArguments[2] -ne '/m:1' -or $DotNetArguments[3] -ne '--nologo') {
    throw 'Fixture build invocation changed.'
}
STUB_RESULT
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'stub-template.txt')

    foreach ($case in @(
        @{ Name = 'retained-failure'; Result = 'exit 73'; Expected = 'Argument probe build failed.'; ExitCode = 73 },
        @{ Name = 'retained-timeout'; Result = 'exit 124'; Expected = 'Argument probe build failed.'; ExitCode = 124 },
        @{ Name = 'blocked-append'; Result = "throw 'guard fault sentinel'"; Expected = 'guard fault sentinel' },
        @{ Name = 'blocked-directory'; Result = 'exit 73'; Expected = 'Argument probe build failed.'; ExitCode = 73 }
    )) {
        $diagnostics = Join-Path $testRoot $case.Name
        $template = Get-Content -LiteralPath (Join-Path $testRoot 'stub-template.txt') -Raw
        $template.Replace('STUB_RESULT', $case.Result) | Set-Content -LiteralPath $stub
        if ($case.Name -eq 'blocked-append') {
            New-Item -ItemType Directory -Path (Join-Path $diagnostics 'arguments-phases.log') -Force | Out-Null
        }
        if ($case.Name -eq 'blocked-directory') { 'not a directory' | Set-Content -LiteralPath $diagnostics }
        foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, "sentinel-$variable") }
        $writer = [IO.StringWriter]::new()
        $console = [Console]::Out
        $failure = $null
        try {
            [Console]::SetOut($writer)
            try { & $smoke -DiagnosticsDirectory $diagnostics }
            catch { $failure = $_.Exception.Message }
            $guardExitCode = $LASTEXITCODE
        }
        finally { [Console]::SetOut($console) }
        $live = $writer.ToString()
        $writer.Dispose()
        if ($failure -ne $case.Expected) { throw "$($case.Name): original failure replaced: $failure" }
        if ($case.ContainsKey('ExitCode') -and $guardExitCode -ne $case.ExitCode) {
            throw "$($case.Name): guard exit code lost: $guardExitCode"
        }
        foreach ($phase in @('setup-enter', 'fixture-build guard-enter timeoutSeconds=60', 'smoke-failed', 'cleanup-enter', 'cleanup-return')) {
            if (-not $live.Contains($phase)) { throw "$($case.Name): live marker missing: $phase" }
        }
        $boundary = if ($case.ContainsKey('ExitCode')) { "guard-return exit=$($case.ExitCode)" } else { 'guard-throw: guard fault sentinel' }
        if (-not $live.Contains($boundary)) { throw "$($case.Name): missing guard outcome: $boundary" }
        if ($live -notmatch 'hostPid=\d+' -or $live -notmatch 'fixture=(.+?) pwsh=') { throw 'Process/fixture identity missing.' }
        $fixtureRoot = $Matches[1]
        if (Test-Path -LiteralPath $fixtureRoot) { throw "Fixture was not removed: $fixtureRoot" }
        if ((Get-Location).ProviderPath -ne $originalDirectory) { throw 'Location was not restored.' }
        foreach ($variable in $variables) {
            if ([Environment]::GetEnvironmentVariable($variable) -cne "sentinel-$variable") { throw "Environment was not restored: $variable" }
        }
        if ($case.Name.StartsWith('retained-')) {
            $retained = [IO.File]::ReadAllText((Join-Path $diagnostics 'arguments-phases.log'))
            if ($retained -cne $live) { throw 'Retained phase log differs from the live markers.' }
        }
    }
    Write-Output 'OK argument diagnostics preserve failures, guard limits, environment, location and cleanup when writes fail.'
}
finally {
    foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $previousEnvironment[$variable]) }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing cleanup outside temp root: $resolvedRoot" }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
