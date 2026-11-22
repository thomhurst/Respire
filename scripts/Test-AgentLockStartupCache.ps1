#!/usr/bin/env pwsh
# Regression for PowerShell lock launches when the inherited Unix startup cache is corrupt.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($IsWindows) { Write-Host 'Skipping Unix PowerShell startup cache tests on Windows.'; exit 0 }

$agentLocks = Join-Path $PSScriptRoot 'AgentLocks.ps1'
$workerLauncher = Join-Path $PSScriptRoot 'Invoke-AgentLocks.sh'
. (Join-Path $PSScriptRoot 'Invoke-IsolatedPowerShell.ps1')
$ownerVariable = 'RESPIRE_AGENT_LOCK_OWNER_ID'
$testRoot = [IO.Directory]::CreateTempSubdirectory('agent-lock-cache-test-').FullName
$cache = Join-Path $testRoot 'default cache'
$profile = Join-Path $cache 'powershell/StartupProfileData-NonInteractive'
$cacheLog = Join-Path $testRoot 'launches.log'
$lockName = "agent-lock-cache-test-$([Guid]::NewGuid().ToString('N'))"
$originalEnvironment = @{}
$held = $false

function Set-TestEnvironment([string]$Name, [string]$Value) {
    if (-not $originalEnvironment.ContainsKey($Name)) {
        $originalEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name)
    }
    [Environment]::SetEnvironmentVariable($Name, $Value)
}

function Assert-Result($Result, [int]$Code, [string]$Output) {
    if ($Result.ExitCode -ne $Code -or $Result.Output -notmatch $Output) {
        throw "Expected exit $Code and output matching '$Output', got $($Result.ExitCode): $($Result.Output)"
    }
}

function Invoke-Worker([string[]]$Arguments) {
    $output = @(& sh $workerLauncher @Arguments 2>&1)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine).Trim() }
}

try {
    # Model the crash deterministically. The shim otherwise execs real PowerShell.
    $bin = New-Item -ItemType Directory -Path (Join-Path $testRoot 'bin')
    New-Item -ItemType Directory -Path (Split-Path $profile) -Force | Out-Null
    Set-Content -LiteralPath $profile -Value 'corrupt startup profile'
    @'
#!/bin/sh
if [ -f "$XDG_CACHE_HOME/powershell/StartupProfileData-NonInteractive" ]; then
    echo 'PowerShell startup cache is corrupt' >&2
    exit 134
fi
if [ ! -d "$XDG_CACHE_HOME" ]; then
    echo 'No isolated startup cache directory' >&2
    exit 99
fi
echo "$XDG_CACHE_HOME" >> "$RESPIRE_TEST_LOCK_CACHE_LOG"
touch "$XDG_CACHE_HOME/generated" || exit 99
if [ "$RESPIRE_TEST_LOCK_CACHE_MODE" = timeout ]; then
    exec sleep 30
fi
exec "$RESPIRE_TEST_REAL_PWSH" "$@"
'@ | Set-Content -LiteralPath (Join-Path $bin.FullName 'pwsh')
    chmod +x (Join-Path $bin.FullName 'pwsh')
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the startup shim executable.' }

    Set-TestEnvironment 'RESPIRE_TEST_REAL_PWSH' (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    Set-TestEnvironment 'RESPIRE_TEST_LOCK_CACHE_LOG' $cacheLog
    Set-TestEnvironment 'RESPIRE_TEST_LOCK_CACHE_MODE' ''
    Set-TestEnvironment 'XDG_CACHE_HOME' $cache
    $childTemp = New-Item -ItemType Directory -Path (Join-Path $testRoot 'temp with spaces')
    Set-TestEnvironment 'TMPDIR' $childTemp.FullName
    Set-TestEnvironment $ownerVariable $lockName
    Set-TestEnvironment 'PATH' "$($bin.FullName):$env:PATH"

    $control = @(& pwsh -NoProfile -File $agentLocks status -LockName $lockName 2>&1)
    Assert-Result @{ ExitCode = $LASTEXITCODE; Output = ($control -join [Environment]::NewLine).Trim() } 134 '^PowerShell startup cache is corrupt$'

    # Load the driver's actual lock entry point without running its sweep or queue.
    $driver = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Start-IssuePrLoop.ps1'), [ref]$null, [ref]$null)
    $invokeLocks = $driver.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Locks'
    }, $false)
    . ([scriptblock]::Create($invokeLocks.Extent.Text))

    Assert-Result (Invoke-Locks @('status', '-LockName', $lockName)) 0 '^FREE$'
    $acquired = Invoke-Locks @('acquire', '-LockName', $lockName)
    if ($acquired.ExitCode -ne 0) { throw "Driver acquire failed with exit $($acquired.ExitCode): $($acquired.Output)" }
    $held = $true
    Assert-Result (Invoke-Worker @('status', '-LockName', $lockName)) 0 '^HELD-BY-ME$'
    Assert-Result (Invoke-Worker @('acquire', '-LockName', $lockName, '-OwnerId', "$lockName other")) 3 '^HELD$'
    Assert-Result (Invoke-Worker @('release', '-LockName', $lockName)) 0 '^$'
    $held = $false
    Assert-Result (Invoke-Locks @('status', '-LockName', $lockName)) 0 '^FREE$'

    # Simulate Docker losing the running Redis instance after a successful inspect.
    @'
#!/bin/sh
if [ "$1" = inspect ]; then
    echo 'true redis-server'
    exit 0
fi
echo 'could not connect to Redis' >&2
exit 1
'@ | Set-Content -LiteralPath (Join-Path $bin.FullName 'docker')
    chmod +x (Join-Path $bin.FullName 'docker')
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the Redis failure shim executable.' }
    try {
        Assert-Result (Invoke-Locks @('status', '-LockName', $lockName)) 1 'could not connect to Redis'
        Assert-Result (Invoke-Worker @('status', '-LockName', $lockName)) 1 'could not connect to Redis'
    }
    finally { Remove-Item -LiteralPath (Join-Path $bin.FullName 'docker') }

    Set-TestEnvironment 'RESPIRE_TEST_LOCK_CACHE_MODE' 'timeout'
    try {
        Assert-Result (Invoke-Locks @('ping') -TimeoutSeconds 1) 124 '^timed out after 1 s$'
    }
    finally { Set-TestEnvironment 'RESPIRE_TEST_LOCK_CACHE_MODE' '' }

    $caches = @(Get-Content -LiteralPath $cacheLog)
    if ($caches.Count -ne 9 -or @($caches | Sort-Object -Unique).Count -ne $caches.Count) {
        throw "Expected 9 separate cache directories, got $($caches.Count) launches."
    }
    foreach ($usedCache in $caches) {
        if (Test-Path -LiteralPath $usedCache) { throw "Child startup cache was not removed: $usedCache" }
    }
    if ($env:XDG_CACHE_HOME -cne $cache -or (Get-Content -LiteralPath $profile -Raw).Trim() -cne 'corrupt startup profile') {
        throw 'The inherited startup cache was changed.'
    }
    Write-Host 'OK AgentLocks Unix startup caches are isolated and removed.'
}
finally {
    try {
        if ($held) {
            # Clean up even when the launcher itself is the failing assertion.
            $cleanupCache = New-Item -ItemType Directory -Path (Join-Path $testRoot 'cleanup-cache')
            Set-TestEnvironment 'XDG_CACHE_HOME' $cleanupCache.FullName
            & $env:RESPIRE_TEST_REAL_PWSH -NoProfile -File $agentLocks release -LockName $lockName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not release the test lock.' }
        }
    }
    finally {
        foreach ($name in $originalEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name])
        }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
