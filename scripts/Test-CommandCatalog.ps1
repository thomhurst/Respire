$ErrorActionPreference = 'Stop'

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('respire-catalog-' + [Guid]::NewGuid().ToString('N'))
$redisPath = Join-Path $fixtureRoot 'redis'
$valkeyPath = Join-Path $fixtureRoot 'valkey'
$outputPath = Join-Path $fixtureRoot 'RespireCommands.g.cs'

function Write-CommandFixture([string] $Path, [string] $File, [string] $Name, [object] $Flags,
    [string] $Container = '') {
    $definition = @{ group = 'string'; command_flags = $Flags }
    if ($Container) { $definition.container = $Container }
    @{ $Name = $definition } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Path "$File.json") -Encoding utf8
}

try {
    [void] [System.IO.Directory]::CreateDirectory($redisPath)
    [void] [System.IO.Directory]::CreateDirectory($valkeyPath)
    Write-CommandFixture $redisPath 'get' 'GET' @('READONLY', 'FAST')
    Write-CommandFixture $valkeyPath 'get' 'GET' @('readonly')
    Write-CommandFixture $redisPath 'set' 'SET' @('WRITE')
    Write-CommandFixture $redisPath 'conflict' 'CONFLICT' @('READONLY')
    Write-CommandFixture $valkeyPath 'conflict' 'CONFLICT' @('WRITE')
    Write-CommandFixture $redisPath 'missing' 'MISSING' @('READONLY')
    Write-CommandFixture $valkeyPath 'missing' 'MISSING' $null
    Write-CommandFixture $redisPath 'empty' 'EMPTY' @()
    Write-CommandFixture $redisPath 'contradiction' 'CONTRADICTION' @('READONLY', 'WRITE')
    Write-CommandFixture $redisPath 'duplicate-read' 'DUPLICATE' @('READONLY')
    Write-CommandFixture $redisPath 'duplicate-write' 'DUPLICATE' @('WRITE')
    Write-CommandFixture $redisPath 'invented-ro' 'INVENTED_RO' @('FAST')
    Write-CommandFixture $redisPath 'subcommand' 'GET' @('READONLY') 'CUSTOM'
    # The manual list repeats VCARD for Redis, but adds a second, unaudited provider for JSON.GET.
    Write-CommandFixture $redisPath 'vcard' 'VCARD' @('READONLY')
    Write-CommandFixture $redisPath 'json-get' 'JSON.GET' @('READONLY')
    Write-CommandFixture $redisPath 'slot-stats' 'SLOT-STATS' @('WRITE') 'CLUSTER'

    $generator = Join-Path $PSScriptRoot '../tools/Generate-CommandCatalog.ps1'
    & $generator -RedisCommandPath $redisPath -ValkeyCommandPath $valkeyPath -OutputPath $outputPath
    $first = [System.IO.File]::ReadAllText($outputPath)
    & $generator -RedisCommandPath $redisPath -ValkeyCommandPath $valkeyPath -OutputPath $outputPath
    if ($first -cne [System.IO.File]::ReadAllText($outputPath)) {
        throw 'Catalog generation is not reproducible.'
    }

    $expected = @{
        GET = $true; SET = $false; CONFLICT = $false; MISSING = $false; EMPTY = $false
        CONTRADICTION = $false; DUPLICATE = $false; INVENTED_RO = $false
        'CUSTOM GET' = $true; VCARD = $true; 'JSON.GET' = $false
        'BF.EXISTS' = $false; 'KEYDB.NHGET' = $false
    }
    foreach ($entry in $expected.GetEnumerator()) {
        $pattern = '(?m)^\s*public static readonly RespireCommand \w+ = new\("' +
            [regex]::Escape($entry.Key) + '", (?<arguments>[^;]+)\);\r?$'
        $declarations = [regex]::Matches($first, $pattern)
        if ($declarations.Count -ne 1) { throw "Expected one descriptor for $($entry.Key)." }
        $isReadOnly = $declarations[0].Groups['arguments'].Value.Contains('isReadOnly: true')
        if ($isReadOnly -ne $entry.Value) { throw "Incorrect read-only metadata for $($entry.Key)." }
    }
    $mutationExpectations = @{
        GET = 'ReadOnly'; SET = 'Mutation'; 'JSON.GET' = 'ReadOnly'; 'BF.EXISTS' = 'ReadOnly'
        'KEYDB.NHGET' = 'Mutation'; 'CLUSTER SLOT-STATS' = 'ReadOnly'
    }
    foreach ($entry in $mutationExpectations.GetEnumerator()) {
        $pattern = '(?m)^\s*public static readonly RespireCommand \w+ = new\("' +
            [regex]::Escape($entry.Key) + '", (?<arguments>[^;]+)\);\r?$'
        $declarations = [regex]::Matches($first, $pattern)
        if ($declarations.Count -ne 1) { throw "Expected one descriptor for $($entry.Key)." }
        if (-not $declarations[0].Groups['arguments'].Value.Contains("RespireCacheMutation.$($entry.Value)")) {
            throw "Incorrect cache mutation metadata for $($entry.Key)."
        }
    }
    Write-Host 'Command catalog generation checks passed.'
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('/', '\') +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a fixture directory outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
