param(
    [Parameter(Mandatory)] [string] $RedisCommandPath,
    [Parameter(Mandatory)] [string] $ValkeyCommandPath,
    [string] $RedisVersion = '8.10.0',
    [string] $ValkeyVersion = '9.1.1',
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\Respire\RespireCommands.g.cs')
)

$ErrorActionPreference = 'Stop'

function Read-CoreCommands([string] $Path, [string] $Provider) {
    Get-ChildItem -LiteralPath $Path -Filter '*.json' | ForEach-Object {
        $json = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
        $property = $json.PSObject.Properties | Select-Object -First 1
        $definition = $property.Value
        $name = if ($definition.container) {
            "$($definition.container) $($property.Name)"
        } else {
            $property.Name
        }

        [pscustomobject]@{
            Name = $name.ToUpperInvariant()
            Group = [string] $definition.group
            Provider = $Provider
        }
    }
}

function Add-Commands(
    [System.Collections.Generic.List[object]] $Commands,
    [string] $Group,
    [string] $Provider,
    [string] $Names) {
    foreach ($name in $Names.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
        $Commands.Add([pscustomobject]@{
            Name = $name.Trim()
            Group = $Group
            Provider = $Provider
        })
    }
}

function Get-ClassName([string] $Group) {
    switch ($Group) {
        'bitmap' { 'Bitmap' }
        'cluster' { 'Cluster' }
        'connection' { 'Connection' }
        'generic' { 'Key' }
        'geo' { 'Geo' }
        'hash' { 'Hash' }
        'hyperloglog' { 'HyperLogLog' }
        'list' { 'List' }
        'pubsub' { 'PubSub' }
        'scripting' { 'Scripting' }
        'sentinel' { 'Sentinel' }
        'server' { 'Server' }
        'set' { 'Set' }
        'sorted_set' { 'SortedSet' }
        'stream' { 'Stream' }
        'string' { 'String' }
        'transactions' { 'Transaction' }
        'array' { 'Array' }
        'json' { 'Json' }
        'search' { 'Search' }
        'timeseries' { 'TimeSeries' }
        'vectorset' { 'VectorSet' }
        'bloom' { 'Bloom' }
        'cuckoo' { 'Cuckoo' }
        'cms' { 'CountMinSketch' }
        'topk' { 'TopK' }
        'tdigest' { 'TDigest' }
        'keydb' { 'KeyDb' }
        'dragonfly' { 'Dragonfly' }
        default { throw "Unknown command group: $Group" }
    }
}

function Get-Identifier([string] $Name) {
    $identifier = $Name -replace '[^A-Z0-9]+', '_'
    if ($identifier[0] -match '[0-9]') {
        return "_$identifier"
    }

    return $identifier
}

$commands = [System.Collections.Generic.List[object]]::new()
$commands.AddRange([object[]] @(Read-CoreCommands $RedisCommandPath 'Redis'))
$commands.AddRange([object[]] @(Read-CoreCommands $ValkeyCommandPath 'Valkey'))

# Commands documented by a pinned core reference but absent from its JSON metadata.
Add-Commands $commands 'connection' 'Valkey' 'CLIENT MAINT_NOTIFICATIONS'

# Redis 8 integrated data structures and processing engines.
Add-Commands $commands 'bloom' 'Redis' 'BF.ADD,BF.CARD,BF.EXISTS,BF.INFO,BF.INSERT,BF.LOADCHUNK,BF.MADD,BF.MEXISTS,BF.RESERVE,BF.SCANDUMP'
Add-Commands $commands 'cuckoo' 'Redis' 'CF.ADD,CF.ADDNX,CF.COUNT,CF.DEL,CF.EXISTS,CF.INFO,CF.INSERT,CF.INSERTNX,CF.LOADCHUNK,CF.MEXISTS,CF.RESERVE,CF.SCANDUMP'
Add-Commands $commands 'cms' 'Redis' 'CMS.INCRBY,CMS.INFO,CMS.INITBYDIM,CMS.INITBYPROB,CMS.MERGE,CMS.QUERY'
Add-Commands $commands 'topk' 'Redis' 'TOPK.ADD,TOPK.COUNT,TOPK.INCRBY,TOPK.INFO,TOPK.LIST,TOPK.QUERY,TOPK.RESERVE'
Add-Commands $commands 'tdigest' 'Redis' 'TDIGEST.ADD,TDIGEST.BYRANK,TDIGEST.BYREVRANK,TDIGEST.CDF,TDIGEST.CREATE,TDIGEST.INFO,TDIGEST.MAX,TDIGEST.MERGE,TDIGEST.MIN,TDIGEST.QUANTILE,TDIGEST.RANK,TDIGEST.RESET,TDIGEST.REVRANK,TDIGEST.TRIMMED_MEAN'
Add-Commands $commands 'json' 'Redis' 'JSON.ARRAPPEND,JSON.ARRINDEX,JSON.ARRINSERT,JSON.ARRLEN,JSON.ARRPOP,JSON.ARRTRIM,JSON.CLEAR,JSON.DEBUG,JSON.DEBUG MEMORY,JSON.DEL,JSON.FORGET,JSON.GET,JSON.MERGE,JSON.MGET,JSON.MSET,JSON.NUMINCRBY,JSON.NUMMULTBY,JSON.OBJKEYS,JSON.OBJLEN,JSON.RESP,JSON.SET,JSON.STRAPPEND,JSON.STRLEN,JSON.TOGGLE,JSON.TYPE'
Add-Commands $commands 'search' 'Redis' 'FT._LIST,FT.AGGREGATE,FT.ALIASADD,FT.ALIASDEL,FT.ALIASLIST,FT.ALIASUPDATE,FT.ALTER,FT.CONFIG GET,FT.CONFIG SET,FT.CREATE,FT.CURSOR DEL,FT.CURSOR READ,FT.DICTADD,FT.DICTDEL,FT.DICTDUMP,FT.DROPINDEX,FT.EXPLAIN,FT.EXPLAINCLI,FT.HYBRID,FT.INFO,FT.PROFILE,FT.SEARCH,FT.SPELLCHECK,FT.SUGADD,FT.SUGDEL,FT.SUGGET,FT.SUGLEN,FT.SYNDUMP,FT.SYNUPDATE,FT.TAGVALS'
Add-Commands $commands 'timeseries' 'Redis' 'TS.ADD,TS.ALTER,TS.CREATE,TS.CREATERULE,TS.DECRBY,TS.DEL,TS.DELETERULE,TS.GET,TS.INCRBY,TS.INFO,TS.MADD,TS.MGET,TS.MRANGE,TS.MREVRANGE,TS.NRANGE,TS.NREVRANGE,TS.QUERYINDEX,TS.QUERYLABELS,TS.RANGE,TS.READ,TS.REVRANGE'
Add-Commands $commands 'vectorset' 'Redis' 'VADD,VCARD,VDIM,VEMB,VGETATTR,VINFO,VISMEMBER,VLINKS,VRANDMEMBER,VRANGE,VREM,VSETATTR,VSIM'

# Optional modules listed by Valkey's command reference.
Add-Commands $commands 'bloom' 'Valkey' 'BF.ADD,BF.CARD,BF.EXISTS,BF.INFO,BF.INSERT,BF.LOAD,BF.MADD,BF.MEXISTS,BF.RESERVE'
Add-Commands $commands 'json' 'Valkey' 'JSON.ARRAPPEND,JSON.ARRINDEX,JSON.ARRINSERT,JSON.ARRLEN,JSON.ARRPOP,JSON.ARRTRIM,JSON.CLEAR,JSON.DEBUG,JSON.DEL,JSON.FORGET,JSON.GET,JSON.MGET,JSON.MSET,JSON.NUMINCRBY,JSON.NUMMULTBY,JSON.OBJKEYS,JSON.OBJLEN,JSON.RESP,JSON.SET,JSON.STRAPPEND,JSON.STRLEN,JSON.TOGGLE,JSON.TYPE'
Add-Commands $commands 'search' 'Valkey' 'FT._LIST,FT.AGGREGATE,FT.CREATE,FT.DROPINDEX,FT.INFO,FT.SEARCH'

# Documented compatible-server extensions.
Add-Commands $commands 'keydb' 'KeyDb' 'EXPIREMEMBER,EXPIREMEMBERAT,KEYDB.CRON,REPLPING'
Add-Commands $commands 'dragonfly' 'Dragonfly' 'STICK'

$merged = $commands |
    Group-Object Name |
    ForEach-Object {
        $providers = @($_.Group.Provider | Sort-Object -Unique)
        [pscustomobject]@{
            Name = $_.Name
            Group = [string] ($_.Group | Select-Object -First 1).Group
            Providers = $providers
        }
    } |
    Sort-Object Group, Name

$builder = [System.Text.StringBuilder]::new()
[void] $builder.AppendLine('// <auto-generated />')
[void] $builder.AppendLine("// Redis $RedisVersion and Valkey $ValkeyVersion command metadata; Redis/Valkey module and compatible-server references.")
[void] $builder.AppendLine('namespace Respire;')
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Pre-encoded descriptors for commands documented by Redis, Valkey, KeyDB, and Dragonfly.</summary>')
[void] $builder.AppendLine('public static class RespireCommands')
[void] $builder.AppendLine('{')

$allReferences = [System.Collections.Generic.List[string]]::new()
foreach ($group in ($merged | Group-Object Group | Sort-Object { Get-ClassName $_.Name })) {
    $className = Get-ClassName $group.Name
    [void] $builder.AppendLine("    public static class $className")
    [void] $builder.AppendLine('    {')
    foreach ($command in ($group.Group | Sort-Object Name)) {
        $identifier = Get-Identifier $command.Name
        $sources = ($command.Providers | ForEach-Object { "RespireCommandSource.$_" }) -join ' | '
        [void] $builder.AppendLine("        /// <summary><c>$($command.Name)</c>.</summary>")
        [void] $builder.AppendLine("        public static readonly RespireCommand $identifier = new(`"$($command.Name)`", $sources);")
        [void] $builder.AppendLine()
        $allReferences.Add("$className.$identifier")
    }
    [void] $builder.AppendLine('    }')
    [void] $builder.AppendLine()
}

[void] $builder.AppendLine('    private static readonly RespireCommand[] s_all =')
[void] $builder.AppendLine('    [')
foreach ($reference in $allReferences) {
    [void] $builder.AppendLine("        $reference,")
}
[void] $builder.AppendLine('    ];')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    /// <summary>Every known descriptor, sorted by group and command name.</summary>')
[void] $builder.AppendLine('    public static ReadOnlySpan<RespireCommand> All => s_all;')
[void] $builder.AppendLine('}')

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
[System.IO.File]::WriteAllText($resolvedOutput, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $($merged.Count) commands at $resolvedOutput"
