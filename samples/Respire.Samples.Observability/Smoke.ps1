[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) ('Respire-observability-' + [guid]::NewGuid().ToString('N'))),
    [ValidateSet('net8.0', 'net10.0')][string]$Framework = 'net10.0'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $outputPath | Out-Null
$dashboard = Join-Path $outputPath 'dashboard-pinned.json'
$revision = '033fe86e47440da7f365ed2d8dd7f5d6217a575a'
Invoke-WebRequest "https://raw.githubusercontent.com/redis-developer/redis-client-observability/$revision/grafana/dashboards/redis-client-observability.json" -OutFile $dashboard -MaximumRetryCount 3 -RetryIntervalSec 2
if ((Get-FileHash $dashboard -Algorithm SHA256).Hash -ne '533CF0FF7D37F985D3FC4C3BB093AB42C9D63D4C53CF26430C82266832819582') {
    throw 'Pinned Redis dashboard checksum changed.'
}
$container = 'respire-observability-' + [guid]::NewGuid().ToString('N')
$image = 'redis:8.2.1@sha256:5fa2edb1e408fa8235e6db8fab01d1afaaae96c9403ba67b70feceb8661e8621'
$ownedContainer = $false
$promtoolContainer = $container + '-promtool'
$ownedPromtool = $false
try {
    docker run --detach --rm --name $container --memory 128m --cpus 1 --publish 127.0.0.1::6379 $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start smoke Redis.' }
    $ownedContainer = $true
    $binding = docker inspect $container --format '{{(index (index .NetworkSettings.Ports "6379/tcp") 0).HostPort}}'
    if ($LASTEXITCODE -ne 0) { throw 'Could not discover smoke Redis port.' }
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $pong = docker exec $container redis-cli ping 2>$null
        if ($LASTEXITCODE -eq 0 -and $pong -eq 'PONG') { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Smoke Redis did not become ready.' }
    Push-Location $repoRoot
    try {
        & (Join-Path $repoRoot 'scripts/Invoke-AgentDotNet.ps1') -SingleNode -DotNetArguments @(
            'build', (Join-Path $PSScriptRoot 'Respire.Samples.Observability.csproj'), '-c', 'Release', '-f', $Framework)
        if ($LASTEXITCODE -ne 0) { throw "Smoke build failed: $LASTEXITCODE" }
        & (Join-Path $repoRoot 'scripts/Invoke-AgentDotNet.ps1') -SingleNode -DotNetArguments @(
            (Join-Path $PSScriptRoot "bin/Release/$Framework/Respire.Samples.Observability.dll"), $binding.Trim(), $outputPath, $dashboard)
        if ($LASTEXITCODE -ne 0) { throw "Export/dashboard verification failed: $LASTEXITCODE" }
        # Replay corrupted copies, never synthetic measurements in the live exporter.
        $poolLabel = [regex]::Match((Get-Content (Join-Path $outputPath 'busy.prom') -Raw), 'db_client_connection_pool_name="[^"]+/dedicated"').Value
        if (-not $poolLabel) { throw 'Missing dedicated pool label for replay controls.' }
        $controls = @(
            @{ Metric = 'redis_client_csc_items'; Error = 'Unsupported dashboard metric is now exported' },
            @{ Metric = 'redis_client_csc_network_saved_bytes_total'; Error = 'Unsupported dashboard metric is now exported' },
            @{ Metric = 'redis_client_connection_relaxed_timeout'; Error = 'Feature event is now exercised' },
            @{ Metric = 'redis_client_connection_handoff_total'; Error = 'Feature event is now exercised' },
            @{ Metric = 'redis_client_geofailover_failovers_total'; Error = 'Feature event is now exercised' }
        )
        foreach ($control in $controls) {
            $replay = Join-Path $outputPath ('negative-' + $control.Metric)
            New-Item -ItemType Directory -Force $replay | Out-Null
            Copy-Item (Join-Path $outputPath '*.prom') $replay
            Add-Content (Join-Path $replay 'closed.prom') ("`n" + $control.Metric + ' 1')
            $diagnostic = Join-Path $replay 'verification-error.txt'
            & (Join-Path $repoRoot 'scripts/Invoke-AgentDotNet.ps1') -SingleNode -DotNetArguments @(
                (Join-Path $PSScriptRoot "bin/Release/$Framework/Respire.Samples.Observability.dll"), '--verify-only', $replay, $dashboard, $poolLabel)
            if ($LASTEXITCODE -ne 1 -or -not (Test-Path -LiteralPath $diagnostic) -or -not (Select-String -LiteralPath $diagnostic -SimpleMatch $control.Error -Quiet)) {
                throw "Classification negative control failed: $($control.Metric). See $diagnostic"
            }
        }
        Write-Output 'PASS: unsupported and feature-event classifications reject corrupted real-scrape copies.'
    }
    finally { Pop-Location }
    # Copy instead of bind mounting: this also works with remote Docker daemons.
    $prometheusImage = 'prom/prometheus:v3.5.0@sha256:63805ebb8d2b3920190daf1cb14a60871b16fd38bed42b857a3182bc621f4996'
    docker create --name $promtoolContainer --memory 128m --cpus 1 --entrypoint /bin/promtool $prometheusImage test rules /tmp/dashboard-promql-tests.yml | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create promtool container.' }
    $ownedPromtool = $true
    docker cp (Join-Path $outputPath 'dashboard-promql-tests.yml') "${promtoolContainer}:/tmp/dashboard-promql-tests.yml"
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy actual exported series into promtool.' }
    docker start --attach $promtoolContainer
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard PromQL evaluation failed.' }
    $toolExit = docker inspect $promtoolContainer --format '{{.State.ExitCode}}'
    if ($toolExit -ne '0') { throw "promtool failed: $toolExit" }
    Write-Output 'PASS: pinned promtool evaluated supported queries, checked selector controls, and parsed exception queries.'
    Write-Output "Evidence: $outputPath"
}
finally {
    if ($ownedPromtool) {
        docker rm --force $promtoolContainer | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not remove owned container $promtoolContainer" }
    }
    if ($ownedContainer) {
        docker stop $container | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not stop owned container $container" }
    }
}
