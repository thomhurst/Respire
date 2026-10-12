<#
.SYNOPSIS
Checks diagnostic build provenance and capture safeguards without Redis or allocation tracing.
#>
[CmdletBinding()]
param([ValidateSet('batch', 'stream')][string]$Kind = 'batch')
$ErrorActionPreference = 'Stop'
$captureScript = if ($Kind -eq 'stream') { 'Invoke-TransportStreamDiagnostics.ps1' } else { 'Invoke-TransportBatchDiagnostics.ps1' }
$captureCount = if ($Kind -eq 'stream') { 24 } else { 16 }
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('respire-diagnostic-test-' + [Guid]::NewGuid())
$previousMetrics = $env:RESPIRE_BENCH_ERRORS
try {
    $cases = @('net10.0', 'net8.0', 'build-failure', 'revision-change', 'source-change', 'dirty-sdk-selector', 'sdk-selector-change', 'missing-assembly')
    if ($Kind -eq 'stream') { $cases += @('existing-destination', 'launch-failure', 'conversion-failure') }
    foreach ($case in $cases) {
        $fixture = Join-Path $fixtureRoot $case
        $scripts = Join-Path $fixture 'scripts'
        $source = Join-Path $fixture 'benchmarks/Respire.Benchmarks'
        New-Item -ItemType Directory -Path $scripts, $source -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $captureScript) -Destination $scripts
        Set-Content -LiteralPath (Join-Path $source 'Respire.Benchmarks.csproj') -Value '<Project />'
        Set-Content -LiteralPath (Join-Path $fixture '.gitignore') -Value 'bin/'
        Set-Content -LiteralPath (Join-Path $fixture 'global.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $scripts 'Invoke-AgentDotNet.ps1') -Value @'
param([switch]$SingleNode, [string[]]$DotNetArguments)
$repo = Split-Path $PSScriptRoot -Parent
$case = Split-Path $repo -Leaf
$revision = (git -C $repo rev-parse HEAD).Trim()
if ($DotNetArguments[0] -eq 'build') {
    if (!$SingleNode -or $DotNetArguments -notcontains '-t:Rebuild' -or
        $DotNetArguments[3] -ne '-c' -or $DotNetArguments[4] -ne 'Release') {
        throw 'Capture must rebuild Release through the single-node guard.'
    }
    if ($case -eq 'build-failure') { exit 137 }
    if ($case -eq 'revision-change') {
        git -C $repo -c user.name=Test -c user.email=test@example.invalid commit --allow-empty -qm changed
    }
    if ($case -eq 'source-change') {
        Add-Content -LiteralPath (Join-Path $repo 'benchmarks/Respire.Benchmarks/Respire.Benchmarks.csproj') -Value 'changed'
    }
    if ($case -eq 'sdk-selector-change') {
        Set-Content -LiteralPath (Join-Path $repo 'global.json') -Value '{"sdk":{"version":"10.0.100"}}'
    }
    $framework = $DotNetArguments[-1]
    $binary = Join-Path $repo "benchmarks/Respire.Benchmarks/bin/Release/$framework/Respire.Benchmarks.dll"
    New-Item -ItemType Directory -Path (Split-Path $binary -Parent) -Force | Out-Null
    if ($case -ne 'missing-assembly') { Set-Content -LiteralPath $binary -Value $revision }
}
else {
    if ((Get-Content -LiteralPath $DotNetArguments[0] -Raw).Trim() -ne $revision) {
        throw 'Capture used a stale assembly.'
    }
    if ($DotNetArguments[1] -like '--transport-*-diagnostics' -and $DotNetArguments[3] -ne $revision) {
        throw 'Capture used a stale revision.'
    }
    Add-Content -LiteralPath (Join-Path $repo 'captures.log') -Value $DotNetArguments[1]
    $destination = $DotNetArguments[2]
    if ($DotNetArguments[1] -like '--transport-*-diagnostics') {
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $destination 'allocations.nettrace') -Value 'trace'
        if ($case -eq 'launch-failure' -and $DotNetArguments[7] -eq '2') { exit 1 }
    }
    else {
        if ($case -eq 'conversion-failure' -and (Split-Path (Split-Path $destination -Parent) -Leaf) -like '*-l2') { exit 1 }
        Set-Content -LiteralPath $DotNetArguments[3] -Value 'report'
    }
}
exit 0
'@
        git -C $fixture init -q
        git -C $fixture add .
        git -C $fixture -c user.name=Test -c user.email=test@example.invalid commit -qm initial
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the test repository.' }
        if ($case -eq 'dirty-sdk-selector') {
            Set-Content -LiteralPath (Join-Path $fixture 'global.json') -Value '{"sdk":{"version":"10.0.100"}}'
        }
        $framework = if ($case -eq 'net8.0') { 'net8.0' } else { 'net10.0' }
        if ($case -ne 'missing-assembly') {
            $binaryDirectory = Join-Path $source "bin/Release/$framework"
            New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $binaryDirectory 'Respire.Benchmarks.dll') -Value 'stale assembly'
        }
        $env:RESPIRE_BENCH_ERRORS = 'original'
        $output = Join-Path $fixture 'output'
        $secondDestination = Join-Path $output 'disabled/c1-instant-l2'
        $sentinel = Join-Path $secondDestination 'allocations.nettrace'
        if ($case -eq 'existing-destination') {
            New-Item -ItemType Directory -Path $secondDestination -Force | Out-Null
            Set-Content -LiteralPath $sentinel -Value 'existing evidence'
        }
        $failure = $null
        try {
            & (Join-Path $scripts $captureScript) -Framework $framework -OutputDirectory $output
        }
        catch { $failure = $_.Exception.Message }
        $expected = switch ($case) {
            'build-failure' { 'Diagnostic rebuild failed: net10.0' }
            'revision-change' { 'Diagnostic revision changed during the rebuild.' }
            'source-change' { 'Measured source changed during the rebuild.' }
            'dirty-sdk-selector' { 'Commit measured source before capturing revision-stamped evidence.' }
            'sdk-selector-change' { 'Measured source changed during the rebuild.' }
            'missing-assembly' { 'Diagnostic rebuild did not produce the net10.0 benchmark assembly.' }
            'existing-destination' { "Diagnostic destination already exists: $secondDestination" }
            'launch-failure' { "Diagnostic launch failed: $secondDestination" }
            'conversion-failure' { "Trace conversion failed: $secondDestination" }
            default { $null }
        }
        if ($failure -ne $expected) { throw "${case}: expected '$expected', got '$failure'." }
        $captureLog = Join-Path $fixture 'captures.log'
        if ($case -in @('existing-destination', 'launch-failure', 'conversion-failure')) {
            $captures = @(Get-Content -LiteralPath $captureLog)
            $expectedCalls = @('--transport-stream-diagnostics', '--transport-stream-trace-report')
            if ($case -ne 'existing-destination') { $expectedCalls += '--transport-stream-diagnostics' }
            if ($case -eq 'conversion-failure') { $expectedCalls += '--transport-stream-trace-report' }
            if (($captures -join ',') -ne ($expectedCalls -join ',')) {
                throw "${case}: capture did not stop at the failed launch or conversion."
            }
            if ($case -eq 'existing-destination' -and (Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'existing evidence') {
                throw "${case}: existing evidence was overwritten."
            }
            if (Test-Path -LiteralPath (Join-Path $secondDestination 'allocations.jsonl')) {
                throw "${case}: failed destination has a converted report."
            }
        }
        elseif ($expected) {
            if (Test-Path -LiteralPath $captureLog) { throw "${case}: captured evidence after failed provenance check." }
        }
        else {
            $captures = @(Get-Content -LiteralPath $captureLog)
            if (@($captures | Where-Object { $_ -eq "--transport-$Kind-diagnostics" }).Count -ne $captureCount -or
                @($captures | Where-Object { $_ -eq "--transport-$Kind-trace-report" }).Count -ne $captureCount) {
                throw "${case}: expected $captureCount captures and $captureCount conversions."
            }
        }
        if ($env:RESPIRE_BENCH_ERRORS -ne 'original') { throw "${case}: metric environment was not restored." }
        Write-Host "PASS $case"
    }
}
finally {
    $env:RESPIRE_BENCH_ERRORS = $previousMetrics
    $resolvedRoot = [IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test cleanup path is outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
