<#
Installs the packed template into a private hive, builds a renamed standalone app,
and checks cache-aside, job processing and dashboard authorization against an owned Redis.
Also builds the Aspire starter and runs its AppHost, API and Redis through Aspire testing.
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
$appName = 'Renamed.Api'
$operatorPassword = [guid]::NewGuid().ToString('N')
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

function Test-AspireTemplate {
    $aspireApp = Join-Path $scratch 'Renamed.Aspire-Store'
    Invoke-DotNet @('new', 'respire-aspire', '-n', 'Renamed.Aspire-Store', '-o', $aspireApp, '--debug:custom-hive', $hive)
    $projects = @(Get-ChildItem -LiteralPath $aspireApp -Recurse -Filter '*.csproj')
    if ($projects.Count -ne 2) { throw 'Expected an AppHost and API in the Aspire starter.' }
    $respireReferences = @()
    foreach ($project in $projects) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
        $respireReferences += @($xml.SelectNodes('/Project/ItemGroup/PackageReference[starts-with(@Include, "Respire")]'))
        foreach ($reference in $xml.SelectNodes('/Project/ItemGroup/ProjectReference')) {
            if (-not (Test-Path -LiteralPath (Join-Path $project.DirectoryName $reference.Include))) {
                throw "Broken generated project reference: $($reference.Include)"
            }
        }
    }
    if ($respireReferences.Count -ne 1 -or $respireReferences[0].Include -ne 'Respire.Aspire' -or $respireReferences[0].Version -cne $Version) {
        throw 'Expected Respire.Aspire stamped with the packed version.'
    }
    foreach ($file in Get-ChildItem -LiteralPath $aspireApp -Recurse -File -Force) {
        if ($file.FullName -match '[\\/]\.template\.config[\\/]' -or
            $file.Name.Contains('Respire.Aspire-Starter') -or
            (Get-Content -LiteralPath $file.FullName -Raw).Contains('Respire.Aspire-Starter')) {
            throw "Unreplaced template name or leaked metadata: $($file.FullName)"
        }
    }
    $appHost = Join-Path $aspireApp 'Renamed.Aspire-Store.AppHost/Renamed.Aspire-Store.AppHost.csproj'
    [xml]$hostProject = Get-Content -LiteralPath $appHost -Raw
    $aspireVersion = $hostProject.SelectSingleNode('/Project/Sdk[@Name="Aspire.AppHost.Sdk"]').Version
    foreach ($reference in $hostProject.SelectNodes('/Project/ItemGroup/PackageReference[starts-with(@Include, "Aspire.Hosting.")]')) {
        if ($reference.Version -cne $aspireVersion) { throw 'Aspire hosting SDK and package versions differ.' }
    }
    $restore = @("-p:RestoreAdditionalProjectSources=$packages", "-p:RestorePackagesPath=$scratch/packages")
    Invoke-DotNet (@('build', (Join-Path $aspireApp 'Renamed.Aspire-Store.slnx'), '-c', 'Release', '-warnaserror') + $restore)

    # A fixture outside the checkout tests the generated AppHost, not the repository sample.
    # Aspire testing assigns ports and disposes only the resources it starts, including Redis.
    $smoke = Join-Path $scratch 'AspireSmoke'
    New-Item -ItemType Directory -Path $smoke | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'template-smoke/Program.cs') -Destination $smoke
    $smokeProject = Join-Path $smoke 'AspireSmoke.csproj'
    (Get-Content (Join-Path $PSScriptRoot 'template-smoke/AspireSmoke.csproj.template') -Raw).
        Replace('__ASPIRE_VERSION__', $aspireVersion) | Set-Content -LiteralPath $smokeProject
    Invoke-DotNet (@('build', $smokeProject, '-c', 'Release', '-warnaserror') + $restore)
    # DCP launches the already-built API; --no-build keeps restores out of the runtime check.
    Invoke-DotNet @('run', '--project', $smokeProject, '-c', 'Release', '--no-build') *> (Join-Path $evidence 'aspire-smoke.log')
    Write-Output 'PASS: Aspire template install, rename, version stamping, standalone solution build and AppHost smoke.'
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

function Start-App([string]$Environment = 'Development') {
    $script:run++
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $arguments = @((Join-Path $generated "bin/Release/net10.0/$appName.dll"), '--urls', $url)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.WorkingDirectory = $generated
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ConnectionStrings__Redis'] = "redis://127.0.0.1:$script:redisPort"
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = $Environment
    $start.Environment['DOTNET_ENVIRONMENT'] = $Environment
    $start.Environment['Dashboard__Password'] = $operatorPassword
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
    $lastReadinessError = 'No request exception was observed.'
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($script:appProcess.HasExited) { throw "Generated app exited before readiness. Logs: $evidence" }
        try {
            if ((Invoke-WebRequest "$url/health/ready" -TimeoutSec 2).Content.Trim() -eq 'Healthy') { return $url }
        }
        catch { $lastReadinessError = $_.Exception.Message }
        Start-Sleep -Milliseconds 100
    }
    throw "Generated app did not become healthy. Last readiness error: $lastReadinessError Logs: $evidence"
}

function New-TemplateApp([string]$Template, [string]$Name, [int]$ReferenceCount) {
    $destination = Join-Path $scratch $Name
    Invoke-DotNet @('new', $Template, '-n', $Name, '-o', $destination, '--debug:custom-hive', $hive)
    $project = Join-Path $destination "$Name.csproj"
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $references = $xml.SelectNodes('/Project/ItemGroup/PackageReference[starts-with(@Include, "Respire")]')
    if ($references.Count -ne $ReferenceCount) { throw "Expected $ReferenceCount Respire references in $Template." }
    foreach ($reference in $references) {
        if ($reference.Version -cne $Version) { throw "Unstamped package reference: $($reference.OuterXml)" }
    }
    if (Test-Path (Join-Path $destination '.template.config')) { throw 'Template engine metadata leaked into generated app.' }
    $ignore = Get-Content -LiteralPath (Join-Path $destination '.gitignore')
    foreach ($pattern in @('bin/', 'obj/', '.vs/', '*.user', '.env')) {
        if ($pattern -cnotin $ignore) { throw "Generated $Template is missing ignore rule $pattern." }
    }
    Invoke-DotNet @('build', $project, '-c', 'Release', '-warnaserror',
        "-p:RestoreAdditionalProjectSources=$packages", "-p:RestorePackagesPath=$scratch/packages")
}

function Assert-HttpStatus([string]$Uri, [int]$Expected, [hashtable]$Headers = @{}, [string]$Method = 'GET', [string]$Body = '') {
    $request = @{ Uri = $Uri; Headers = $Headers; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 10 }
    if ($Body) { $request.Body = $Body; $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ([int]$response.StatusCode -ne $Expected) {
        throw "Expected HTTP $Expected from $Method $Uri, got $($response.StatusCode)."
    }
}

try {
    Push-Location $scratch
    try {
        # Install the artifact, not a source directory. Private hive leaves user templates untouched.
        Invoke-DotNet @('new', 'install', $package, '--debug:custom-hive', $hive)
        New-TemplateApp 'respire-api' 'Renamed.Api' 2
        New-TemplateApp 'respire-worker' 'Renamed.Worker' 4

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
        Stop-App

        $appName = 'Renamed.Worker'
        $generated = Join-Path $scratch $appName
        $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("operator:$operatorPassword")) }
        $url = Start-App
        Assert-HttpStatus "$url/workers/" 401
        Assert-HttpStatus "$url/workers/0/retry" 401 @{} 'POST'
        Assert-HttpStatus "$url/jobs/smoke" 401 @{} 'POST' '{"message":"unauthorized"}'
        Assert-HttpStatus "$url/jobs/smoke" 401
        Assert-HttpStatus "$url/workers/" 401 @{ Authorization = 'Basic b3BlcmF0b3I6d3Jvbmc=' }
        Assert-HttpStatus "$url/workers/" 401 @{ Authorization = 'Basic invalid!' }
        $dashboard = Invoke-WebRequest "$url/workers/" -Headers $headers -TimeoutSec 10
        if ($dashboard.Content -notmatch 'Sample jobs') {
            throw 'Authorized dashboard did not render the configured job queue.'
        }
        # Antiforgery may combine no-store with no-cache in a single header value.
        $cacheControl = $dashboard.Headers['Cache-Control'] -join ','
        if ($cacheControl -notmatch '(^|,)\s*no-store\s*(,|$)') {
            throw "Dashboard must disable storage. Cache-Control was: $cacheControl"
        }
        # Dashboard mutations still require antiforgery, even for an authenticated operator.
        Assert-HttpStatus "$url/workers/0/retry" 400 $headers 'POST'
        foreach ($invalidId in @('%20', ('x' * 129))) {
            Assert-HttpStatus "$url/jobs/$invalidId" 400 $headers
            Assert-HttpStatus "$url/jobs/$invalidId" 400 $headers 'POST' '{"message":"invalid ID"}'
        }
        foreach ($validId in @('x', ('x' * 128))) {
            Assert-HttpStatus "$url/jobs/$validId" 404 $headers
            Assert-HttpStatus "$url/jobs/$validId" 202 $headers 'POST' '{"message":"valid ID"}'
        }
        $submitted = Invoke-RestMethod "$url/jobs/smoke" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"message":"hello worker"}' -TimeoutSec 10
        $duplicate = Invoke-RestMethod "$url/jobs/smoke" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"message":"ignored duplicate"}' -TimeoutSec 10
        if (-not $submitted.enqueued -or $duplicate.enqueued -or $submitted.entryId -ne $duplicate.entryId) {
            throw 'Job enqueue/deduplication failed.'
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $result = Invoke-WebRequest "$url/jobs/smoke" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 5
            $pending = @(docker exec $container redis-cli --raw XPENDING '{respire-job:Renamed.Worker}:ready' respire-jobs)
            if ($LASTEXITCODE -ne 0) { throw 'Could not inspect worker acknowledgements.' }
            if ($result.StatusCode -eq 200 -and ($result.Content | ConvertFrom-Json).result -eq 'HELLO WORKER' -and
                $pending.Count -gt 0 -and $pending[0] -eq '0') { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Worker did not process and acknowledge the job.' }
            Start-Sleep -Milliseconds 100
        } while ($true)
        Stop-App
        $url = Start-App
        $persisted = Invoke-RestMethod "$url/jobs/smoke" -Headers $headers -TimeoutSec 10
        if ($persisted.result -ne 'HELLO WORKER') { throw 'Job result did not survive worker restart.' }
        Stop-App
        # Valid credentials cannot enable the starter's HTTP exception outside Development.
        $url = Start-App 'Production'
        Assert-HttpStatus "$url/workers/" 403 $headers
        $spoofedHeaders = $headers.Clone()
        $spoofedHeaders['X-Forwarded-Proto'] = 'https'
        Assert-HttpStatus "$url/workers/" 403 $spoofedHeaders
        $insecure = Invoke-WebRequest "$url/workers/" -SkipHttpErrorCheck -TimeoutSec 10
        if ($insecure.StatusCode -ne 403 -or $insecure.Headers.ContainsKey('WWW-Authenticate')) {
            throw 'Production HTTP must reject access without prompting for plaintext credentials.'
        }
        Write-Output 'PASS: worker enqueue, deduplication, processing, acknowledgement, restart, dashboard authentication and antiforgery.'
        Stop-App
        Test-AspireTemplate
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
