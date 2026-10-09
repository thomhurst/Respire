param([Parameter(Mandatory)][string]$BaseUrl)
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$key = [guid]::NewGuid().ToString('N')

if ((Invoke-WebRequest "$BaseUrl/health" -TimeoutSec 15).Content.Trim() -ne 'Healthy') {
    throw 'The Redis and Valkey health checks did not report Healthy.'
}
foreach ($endpoint in 'ping', 'valkey') {
    $result = Invoke-RestMethod "$BaseUrl/$endpoint" -TimeoutSec 15
    if (-not $result.server -or $null -eq $result.latency) {
        throw "$endpoint did not return a successful server PING."
    }
}
$distributed = Invoke-RestMethod "$BaseUrl/distributed/$key-distributed" -TimeoutSec 15
if (-not $distributed.value -or $distributed.stored -ne $distributed.value) {
    throw 'The distributed cache did not round-trip its value.'
}
$hybrid = (Invoke-WebRequest "$BaseUrl/hybrid/$key" -TimeoutSec 15).Content
foreach ($endpoint in "hybrid/$key", "hybrid/$key/distributed") {
    if ((Invoke-WebRequest "$BaseUrl/$endpoint" -TimeoutSec 15).Content -ne $hybrid) {
        throw 'HybridCache did not reuse its value from both L1 and Redis L2.'
    }
}
$outputUrl = "$BaseUrl/output?smoke=$key"
$output = (Invoke-WebRequest $outputUrl -TimeoutSec 15).Content
if ((Invoke-WebRequest $outputUrl -TimeoutSec 15).Content -ne $output) {
    throw 'Output caching did not reuse its response.'
}
Write-Output 'PASS: Redis, Valkey, health, distributed cache, HybridCache L1/L2, output cache.'
