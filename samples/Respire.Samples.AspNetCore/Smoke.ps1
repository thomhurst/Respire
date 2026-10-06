param([string]$BaseUrl = 'http://localhost:5084')
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$entry = [guid]::NewGuid().ToString('N')
$hybridUrl = "$BaseUrl/hybrid?entry=$entry"
$outputUrl = "$BaseUrl/output?entry=$entry"

if ((Invoke-WebRequest "$BaseUrl/health" -TimeoutSec 15).Content.Trim() -ne 'Healthy') {
    throw 'Redis health check did not report Healthy.'
}
foreach ($endpoint in 'redis', 'distributed') {
    $value = [guid]::NewGuid().ToString()
    Invoke-WebRequest "$BaseUrl/$endpoint" -TimeoutSec 15 -Method Post -ContentType 'application/json' `
        -Body (@{ value = $value } | ConvertTo-Json) | Out-Null
    if ((Invoke-RestMethod "$BaseUrl/$endpoint" -TimeoutSec 15).value -ne $value) {
        throw "$endpoint did not round-trip its value."
    }
    Write-Output "${endpoint}: round-trip passed"
}
$hybrid = Invoke-RestMethod $hybridUrl -TimeoutSec 15
if (-not $hybrid.version -or -not $hybrid.generatedAt) { throw 'Missing HybridCache response fields.' }
if ((Invoke-RestMethod $hybridUrl -TimeoutSec 15).version -ne $hybrid.version) {
    throw 'HybridCache did not reuse the cached value.'
}
# The configured L1 TTL is five seconds; the Redis L2 TTL is one minute.
Start-Sleep -Seconds 6
if ((Invoke-RestMethod $hybridUrl -TimeoutSec 15).version -ne $hybrid.version) {
    throw 'HybridCache did not retain its value after the L1 expiry.'
}
$output = Invoke-RestMethod $outputUrl -TimeoutSec 15
if (-not $output.version -or -not $output.generatedAt) { throw 'Missing output-cache response fields.' }
if ((Invoke-RestMethod $outputUrl -TimeoutSec 15).version -ne $output.version) {
    throw 'Output caching did not reuse the response.'
}
Write-Output 'PASS: health, direct Redis, distributed cache, HybridCache L1/L2, output cache.'
