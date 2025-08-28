# PowerShell script to run Respire benchmarks locally

param(
    [Parameter()]
    [ValidateSet("Container", "Throughput", "All")]
    [string]$BenchmarkType = "All",
    
    [Parameter()]
    [ValidateSet("net8.0", "net9.0", "Both")]
    [string]$Framework = "Both",
    
    [Parameter()]
    [string]$Filter = "*",
    
    [Parameter()]
    [switch]$OpenResults
)

Write-Host "Respire Benchmark Runner" -ForegroundColor Cyan
Write-Host "=====================" -ForegroundColor Cyan

# Build the solution first
Write-Host "`nBuilding solution in Release mode..." -ForegroundColor Yellow
dotnet build -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed! Please fix build errors before running benchmarks." -ForegroundColor Red
    exit 1
}

# Navigate to benchmark project
Push-Location "benchmarks/Respire.Benchmarks"

try {
    $frameworks = @()
    if ($Framework -eq "Both") {
        $frameworks = @("net8.0", "net9.0")
    } else {
        $frameworks = @($Framework)
    }
    
    foreach ($fw in $frameworks) {
        Write-Host "`nRunning benchmarks for $fw..." -ForegroundColor Green
        
        $benchmarkFilter = ""
        switch ($BenchmarkType) {
            "Container" { $benchmarkFilter = "*RedisContainerBenchmarks*" }
            "Throughput" { $benchmarkFilter = "*RedisThroughputBenchmarks*" }
            "All" { $benchmarkFilter = "*" }
        }
        
        # Apply custom filter if provided
        if ($Filter -ne "*") {
            $benchmarkFilter = $Filter
        }
        
        Write-Host "Filter: $benchmarkFilter" -ForegroundColor Gray
        
        # Run benchmarks
        dotnet run -c Release -f $fw -- `
            --filter "$benchmarkFilter" `
            --exporters json markdown html `
            --artifacts "./BenchmarkDotNet.Artifacts/$fw"
        
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Benchmark run failed for $fw!" -ForegroundColor Red
        } else {
            Write-Host "Benchmarks completed successfully for $fw" -ForegroundColor Green
            
            $resultsPath = "./BenchmarkDotNet.Artifacts/$fw/results"
            if (Test-Path $resultsPath) {
                Write-Host "Results saved to: $resultsPath" -ForegroundColor Cyan
                
                # Display summary
                $htmlFile = Get-ChildItem -Path $resultsPath -Filter "*.html" | Select-Object -First 1
                if ($htmlFile -and $OpenResults) {
                    Write-Host "Opening results in browser..." -ForegroundColor Yellow
                    Start-Process $htmlFile.FullName
                }
            }
        }
    }
    
    Write-Host "`n==============================" -ForegroundColor Cyan
    Write-Host "All benchmarks completed!" -ForegroundColor Green
    Write-Host "==============================" -ForegroundColor Cyan
    
} finally {
    Pop-Location
}

Write-Host @"

Benchmark Tips:
- For quick validation: .\run-benchmarks.ps1 -BenchmarkType Container -Framework net9.0
- For full comparison: .\run-benchmarks.ps1 -BenchmarkType All -Framework Both
- To open results automatically: .\run-benchmarks.ps1 -OpenResults
- Custom filter: .\run-benchmarks.ps1 -Filter "*Set*"

Results are saved in: benchmarks\Respire.Benchmarks\BenchmarkDotNet.Artifacts\
"@ -ForegroundColor Gray