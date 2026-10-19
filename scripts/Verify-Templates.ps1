<#
Installs the packed template into a private hive, builds a renamed standalone app,
and checks readiness and cache-aside across app restarts against an owned Redis.
Use -UseAgentGuard for all local agent invocations.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagesPath,
    [Parameter(Mandatory)][string]$Version,
    [switch]$UseAgentGuard
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$packages = (Resolve-Path -LiteralPath $PackagesPath).Path
$package = Join-Path $packages "Respire.Templates.$Version.nupkg"
if (-not (Test-Path -LiteralPath $package)) { throw "Missing $package" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    if (@($archive.Entries | Where-Object FullName -Match '^(lib|ref|contentFiles)/|/(bin|obj)/').Count -ne 0) {
        throw 'Template package contains build outputs or consumer contentFiles.'
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Respire.Templates.nuspec').Open())
    try { [xml]$nuspec = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    if ($nuspec.package.metadata.packageTypes.packageType.name -ne 'Template') { throw 'Package is not marked as a Template.' }
    if ($nuspec.SelectNodes('//*[local-name()="dependency"]').Count -ne 0) { throw 'Template package has runtime dependencies.' }
}
finally { $archive.Dispose() }
# Deliberately outside the checkout: no repository props, central versions or NuGet
# configuration may make an incomplete template appear to work.
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('Respire-template-' + [guid]::NewGuid().ToString('N'))
$evidence = Join-Path $repositoryRoot ('artifacts/template-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch, $evidence | Out-Null
# Pin only the toolchain, not build defaults. Otherwise a workstation's .NET 11
# preview could hide a template that does not build with the required .NET 10 SDK.
$sdk = (Get-Content (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk
@{ sdk = $sdk } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'global.json')
$hive = Join-Path $scratch 'hive'
$generated = Join-Path $scratch 'Renamed.Api'
$guard = Join-Path $PSScriptRoot 'Invoke-AgentDotNet.ps1'
$container = 'respire-template-' + [guid]::NewGuid().ToString('N')
$ownsContainer = $false
$appProcess = $null
$run = 0

function Invoke-DotNet([string[]]$CommandArguments) {
    if ($UseAgentGuard) { & $guard -SingleNode -DotNetArguments $CommandArguments }
    else { & dotnet @CommandArguments }
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($CommandArguments[0]) failed: $LASTEXITCODE" }
}

function Stop-App {
    if ($null -eq $script:appProcess) { return }
    if (-not $script:appProcess.HasExited) { $script:appProcess.Kill($true) }
    $script:appProcess.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $evidence "app-$script:run.stdout.log"), $script:stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $evidence "app-$script:run.stderr.log"), $script:stderr.GetAwaiter().GetResult())
    $script:appProcess.Dispose()
    $script:appProcess = $null
}

function Start-App {
    $script:run++
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $arguments = @((Join-Path $generated 'bin/Release/net10.0/Renamed.Api.dll'), '--urls', $url)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.WorkingDirectory = $generated
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ConnectionStrings__Redis'] = "redis://127.0.0.1:$script:redisPort"
    # Do not inherit a workstation's collector configuration into this test.
    $start.Environment['OTEL_EXPORTER_OTLP_ENDPOINT'] = ''
    if ($UseAgentGuard) {
        $launch = Join-Path $scratch 'launch.json'
        @{ Guard = $guard; Arguments = $arguments } | ConvertTo-Json | Set-Content -LiteralPath $launch
        $runner = Join-Path $scratch 'launch.ps1'
        [IO.File]::WriteAllText($runner, @'
param([string]$LaunchFile)
$launch = Get-Content -LiteralPath $LaunchFile -Raw | ConvertFrom-Json
& $launch.Guard -DotNetArguments $launch.Arguments
exit $LASTEXITCODE
'@)
        $start.FileName = (Get-Command pwsh).Source
        foreach ($argument in @('-NoProfile', '-File', $runner, $launch)) { $start.ArgumentList.Add($argument) }
    }
    else {
        $start.FileName = (Get-Command dotnet).Source
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    }
    $script:appProcess = [Diagnostics.Process]::Start($start)
    $script:stdout = $script:appProcess.StandardOutput.ReadToEndAsync()
    $script:stderr = $script:appProcess.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:appProcess.HasExited) { throw "Generated app exited before readiness. Logs: $evidence" }
        try {
            if ((Invoke-WebRequest "$url/health/ready" -TimeoutSec 2).Content.Trim() -eq 'Healthy') { return $url }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "Generated app did not become healthy. Logs: $evidence"
}

try {
    Push-Location $scratch
    try {
        # Install the artifact, not a source directory. Private hive leaves user templates untouched.
        Invoke-DotNet @('new', 'install', $package, '--debug:custom-hive', $hive)
        Invoke-DotNet @('new', 'respire-api', '-n', 'Renamed.Api', '-o', $generated, '--debug:custom-hive', $hive)
        $project = Join-Path $generated 'Renamed.Api.csproj'
        [xml]$xml = Get-Content -LiteralPath $project -Raw
        $references = $xml.SelectNodes('/Project/ItemGroup/PackageReference[starts-with(@Include, "Respire")]')
        if ($references.Count -ne 2) { throw 'Expected both Respire package references in the generated API.' }
        foreach ($reference in $references) {
            if ($reference.Version -cne $Version) { throw "Unstamped package reference: $($reference.OuterXml)" }
        }
        if (Test-Path (Join-Path $generated '.template.config')) { throw 'Template engine metadata leaked into generated app.' }
        Invoke-DotNet @('build', $project, '-c', 'Release', '-warnaserror',
            "-p:RestoreAdditionalProjectSources=$packages", "-p:RestorePackagesPath=$scratch/packages")

        docker run --detach --rm --name $container --memory 128m --cpus 1 --publish 127.0.0.1::6379 redis:7-alpine | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not start template Redis.' }
        $ownsContainer = $true
        $script:redisPort = (docker inspect $container --format '{{(index (index .NetworkSettings.Ports "6379/tcp") 0).HostPort}}').Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not discover Redis port.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $pong = docker exec $container redis-cli ping 2>$null
            if ($LASTEXITCODE -eq 0 -and $pong -eq 'PONG') { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Redis did not become ready.' }
            Start-Sleep -Milliseconds 100
        } while ($true)

        $url = Start-App
        $first = Invoke-RestMethod "$url/values/smoke" -TimeoutSec 10
        $hit = Invoke-RestMethod "$url/values/smoke" -TimeoutSec 10
        if ($first.key -ne 'smoke' -or -not $first.value -or $hit.value -ne $first.value) { throw 'Cache-aside hit failed.' }
        $ttl = docker exec $container redis-cli ttl Renamed.Api:value:smoke
        if ($LASTEXITCODE -ne 0 -or [int]$ttl -le 0 -or [int]$ttl -gt 300) { throw 'Cached Redis key has no bounded expiry.' }
        Stop-App
        $url = Start-App
        $persisted = Invoke-RestMethod "$url/values/smoke" -TimeoutSec 10
        if ($persisted.value -ne $first.value) { throw 'Cached value did not survive an app restart.' }
        $other = Invoke-RestMethod "$url/values/other" -TimeoutSec 10
        if (-not $other.value -or $other.value -eq $first.value) { throw 'Distinct cache keys reused the same value.' }
        Write-Output 'PASS: package install, rename, standalone build, readiness, cache hit, Redis expiry and app restart.'
    }
    finally { Pop-Location }
}
finally {
    Stop-App
    if ($ownsContainer) {
        docker stop $container | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not stop owned container $container" }
    }
    # Only the unique directory created by this invocation is eligible for deletion.
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (-not $resolvedScratch.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside temporary directory: $resolvedScratch"
    }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    Write-Output "Template test logs: $evidence"
}
