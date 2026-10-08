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
    Write-CommandFixture $redisPath 'touch' 'TOUCH' @('READONLY')
    foreach ($name in @('SCAN', 'HSCAN', 'SSCAN', 'ZSCAN', 'ARSCAN')) {
        Write-CommandFixture $redisPath $name $name @('READONLY')
    }

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
        $readEntry = '["' + $entry.Key + '"] = (ReadCommandKind.Read, -1),'
        if ($first.Contains($readEntry) -ne $entry.Value) {
            throw "Incorrect shared read classification for $($entry.Key)."
        }
    }
    if ($first.Contains('["TOUCH"] = (ReadCommandKind.')) { throw 'TOUCH must remain primary-only.' }
    $cacheTable = [regex]::Match($first, '(?s)internal static class CommandCacheMutationMetadata\s*\{(?<body>.*?)\n\}\s*/// <summary>Audited replica-read')
    if (-not $cacheTable.Success) { throw 'Missing independent cache mutation table.' }
    foreach ($forbidden in @('s_all', 'RespireCommands.', 'new RespireCommand')) {
        if ($cacheTable.Groups['body'].Value.Contains($forbidden)) {
            throw "Cache mutation table depends on descriptor initialization: $forbidden."
        }
    }
    $cursorIndices = @{ SCAN = 0; HSCAN = 1; SSCAN = 1; ZSCAN = 1; ARSCAN = -1 }
    foreach ($entry in $cursorIndices.GetEnumerator()) {
        $declaration = '["' + $entry.Key + '"] = (ReadCommandKind.CursorRead, ' + $entry.Value + '),'
        if (-not $first.Contains($declaration)) { throw "Incorrect cursor classification for $($entry.Key)." }
    }
    $mutationExpectations = @{
        GET = 'ReadOnly'; SET = 'Mutation'; 'JSON.GET' = 'ReadOnly'; 'BF.EXISTS' = 'ReadOnly'
        'KEYDB.NHGET' = 'Mutation'; 'CLUSTER SLOT-STATS' = 'ReadOnly'
        'FT.CONFIG GET' = 'ReadOnly'; 'FT.CONFIG SET' = 'Mutation'
    }
    foreach ($entry in $mutationExpectations.GetEnumerator()) {
        $pattern = '(?m)^\s*public static readonly RespireCommand \w+ = new\("' +
            [regex]::Escape($entry.Key) + '", (?<arguments>[^;]+)\);\r?$'
        $declarations = [regex]::Matches($first, $pattern)
        if ($declarations.Count -ne 1) { throw "Expected one descriptor for $($entry.Key)." }
        if (-not $declarations[0].Groups['arguments'].Value.Contains("RespireCacheMutation.$($entry.Value)")) {
            throw "Incorrect cache mutation metadata for $($entry.Key)."
        }
        $independentEntry = 'mutations["' + $entry.Key + '"] = RespireCacheMutation.' + $entry.Value + ';'
        if (-not $cacheTable.Groups['body'].Value.Contains($independentEntry)) {
            throw "Independent cache mutation table differs for $($entry.Key)."
        }
    }
    $frames = [regex]::Matches($first,
        'internal static ReadOnlySpan<byte> (?<option>[A-Z0-9]+) => "\$(?<length>\d+)\\r\\n(?<payload>[A-Z0-9]+)\\r\\n"u8;')
    foreach ($frame in $frames) {
        $option = $frame.Groups['option'].Value
        $payload = $frame.Groups['payload'].Value
        if ($option -cne $payload -or [int] $frame.Groups['length'].Value -ne $payload.Length) {
            throw "Invalid pre-encoded option frame: $option."
        }
    }
    foreach ($requiredOption in @('PX', 'WITHSCORES', 'REV')) {
        if ($requiredOption -notin @($frames | ForEach-Object { $_.Groups['option'].Value })) {
            throw "Missing pre-encoded option frame: $requiredOption."
        }
    }
    Write-Host 'Command catalog generation and fixed-frame checks passed.'
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
