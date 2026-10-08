param(
    [Parameter(Mandatory)] [string] $RedisCommandPath,
    [Parameter(Mandatory)] [string] $ValkeyCommandPath,
    [string] $RedisVersion = '8.10.0',
    [string] $ValkeyVersion = '9.1.1',
    [string] $OutputPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($PSScriptRoot, '..', 'src', 'Respire', 'RespireCommands.g.cs'))
)

$ErrorActionPreference = 'Stop'

# Redis command flags describe execution properties, not always keyspace effects. Preserve these audited
# no-keyspace-change commands whose provider metadata is absent or says only WRITE/ADMIN.
$cacheReadOnlyOverrides = @(
    'ACL CAT', 'ACL DRYRUN', 'ACL GETUSER', 'ACL LIST', 'ACL LOG', 'ACL WHOAMI',
    'BF.CARD', 'BF.DEBUG', 'BF.EXISTS', 'BF.INFO', 'BF.MEXISTS', 'BF.SCANDUMP',
    'CACHING', 'CF.COUNT', 'CF.DEBUG', 'CF.EXISTS', 'CF.INFO', 'CF.MEXISTS', 'CF.SCANDUMP',
    'CLIENT CACHING', 'CLIENT LIST', 'CLIENT TRACKING',
    'CLUSTER COUNTKEYSINSLOT', 'CLUSTER INFO', 'CLUSTER KEYSLOT', 'CLUSTER LINKS', 'CLUSTER SLOT-STATS',
    'CLUSTER MYID', 'CLUSTER MYSHARDID', 'CLUSTER NODES', 'CLUSTER SHARDS',
    'CMS.INFO', 'CMS.QUERY', 'COMMAND COUNT', 'COMMAND DOCS', 'COMMAND GETKEYS', 'COMMAND INFO', 'COMMAND LIST',
    'COMMANDLOG GET', 'COMMANDLOG LEN', 'CONFIG GET', 'ECHO',
    'FT.AGGREGATE', 'FT.ALIASLIST', 'FT.CONFIG GET', 'FT.CURSOR', 'FT.CURSOR DEL', 'FT.CURSOR GC', 'FT.CURSOR READ',
    'FT.DICTDUMP', 'FT.EXPLAIN', 'FT.EXPLAINCLI', 'FT.INFO', 'FT.PROFILE', 'FT.SEARCH', 'FT.SPELLCHECK',
    'FT.SUGGET', 'FT.SUGLEN', 'FT.SYNDUMP', 'FT.TAGVALS',
    'FUNCTION DELETE', 'FUNCTION DUMP', 'FUNCTION FLUSH', 'FUNCTION LIST', 'FUNCTION LOAD', 'FUNCTION RESTORE', 'FUNCTION STATS',
    'HOTKEYS GET', 'HOTKEYS RESET', 'HOTKEYS START', 'HOTKEYS STOP', 'INFO',
    'JSON.ARRINDEX', 'JSON.ARRLEN', 'JSON.DEBUG', 'JSON.DEBUG FIELDS', 'JSON.DEBUG HELP', 'JSON.DEBUG MEMORY',
    'JSON.GET', 'JSON.MGET', 'JSON.OBJKEYS', 'JSON.OBJLEN', 'JSON.RESP', 'JSON.STRLEN', 'JSON.TYPE',
    'LASTSAVE', 'LATENCY DOCTOR', 'LATENCY HISTOGRAM', 'LATENCY HISTORY', 'LATENCY LATEST',
    'MEMORY DOCTOR', 'MEMORY PURGE', 'MEMORY STATS', 'MODULE LIST', 'PING',
    'PUBSUB', 'PUBSUB CHANNELS', 'PUBSUB NUMPAT', 'PUBSUB NUMSUB', 'PUBSUB SHARDCHANNELS', 'PUBSUB SHARDNUMSUB',
    'ROLE', 'SCRIPT EXISTS', 'SCRIPT FLUSH', 'SCRIPT LOAD', 'SLOWLOG GET', 'SLOWLOG LEN', 'TDIGEST.BYRANK',
    'TDIGEST.BYREVRANK', 'TDIGEST.CDF', 'TDIGEST.INFO', 'TDIGEST.MAX', 'TDIGEST.MIN', 'TDIGEST.QUANTILE',
    'TDIGEST.RANK', 'TDIGEST.REVRANK', 'TDIGEST.TRIMMED_MEAN', 'TIME', 'TOPK.COUNT', 'TOPK.INFO', 'TOPK.LIST', 'TOPK.QUERY',
    'TS.GET', 'TS.INFO', 'TS.MGET', 'TS.MRANGE', 'TS.MREVRANGE', 'TS.NRANGE', 'TS.NREVRANGE', 'TS.QUERYINDEX',
    'TS.QUERYLABELS', 'TS.RANGE', 'TS.READ', 'TS.REVRANGE', 'TIMESERIES.REFRESHCLUSTER', 'TRACKING',
    'VCARD', 'VDIM', 'VEMB', 'VGETATTR', 'VINFO', 'VISMEMBER', 'VLINKS', 'VRANDMEMBER', 'VRANGE', 'VSIM'
)
$cacheMutationOverrides = @('DELEX', 'DELIFEQ')
$cacheUnknownOverrides = @('PFCOUNT')

# Replica routing is stricter than cache invalidation. TOUCH is READONLY in provider
# flags, but its purpose is updating access metadata on the primary. ARSCAN uses index
# ranges rather than SCAN cursors. Preserve its conservative CursorRead classification
# with index -1; the standard cursor-continuation layout does not apply to it.
$primaryOnlyReadOverrides = @('TOUCH')
$cursorArgumentIndices = @{ SCAN = 0; HSCAN = 1; SSCAN = 1; ZSCAN = 1; ARSCAN = -1 }

# Retry risk is a separate audit. Cache ReadOnly means no tracked keyspace mutation,
# which does not make scripts, consuming cursors, or connection state replayable.
# Unlisted writes and module operations remain Never. Do not infer permission from names.
$retryOverrides = @{}
foreach ($entry in @(
    @{ Category = 'Always'; Commands = 'PING,ECHO' },
    @{ Category = 'Connection'; Commands = 'CONFIG GET,CLIENT GETNAME,CLIENT GETREDIR,CLIENT ID,CLIENT INFO,CLIENT LIST,CLIENT SETNAME,CLIENT SETINFO,INFO,TIME,ROLE,LASTSAVE,COMMAND,COMMAND COUNT,COMMAND DOCS,COMMAND GETKEYS,COMMAND GETKEYSANDFLAGS,COMMAND INFO,COMMAND LIST' },
    @{ Category = 'WriteChecked'; Commands = 'SETNX,MSETNX,HSETNX' },
    @{ Category = 'WriteLastWins'; Commands = 'MSET,HSET,HMSET,LSET,SETRANGE,SETBIT,VSETATTR' },
    # SET GET and ZADD INCR can lose a result or accumulate effects. A future
    # execution policy may refine plain SET or non-INCR ZADD after inspecting options.
    # Relative expiry, destructive reads and set membership changes can affect
    # intervening data or return different counts even when their final values match.
    @{ Category = 'WriteAccumulating'; Commands = 'SET,SETEX,PSETEX,APPEND,INCR,INCRBY,INCRBYFLOAT,DECR,DECRBY,HINCRBY,HINCRBYFLOAT,HDEL,HGETDEL,HGETEX,HSETEX,GETDEL,GETEX,GETSET,DEL,UNLINK,DELEX,DELIFEQ,EXPIRE,PEXPIRE,EXPIREAT,PEXPIREAT,PERSIST,RENAME,RENAMENX,COPY,MOVE,RESTORE,MIGRATE,LPUSH,LPUSHX,RPUSH,RPUSHX,LPOP,RPOP,BLPOP,BRPOP,LMPOP,BLMPOP,LMOVE,BLMOVE,RPOPLPUSH,BRPOPLPUSH,LREM,LTRIM,LINSERT,SADD,SREM,SMOVE,SPOP,SDIFFSTORE,SINTERSTORE,SUNIONSTORE,ZADD,ZINCRBY,ZREM,ZPOPMAX,ZPOPMIN,BZPOPMAX,BZPOPMIN,ZMPOP,BZMPOP,ZREMRANGEBYLEX,ZREMRANGEBYRANK,ZREMRANGEBYSCORE,ZDIFFSTORE,ZINTERSTORE,ZUNIONSTORE,ZRANGESTORE,GEOADD,GEORADIUS,GEORADIUSBYMEMBER,GEOSEARCHSTORE,BITFIELD,BITOP,PFADD,PFCOUNT,PFMERGE,XADD,XDEL,XACK,XACKDEL,XDELEX,XTRIM,XREADGROUP,XCLAIM,XAUTOCLAIM,XGROUP CREATE,XGROUP CREATECONSUMER,XGROUP DELCONSUMER,XGROUP DESTROY,XGROUP SETID,XSETID,PUBLISH,SPUBLISH,JSON.ARRAPPEND,JSON.ARRINSERT,JSON.ARRPOP,JSON.NUMINCRBY,JSON.NUMMULTBY,JSON.NUMPOWBY,JSON.STRAPPEND,JSON.TOGGLE,BF.ADD,BF.MADD,BF.INSERT,CF.ADD,CF.ADDNX,CF.INSERT,CF.INSERTNX,CF.DEL,CMS.INCRBY,TOPK.ADD,TOPK.INCRBY,TDIGEST.ADD,TS.ADD,TS.MADD,TS.INCRBY,TS.DECRBY,FT.SUGADD' },
    @{ Category = 'ServerAdmin'; Commands = 'CONFIG SET,CONFIG RESETSTAT,CONFIG REWRITE,FLUSHDB,FLUSHALL,ACL SETUSER,ACL DELUSER,ACL LOAD,ACL SAVE,FUNCTION DELETE,FUNCTION FLUSH,FUNCTION LOAD,FUNCTION RESTORE,SCRIPT FLUSH,SCRIPT LOAD,SCRIPT KILL,FUNCTION KILL,MODULE LOAD,MODULE LOADEX,MODULE UNLOAD,SAVE,BGSAVE,BGREWRITEAOF,SLOWLOG RESET,LATENCY RESET,MEMORY PURGE,REPLICAOF,SLAVEOF,FAILOVER,SWAPDB,CLIENT KILL,CLIENT PAUSE,CLIENT UNPAUSE,FT.CONFIG SET' },
    # These exclusions take precedence over READONLY provider flags. Script code
    # and cursor/transaction/connection lifecycle require execution-specific proof.
    @{ Category = 'Never'; Commands = 'EVAL,EVALSHA,EVAL_RO,EVALSHA_RO,FCALL,FCALL_RO,FT.CURSOR READ,FT.CURSOR DEL,FT.AGGREGATE,FT.PROFILE,FT.HYBRID,AUTH,HELLO,SELECT,ASKING,READONLY,READWRITE,CLIENT CACHING,CLIENT TRACKING,CLIENT TRACKINGINFO,CLIENT REPLY,CLIENT CAPA,CLIENT MAINT_NOTIFICATIONS,CLIENT IMPORT-SOURCE,CLIENT NO-EVICT,CLIENT NO-TOUCH,MULTI,EXEC,DISCARD,WATCH,UNWATCH,SUBSCRIBE,PSUBSCRIBE,SSUBSCRIBE,UNSUBSCRIBE,PUNSUBSCRIBE,SUNSUBSCRIBE,MONITOR,QUIT,RESET,SYNC,PSYNC,REPLCONF,WAIT,WAITAOF,SHUTDOWN,TOUCH,DEBUG,SORT' }
)) {
    foreach ($name in $entry.Commands.Split(',')) {
        if ($retryOverrides.ContainsKey($name)) { throw "Duplicate retry audit: $name" }
        $retryOverrides[$name] = $entry.Category
    }
}

# Pure module reads have an explicit semantic audit because the pinned manual
# module references do not supply provider flags. Never override a WRITE flag.
$retryReadOnlyOverrides = @(
    'BF.CARD', 'BF.EXISTS', 'BF.INFO', 'BF.MEXISTS', 'BF.SCANDUMP',
    'CF.COUNT', 'CF.EXISTS', 'CF.INFO', 'CF.MEXISTS', 'CF.SCANDUMP',
    'CMS.INFO', 'CMS.QUERY', 'TOPK.COUNT', 'TOPK.INFO', 'TOPK.LIST', 'TOPK.QUERY',
    'TDIGEST.BYRANK', 'TDIGEST.BYREVRANK', 'TDIGEST.CDF', 'TDIGEST.INFO', 'TDIGEST.MAX',
    'TDIGEST.MIN', 'TDIGEST.QUANTILE', 'TDIGEST.RANK', 'TDIGEST.REVRANK', 'TDIGEST.TRIMMED_MEAN',
    'JSON.ARRINDEX', 'JSON.ARRLEN', 'JSON.DEBUG MEMORY', 'JSON.GET', 'JSON.MGET',
    'JSON.OBJKEYS', 'JSON.OBJLEN', 'JSON.RESP', 'JSON.STRLEN', 'JSON.TYPE',
    'FT._LIST', 'FT.ALIASLIST', 'FT.CONFIG GET', 'FT.DICTDUMP', 'FT.EXPLAIN', 'FT.EXPLAINCLI',
    'FT.INFO', 'FT.SEARCH', 'FT.SPELLCHECK', 'FT.SUGGET', 'FT.SUGLEN', 'FT.SYNDUMP', 'FT.TAGVALS',
    'TS.GET', 'TS.INFO', 'TS.MGET', 'TS.MRANGE', 'TS.MREVRANGE', 'TS.NRANGE', 'TS.NREVRANGE',
    'TS.QUERYINDEX', 'TS.QUERYLABELS', 'TS.RANGE', 'TS.READ', 'TS.REVRANGE',
    'VCARD', 'VDIM', 'VEMB', 'VGETATTR', 'VINFO', 'VISMEMBER', 'VLINKS', 'VRANDMEMBER', 'VRANGE', 'VSIM'
)

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
            IsReadOnly = $definition.command_flags -contains 'READONLY' -and
                $definition.command_flags -notcontains 'WRITE'
            HasWriteFlag = $definition.command_flags -contains 'WRITE'
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
            IsReadOnly = $null
            HasWriteFlag = $false
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
Add-Commands $commands 'json' 'Redis' 'JSON.ARRAPPEND,JSON.ARRINDEX,JSON.ARRINSERT,JSON.ARRLEN,JSON.ARRPOP,JSON.ARRTRIM,JSON.CLEAR,JSON.DEBUG,JSON.DEBUG MEMORY,JSON.DEL,JSON.FORGET,JSON.GET,JSON.MERGE,JSON.MGET,JSON.MSET,JSON.NUMINCRBY,JSON.NUMMULTBY,JSON.NUMPOWBY,JSON.OBJKEYS,JSON.OBJLEN,JSON.RESP,JSON.SET,JSON.STRAPPEND,JSON.STRLEN,JSON.TOGGLE,JSON.TYPE'
Add-Commands $commands 'search' 'Redis' 'FT._LIST,FT.AGGREGATE,FT.ALIASADD,FT.ALIASDEL,FT.ALIASLIST,FT.ALIASUPDATE,FT.ALTER,FT.CONFIG GET,FT.CONFIG SET,FT.CREATE,FT.CURSOR DEL,FT.CURSOR READ,FT.DICTADD,FT.DICTDEL,FT.DICTDUMP,FT.DROPINDEX,FT.EXPLAIN,FT.EXPLAINCLI,FT.HYBRID,FT.INFO,FT.PROFILE,FT.SEARCH,FT.SPELLCHECK,FT.SUGADD,FT.SUGDEL,FT.SUGGET,FT.SUGLEN,FT.SYNDUMP,FT.SYNUPDATE,FT.TAGVALS'
Add-Commands $commands 'timeseries' 'Redis' 'TS.ADD,TS.ALTER,TS.CREATE,TS.CREATERULE,TS.DECRBY,TS.DEL,TS.DELETERULE,TS.GET,TS.INCRBY,TS.INFO,TS.MADD,TS.MGET,TS.MRANGE,TS.MREVRANGE,TS.NRANGE,TS.NREVRANGE,TS.QUERYINDEX,TS.QUERYLABELS,TS.RANGE,TS.READ,TS.REVRANGE'
Add-Commands $commands 'vectorset' 'Redis' 'VADD,VCARD,VDIM,VEMB,VGETATTR,VINFO,VISMEMBER,VLINKS,VRANDMEMBER,VRANGE,VREM,VSETATTR,VSIM'

# Optional modules listed by Valkey's command reference.
Add-Commands $commands 'bloom' 'Valkey' 'BF.ADD,BF.CARD,BF.EXISTS,BF.INFO,BF.INSERT,BF.LOAD,BF.MADD,BF.MEXISTS,BF.RESERVE'
Add-Commands $commands 'json' 'Valkey' 'JSON.ARRAPPEND,JSON.ARRINDEX,JSON.ARRINSERT,JSON.ARRLEN,JSON.ARRPOP,JSON.ARRTRIM,JSON.CLEAR,JSON.DEBUG,JSON.DEL,JSON.FORGET,JSON.GET,JSON.MGET,JSON.MSET,JSON.NUMINCRBY,JSON.NUMMULTBY,JSON.OBJKEYS,JSON.OBJLEN,JSON.RESP,JSON.SET,JSON.STRAPPEND,JSON.STRLEN,JSON.TOGGLE,JSON.TYPE'
Add-Commands $commands 'search' 'Valkey' 'FT._LIST,FT.AGGREGATE,FT.CREATE,FT.DROPINDEX,FT.INFO,FT.SEARCH'

# Compatible-server extensions audited against KeyDB v6.3.4's command table and
# dragonflydb/documentation commit 31881bce033d4cec47cb2e85865d46745760e499.
# DFLYCLUSTER CONFIG is documented in dragonflydb/dragonfly v2.0.0 docs/cluster-mode.md.
# Replication-only commands (e.g. RREPLAY, KEYDB.MVCCRESTORE, DFLYMIGRATE) are not client APIs.
Add-Commands $commands 'keydb' 'KeyDb' 'EXPIREMEMBER,EXPIREMEMBERAT,PEXPIREMEMBERAT,KEYDB.CRON,KEYDB.HRENAME,KEYDB.MEXISTS,KEYDB.NHGET,KEYDB.NHSET,REPLPING'
Add-Commands $commands 'dragonfly' 'Dragonfly' 'STICK,CL.THROTTLE,SADDEX,FIELDEXPIRE,FIELDTTL,RM,SCRIPT LATENCY,SCRIPT LIST,DFLYCLUSTER CONFIG,DFLYCLUSTER FLUSHSLOTS,DFLYCLUSTER GETSLOTINFO,DFLYCLUSTER SLOT-MIGRATION-STATUS,MEMORY ARENA,MEMORY DECOMMIT,MEMORY DEFRAGMENT,CF.COMPACT,JSON.DEBUG FIELDS,JSON.DEBUG HELP'

$merged = $commands |
    Group-Object Name |
    ForEach-Object {
        $providers = @($_.Group.Provider | Sort-Object -Unique)
        # Manual reference entries have no flag audit. A provider needs authoritative
        # metadata, and every authoritative entry for that provider must agree.
        $isReadOnly = $true
        foreach ($provider in ($_.Group | Group-Object Provider)) {
            $metadata = @($provider.Group | Where-Object { $null -ne $_.IsReadOnly })
            if ($metadata.Count -eq 0 -or ($metadata | Where-Object { -not $_.IsReadOnly })) {
                $isReadOnly = $false
                break
            }
        }
        [pscustomobject]@{
            Name = $_.Name
            Group = [string] ($_.Group | Select-Object -First 1).Group
            Providers = $providers
            IsReadOnly = $isReadOnly
            RetryCategory = if ($retryOverrides.ContainsKey($_.Name)) { $retryOverrides[$_.Name] }
                elseif ($isReadOnly) { 'ReadOnly' }
                elseif ($retryReadOnlyOverrides -contains $_.Name -and
                    -not ($_.Group | Where-Object HasWriteFlag)) { 'ReadOnly' }
                else { 'Never' }
            CacheMutation = if ($cacheUnknownOverrides -contains $_.Name) { 'Unknown' }
                elseif ($isReadOnly -or $cacheReadOnlyOverrides -contains $_.Name) { 'ReadOnly' }
                else { 'Mutation' }
        }
    } |
    Sort-Object Group, Name

$builder = [System.Text.StringBuilder]::new()
[void] $builder.AppendLine('// <auto-generated />')
[void] $builder.AppendLine("// Redis $RedisVersion and Valkey $ValkeyVersion command metadata; Redis/Valkey module and compatible-server references.")
[void] $builder.AppendLine('using System.Collections.Frozen;')
[void] $builder.AppendLine('namespace Respire;')
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Pre-encoded descriptors for commands documented by Redis, Valkey, KeyDB, and Dragonfly.</summary>')
[void] $builder.AppendLine('public static class RespireCommands')
[void] $builder.AppendLine('{')

$allReferences = [System.Collections.Generic.List[string]]::new()
foreach ($group in ($merged | Group-Object Group | Sort-Object { Get-ClassName $_.Name })) {
    $className = Get-ClassName $group.Name
    [void] $builder.AppendLine("    /// <summary>Pre-encoded $className command descriptors.</summary>")
    [void] $builder.AppendLine("    public static class $className")
    [void] $builder.AppendLine('    {')
    foreach ($command in ($group.Group | Sort-Object Name)) {
        $identifier = Get-Identifier $command.Name
        $sources = ($command.Providers | ForEach-Object { "RespireCommandSource.$_" }) -join ' | '
        $readOnlyArgument = if ($command.IsReadOnly) { ', isReadOnly: true' } else { '' }
        [void] $builder.AppendLine("        /// <summary><c>$($command.Name)</c>.</summary>")
        if ($command.Name.Contains(' ')) {
            [void] $builder.AppendLine("        [RespireCommandCatalogName(`"$($command.Name)`")]")
        }
        [void] $builder.AppendLine("        public static readonly RespireCommand $identifier = new(`"$($command.Name)`", $sources, RespireCacheMutation.$($command.CacheMutation)$readOnlyArgument, retryCategory: RespireCommandRetryCategory.$($command.RetryCategory));")
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
[void] $builder.AppendLine('    internal static RespireCacheMutation GetCacheMutation(string operation)')
[void] $builder.AppendLine('        => CommandCacheMutationMetadata.Get(operation);')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    /// <summary>Every known descriptor, sorted by group and command name.</summary>')
[void] $builder.AppendLine('    public static ReadOnlySpan<RespireCommand> All => s_all;')
[void] $builder.AppendLine('}')
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Cache effects independent of descriptor and verb initialization.</summary>')
[void] $builder.AppendLine('internal static class CommandCacheMutationMetadata')
[void] $builder.AppendLine('{')
[void] $builder.AppendLine('    private static readonly FrozenDictionary<string, RespireCacheMutation> s_cacheMutations = CreateCacheMutations();')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    private static FrozenDictionary<string, RespireCacheMutation> CreateCacheMutations()')
[void] $builder.AppendLine('    {')
[void] $builder.AppendLine('        var mutations = new Dictionary<string, RespireCacheMutation>(StringComparer.Ordinal);')
foreach ($command in ($merged | Sort-Object Name)) {
    [void] $builder.AppendLine(('        mutations["{0}"] = RespireCacheMutation.{1};' -f $command.Name, $command.CacheMutation))
}
[void] $builder.AppendLine('        foreach (var operation in new string[]')
[void] $builder.AppendLine('        {')
foreach ($operation in $cacheReadOnlyOverrides) {
    [void] $builder.AppendLine("            `"$operation`",")
}
[void] $builder.AppendLine('        }) mutations[operation] = RespireCacheMutation.ReadOnly;')
[void] $builder.AppendLine('        foreach (var operation in new string[]')
[void] $builder.AppendLine('        {')
foreach ($operation in $cacheMutationOverrides) {
    [void] $builder.AppendLine("            `"$operation`",")
}
[void] $builder.AppendLine('        }) mutations[operation] = RespireCacheMutation.Mutation;')
[void] $builder.AppendLine('        foreach (var operation in new string[]')
[void] $builder.AppendLine('        {')
foreach ($operation in $cacheUnknownOverrides) {
    [void] $builder.AppendLine("            `"$operation`",")
}
[void] $builder.AppendLine('        }) mutations[operation] = RespireCacheMutation.Unknown;')
[void] $builder.AppendLine('        return mutations.ToFrozenDictionary(StringComparer.Ordinal);')
[void] $builder.AppendLine('    }')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    internal static RespireCacheMutation Get(string operation)')
[void] $builder.AppendLine('        => s_cacheMutations.TryGetValue(operation, out var mutation) ? mutation : RespireCacheMutation.Unknown;')
[void] $builder.AppendLine('}')

# Independent of RespireCommands and Verbs initialization: both consume this table.
# Classification is cached in descriptors; healthy typed dispatch never performs a lookup.
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Audited replica-read eligibility shared by typed verbs and raw commands.</summary>')
[void] $builder.AppendLine('internal static class CommandReadMetadata')
[void] $builder.AppendLine('{')
[void] $builder.AppendLine('    // No descriptor references: initializing this table cannot recursively initialize the catalog.')
[void] $builder.AppendLine('    private static readonly FrozenDictionary<string, (ReadCommandKind Kind, int CursorArgumentIndex)> s_commands =')
[void] $builder.AppendLine('        new Dictionary<string, (ReadCommandKind, int)>(StringComparer.OrdinalIgnoreCase)')
[void] $builder.AppendLine('        {')
foreach ($command in ($merged | Sort-Object Name)) {
    if (-not $command.IsReadOnly -or $primaryOnlyReadOverrides -contains $command.Name) { continue }
    $isCursor = $cursorArgumentIndices.ContainsKey($command.Name)
    $kind = if ($isCursor) { 'CursorRead' } else { 'Read' }
    $cursorIndex = if ($isCursor) { $cursorArgumentIndices[$command.Name] } else { -1 }
    [void] $builder.AppendLine(('            ["{0}"] = (ReadCommandKind.{1}, {2}),' -f $command.Name, $kind, $cursorIndex))
}
[void] $builder.AppendLine('        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    internal static (ReadCommandKind Kind, int CursorArgumentIndex) Get(string command)')
[void] $builder.AppendLine('        => s_commands.TryGetValue(command, out var metadata) ? metadata : (ReadCommandKind.None, -1);')
[void] $builder.AppendLine('}')

# Retry metadata has no descriptor references or per-dispatch lookup. Default values
# and unknown names fail closed; Never is not part of the ordered permission range.
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Audited retry risk cached when typed verbs and descriptors initialize.</summary>')
[void] $builder.AppendLine('internal static class CommandRetryCategoryMetadata')
[void] $builder.AppendLine('{')
[void] $builder.AppendLine('    private static readonly FrozenDictionary<string, RespireCommandRetryCategory> s_commands =')
[void] $builder.AppendLine('        new Dictionary<string, RespireCommandRetryCategory>(StringComparer.OrdinalIgnoreCase)')
[void] $builder.AppendLine('        {')
foreach ($command in ($merged | Sort-Object Name)) {
    [void] $builder.AppendLine(('            ["{0}"] = RespireCommandRetryCategory.{1},' -f $command.Name, $command.RetryCategory))
}
[void] $builder.AppendLine('        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);')
[void] $builder.AppendLine()
[void] $builder.AppendLine('    internal static RespireCommandRetryCategory Get(string command)')
[void] $builder.AppendLine('        => s_commands.TryGetValue(command, out var category) ? category : RespireCommandRetryCategory.Never;')
[void] $builder.AppendLine('}')

# Fixed options are complete bulk frames. Keep their logical argument identity separate
# from framing so generic command/cache/routing inspection sees ordinary text.
[void] $builder.AppendLine()
[void] $builder.AppendLine('/// <summary>Immutable pre-encoded bulk frames for fixed command options.</summary>')
[void] $builder.AppendLine('internal static class CommandOptionFrames')
[void] $builder.AppendLine('{')
$fixedOptions = @(
    'ANY', 'ASC', 'BLOCK', 'BYBOX', 'BYRADIUS', 'CH', 'CLAIM', 'COUNT', 'DESC',
    'FP32', 'FROMLONLAT', 'FROMMEMBER', 'GET', 'GROUP', 'GT', 'INCR', 'KEEPTTL',
    'LT', 'MAXCOUNT', 'MAXSIZE', 'NOACK', 'NX', 'PERSIST', 'PX', 'PXAT',
    'REV', 'STREAMS', 'VALUES', 'WITHSCORES', 'XREAD', 'XREADGROUP', 'XX'
)
foreach ($option in $fixedOptions) {
    [void] $builder.AppendLine(('    internal static ReadOnlySpan<byte> {0} => "${1}\r\n{0}\r\n"u8;' -f $option, $option.Length))
}
[void] $builder.AppendLine()
foreach ($option in @('PX', 'PXAT', 'REV', 'WITHSCORES')) {
    [void] $builder.AppendLine(('    internal static readonly RespireValue {0}Value = RespireValue.PreEncodedOption("{0}", {0}.ToArray());' -f $option))
}
[void] $builder.AppendLine('}')

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
[System.IO.File]::WriteAllText($resolvedOutput, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $($merged.Count) commands at $resolvedOutput"
