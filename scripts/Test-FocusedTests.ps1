$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$guard = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$project = Join-Path $repo 'tests/Respire.Tests/Respire.Tests.csproj'

function Invoke-Inspection([string[]]$ExtraArguments, [switch]$ExpectFailure) {
    $arguments = @('msbuild', $project, '-nologo', '-p:TargetFramework=net10.0',
        '-p:ContinuousIntegrationBuild=false') + $ExtraArguments
    # A child PowerShell captures the guard's inherited native stdout as well as its
    # own output. The dotnet command itself still runs inside the normal guard.
    $output = & pwsh -NoProfile -Command {
        param($guardPath, $json)
        & $guardPath -DotNetArguments ($json | ConvertFrom-Json)
        exit $LASTEXITCODE
    } -args $guard, (ConvertTo-Json -Compress -InputObject $arguments) 2>&1
    $code = $LASTEXITCODE
    if ($ExpectFailure) {
        if ($code -eq 0) { throw 'Expected focused validation to reject this invocation.' }
        return ($output -join "`n")
    }
    if ($code -ne 0) { throw ($output -join "`n") }
    return (($output -join "`n") | ConvertFrom-Json)
}

Push-Location $repo
try {
    $query = @('-getItem:Compile,ProjectReference,PackageReference,EmbeddedResource',
        '-getProperty:BaseIntermediateOutputPath,BaseOutputPath')
    $full = Invoke-Inspection $query
    $focused = Invoke-Inspection ($query + @('-t:SelectFocusedTestSources',
        '-p:RespireTestFilter=TimeSeries*.cs|Networking/ProxyTests.cs'))
    $sources = @($focused.Items.Compile.Identity | ForEach-Object { $_.Replace('\', '/') })
    foreach ($required in @('TimeSeriesClientTests.cs', 'TimeSeriesReadTests.cs',
        'Networking/ProxyTests.cs', 'Program.cs', 'TestConfiguration.cs',
        'Networking/FakeRespServer.cs', '../GeneratedHashModelScenarios.cs')) {
        if ($required -notin $sources) { throw "Missing focused source: $required" }
    }
    # Program's probe calls must always retain their implementing sources.
    $program = Get-Content (Join-Path $repo 'tests/Respire.Tests/Program.cs') -Raw
    foreach ($match in [regex]::Matches($program, '(\w+Tests)\.RunIsolated\w+Probe')) {
        if (-not ($sources | Where-Object { [IO.Path]::GetFileName($_) -eq "$($match.Groups[1].Value).cs" })) {
            throw "Missing entry-point probe source: $($match.Groups[1].Value)"
        }
    }
    if ('ByteGlobTests.cs' -in $sources) { throw 'Unselected test leaked into the focused build.' }
    if ($sources | Where-Object { $_ -match '^(obj|bin)/' }) { throw 'Build output leaked into Compile.' }
    if ($sources.Count -ge $full.Items.Compile.Count) { throw 'Focused build did not reduce compiler inputs.' }
    foreach ($item in @('ProjectReference', 'PackageReference', 'EmbeddedResource')) {
        if (Compare-Object @($full.Items.$item.Identity) @($focused.Items.$item.Identity)) {
            throw "Focused build changed $item."
        }
    }
    foreach ($property in @('BaseIntermediateOutputPath', 'BaseOutputPath')) {
        if ($full.Properties.$property -eq $focused.Properties.$property) {
            throw "Focused build shares $property with the full suite."
        }
    }
    foreach ($filter in @('NoSuchTest*.cs', 'NoSuchTest.cs')) {
        $errorText = Invoke-Inspection @('-t:SelectFocusedTestSources', "-p:RespireTestFilter=$filter") -ExpectFailure
        if ($errorText -notmatch 'matched no source files') { throw $errorText }
    }
    $errorText = Invoke-Inspection @('-t:SelectFocusedTestSources',
        '-p:RespireTestFilter=TimeSeries*.cs', '-p:ContinuousIntegrationBuild=true') -ExpectFailure
    if ($errorText -notmatch 'CI must compile the full suite') { throw $errorText }
    Write-Host "Focused build contract passed: $($sources.Count) of $($full.Items.Compile.Count) source files."
}
finally {
    Pop-Location
}

# Expected rejection probes leave LASTEXITCODE nonzero. Report the contract's
# success explicitly so GitHub Actions' PowerShell wrapper does not fail the job.
exit 0
