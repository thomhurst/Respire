using System.Collections.Frozen;
using Respire.Internal;

namespace Respire.Commands;

/// <summary>Explicit key layouts shared by immediate and deferred raw execution.</summary>
internal static class RawCommandKeyLayouts
{
    internal enum MutationKind : byte
    {
        Unknown, ReadOnly, LayoutKeys, FirstArgument, SecondArgument, IndirectKeys,
    }
    internal readonly record struct KeyRouting(bool Known, int Index)
    {
        internal const int NoKeyIndex = -1;
        internal static KeyRouting NoKeys => new(true, NoKeyIndex);
    }

    internal readonly record struct KeyLayout(int Start, int Count, int Stride = 1, int Extra = -1);

    private enum LayoutKind
    {
        None, First, FirstTwo, AfterFirst, All, Triples, Pairs, BitOp, CountedAfterName, Counted, CountedWithDestination,
        AllExceptLast, CountedPairs, CountedAfterTimeout, StreamRead, StreamGroupRead, Migrate,
    }
    // Prefixable marks layouts that name every key position, so key-prefixed views may rewrite them.
    private readonly record struct Definition(LayoutKind Kind, bool Deferred, MutationKind Mutation, bool Prefixable = false);

    private static readonly FrozenDictionary<string, Definition> Layouts = CreateLayouts();
    // Test-only enumeration keeps COMMAND GETKEYS coverage aligned with the full deferred allowlist.
    internal static IEnumerable<string> DeferredOperations => Layouts.Where(pair => pair.Value.Deferred).Select(pair => pair.Key);
    internal static IEnumerable<(string Operation, MutationKind Mutation)> MutationClassifications
        => Layouts.Select(pair => (pair.Key, pair.Value.Mutation));

    internal static MutationKind GetMutationKind(string operation)
        => Layouts.TryGetValue(operation, out var definition) ? definition.Mutation : MutationKind.Unknown;

    private static Definition CreateDefinition(string operation, LayoutKind kind, bool deferred, bool prefixable = false)
    {
        // Provider metadata owns read/write classification; this table owns which arguments are written.
        var mutation = RespireCommands.GetCacheMutation(operation) switch
        {
            RespireCacheMutation.ReadOnly => MutationKind.ReadOnly,
            RespireCacheMutation.Mutation or RespireCacheMutation.SingleKey or RespireCacheMutation.MultiKey
                when kind is not (LayoutKind.Migrate or LayoutKind.StreamGroupRead) => MutationKind.LayoutKeys,
            _ => MutationKind.Unknown,
        };
        return new(kind, deferred, mutation, prefixable);
    }

    private static FrozenDictionary<string, Definition> CreateLayouts()
    {
        var layouts = new Dictionary<string, Definition>(StringComparer.Ordinal);
        Add(LayoutKind.None,
            "PING", "ECHO", "TIME");
        Add(LayoutKind.First,
            "GET", "SET", "GETSET", "SETNX", "SETEX", "PSETEX", "GETDEL",
            "GETEX", "APPEND", "STRLEN", "GETRANGE", "SETRANGE", "INCR", "INCRBY", "DELEX", "DELIFEQ",
            "INCRBYFLOAT", "INCREX", "DECR", "DECRBY", "TYPE", "TTL", "PTTL", "EXPIRE",
            "PEXPIRE", "EXPIREAT", "PEXPIREAT", "EXPIRETIME", "PEXPIRETIME", "PERSIST", "DUMP",
            "RESTORE", "HGET", "HSET", "HSETNX", "HMGET", "HMSET", "HGETALL",
            "HDEL", "HEXISTS", "HLEN", "HKEYS", "HVALS", "HSTRLEN", "HINCRBY",
            "HINCRBYFLOAT", "HRANDFIELD", "LPUSH", "RPUSH", "LPUSHX", "RPUSHX", "LPOP",
            "RPOP", "LLEN", "LRANGE", "LINDEX", "LSET", "LINSERT", "LREM",
            "LTRIM", "LPOS", "SADD", "SREM", "SCARD", "SMEMBERS", "SISMEMBER",
            "SMISMEMBER", "SPOP", "SRANDMEMBER", "ZADD", "ZREM", "ZCARD", "ZSCORE",
            "ZMSCORE", "ZINCRBY", "ZCOUNT", "ZLEXCOUNT", "ZRANGE", "ZREVRANGE", "ZRANGEBYSCORE",
            "ZREVRANGEBYSCORE", "ZRANGEBYLEX", "ZREVRANGEBYLEX", "ZRANK", "ZREVRANK", "ZREMRANGEBYRANK", "ZREMRANGEBYSCORE",
            "ZREMRANGEBYLEX", "ZPOPMIN", "ZPOPMAX", "ZRANDMEMBER", "GETBIT", "SETBIT", "BITCOUNT",
            "BITPOS", "BITFIELD", "BITFIELD_RO", "PFADD", "GEOADD", "GEODIST", "GEOHASH",
            "GEOPOS", "GEOSEARCH", "XADD", "XACK", "XDEL", "XTRIM", "XLEN",
            "XRANGE", "XREVRANGE", "XPENDING", "XCLAIM", "XAUTOCLAIM", "OBJECT ENCODING", "OBJECT FREQ",
            "OBJECT IDLETIME", "OBJECT REFCOUNT", "MEMORY USAGE", "XINFO STREAM", "XINFO GROUPS", "XINFO CONSUMERS", "XGROUP CREATE",
            "XGROUP SETID", "XGROUP DESTROY", "XGROUP CREATECONSUMER", "XGROUP DELCONSUMER", "XSETID");
        Add(LayoutKind.FirstTwo,
            "RENAME", "RENAMENX", "LCS", "SMOVE", "LMOVE", "LMOVEM", "RPOPLPUSH");
        AddMutation(LayoutKind.FirstTwo, MutationKind.SecondArgument, ["COPY"]);
        AddMutation(LayoutKind.FirstTwo, MutationKind.FirstArgument, ["ZRANGESTORE", "GEOSEARCHSTORE"]);
        Add(LayoutKind.All,
            "DEL", "UNLINK", "EXISTS", "TOUCH", "MGET", "SDIFF", "SINTER",
            "SUNION");
        // PFCOUNT can rewrite the HLL representation. Keep the conservative full-cache fence.
        AddMutation(LayoutKind.All, MutationKind.Unknown, ["PFCOUNT"]);
        AddMutation(LayoutKind.All, MutationKind.FirstArgument, ["SDIFFSTORE", "SINTERSTORE", "SUNIONSTORE", "PFMERGE"]);
        Add(LayoutKind.Pairs,
            "MSET", "MSETNX");
        AddMutation(LayoutKind.BitOp, MutationKind.SecondArgument, ["BITOP"]);
        Add(LayoutKind.CountedAfterName,
            "EVAL", "EVALSHA", "EVAL_RO", "EVALSHA_RO", "FCALL", "FCALL_RO");
        Add(LayoutKind.Counted,
            "LMPOP", "ZMPOP", "SINTERCARD", "ZDIFF", "ZINTER", "ZUNION", "ZINTERCARD");
        AddMutation(LayoutKind.CountedWithDestination, MutationKind.FirstArgument,
            ["ZDIFFSTORE", "ZINTERSTORE", "ZUNIONSTORE"]);
        // Immediate-only additions do not expand the conservative deferred allowlist.
        AddImmediate(LayoutKind.All, "KEYDB.MEXISTS");
        AddPrefixable(LayoutKind.First,
            "JSON.GET", "JSON.SET", "JSON.DEL", "JSON.FORGET", "JSON.CLEAR", "JSON.ARRAPPEND", "JSON.ARRINDEX",
            "JSON.ARRLEN", "JSON.MERGE", "JSON.NUMPOWBY", "JSON.DEBUG MEMORY", "JSON.DEBUG FIELDS",
            "JSON.ARRINSERT", "JSON.ARRPOP", "JSON.ARRTRIM", "JSON.NUMINCRBY", "JSON.NUMMULTBY", "JSON.OBJKEYS",
            "JSON.OBJLEN", "JSON.STRAPPEND", "JSON.STRLEN", "JSON.TOGGLE", "JSON.TYPE", "JSON.RESP");
        // AfterFirst assumes one subcommand token before the key (JSON.DEBUG MEMORY key, JSON.DEBUG FIELDS key).
        AddPrefixable(LayoutKind.AfterFirst, "JSON.DEBUG");
        AddPrefixable(LayoutKind.None, "JSON.DEBUG HELP");
        // LMOVEM/BLMOVEM are Redis 8.10 commands, with source and destination in the first two positions.
        AddImmediate(LayoutKind.FirstTwo, "BLMOVE", "BLMOVEM", "BRPOPLPUSH");
        AddImmediate(LayoutKind.AllExceptLast, "BLPOP", "BRPOP", "BZPOPMIN", "BZPOPMAX");
        AddPrefixable(LayoutKind.AllExceptLast, "JSON.MGET");
        AddImmediate(LayoutKind.CountedAfterTimeout, "BLMPOP", "BZMPOP");
        AddImmediate(LayoutKind.CountedPairs, "MSETEX");
        AddPrefixable(LayoutKind.Triples, "JSON.MSET");
        AddImmediate(LayoutKind.StreamRead, "XREAD");
        AddImmediate(LayoutKind.StreamGroupRead, "XREADGROUP");
        AddImmediate(LayoutKind.Migrate, "MIGRATE");
        AddPrefixable(LayoutKind.First,
            "TS.GET", "TS.RANGE", "TS.REVRANGE", "TS.INFO", "TS.READ");
        AddPrefixable(LayoutKind.Counted, "TS.NRANGE", "TS.NREVRANGE");
        AddMutation(LayoutKind.First, MutationKind.LayoutKeys, ["TS.CREATE", "TS.ALTER"], deferred: false, prefixable: true);
        // Sample writes can update compaction destinations absent from the argument list. Never infer them.
        AddMutation(LayoutKind.First, MutationKind.IndirectKeys,
            ["TS.ADD", "TS.INCRBY", "TS.DECRBY", "TS.DEL"], deferred: false, prefixable: true);
        AddPrefixable(LayoutKind.FirstTwo, "TS.CREATERULE", "TS.DELETERULE");
        AddMutation(LayoutKind.Triples, MutationKind.IndirectKeys, ["TS.MADD"], deferred: false, prefixable: true);
        // Label-filter queries name no keys and return series from every key namespace, so they are
        // routed keylessly and are not prefixable.
        AddImmediate(LayoutKind.None, "TS.MGET", "TS.MRANGE", "TS.MREVRANGE", "TS.QUERYINDEX", "TS.QUERYLABELS");
        // Probabilistic layouts name every key, including both sides of a merge, so they are prefixable.
        AddPrefixable(LayoutKind.First,
            "BF.RESERVE", "BF.ADD", "BF.EXISTS", "BF.MADD", "BF.MEXISTS", "BF.INSERT", "BF.INFO", "BF.CARD", "BF.SCANDUMP", "BF.LOADCHUNK",
            "CF.RESERVE", "CF.ADD", "CF.ADDNX", "CF.INSERT", "CF.INSERTNX", "CF.DEL", "CF.EXISTS", "CF.MEXISTS", "CF.COUNT", "CF.INFO", "CF.SCANDUMP", "CF.LOADCHUNK",
            "CMS.INITBYDIM", "CMS.INITBYPROB", "CMS.INCRBY", "CMS.QUERY", "CMS.INFO",
            "TOPK.RESERVE", "TOPK.ADD", "TOPK.INCRBY", "TOPK.QUERY", "TOPK.COUNT", "TOPK.LIST", "TOPK.INFO",
            "TDIGEST.CREATE", "TDIGEST.RESET", "TDIGEST.ADD", "TDIGEST.MIN", "TDIGEST.MAX", "TDIGEST.QUANTILE", "TDIGEST.CDF", "TDIGEST.RANK", "TDIGEST.REVRANK", "TDIGEST.BYRANK", "TDIGEST.BYREVRANK", "TDIGEST.TRIMMED_MEAN", "TDIGEST.INFO",
            "VADD", "VREM", "VSETATTR");
        AddMutation(LayoutKind.CountedWithDestination, MutationKind.FirstArgument,
            ["CMS.MERGE", "TDIGEST.MERGE"], deferred: false, prefixable: true);
        return layouts.ToFrozenDictionary(StringComparer.Ordinal);

        void Add(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, CreateDefinition(operation, kind, deferred: true));
        }
        void AddImmediate(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, CreateDefinition(operation, kind, deferred: false));
        }
        void AddPrefixable(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, CreateDefinition(operation, kind, deferred: false, prefixable: true));
        }
        void AddMutation(LayoutKind kind, MutationKind mutation, string[] operations, bool deferred = true, bool prefixable = false)
        {
            foreach (var operation in operations) layouts.Add(operation, new(kind, deferred, mutation, prefixable));
        }
    }

    internal static KeyLayout GetDeferredLayout(string operation, ReadOnlySpan<RespireValue> args)
    {
        if (!Layouts.TryGetValue(operation, out var definition) || !definition.Deferred)
            throw new NotSupportedException($"{operation} has no supported deferred key layout. Use a typed facet or immediate execution; unknown and module commands are not guessed.");
        return Parse(definition.Kind, args);
    }

    /// <summary>Whether <paramref name="operation"/> has an explicit key layout.</summary>
    internal static bool HasLayout(string operation) => Layouts.ContainsKey(operation);

    /// <summary>Whether the registered layout identifies exactly one key in the first argument.</summary>
    internal static bool HasSingleFirstKeyLayout(string operation)
        => Layouts.TryGetValue(operation, out var definition) && definition.Kind == LayoutKind.First;

    /// <summary>Whether <paramref name="operation"/> has an explicit layout safe for key-prefixed views.</summary>
    internal static bool HasPrefixableLayout(string operation)
        => Layouts.TryGetValue(operation, out var definition) && definition.Prefixable;

    internal static bool TryGetLayout(string operation, ReadOnlySpan<RespireValue> args, out KeyLayout layout)
    {
        if (Layouts.TryGetValue(operation, out var definition))
        {
            layout = Parse(definition.Kind, args);
            return true;
        }
        layout = default;
        return false;
    }

    /// <summary>
    /// Non-throwing layout lookup for client-side cache mutation fences. Malformed or unsupported shapes
    /// return <see langword="false"/> so the caller falls back to a full-cache fence.
    /// </summary>
    internal static bool TryGetMutationLayout(
        string operation, in ClientCacheCommandKey args, out KeyLayout layout, bool includeReadKeys = false)
    {
        layout = default;
        if (!Layouts.TryGetValue(operation, out var definition)) return false;
        if (definition.Mutation is MutationKind.Unknown or MutationKind.IndirectKeys) return false;
        if (!TryGetArgumentLayout(definition.Kind, in args, out layout)) return false;
        // An explicit MultiKey policy intentionally fences every declared key, including sources.
        if (includeReadKeys) return true;
        // Validate the full argument shape before projecting only the written destination.
        layout = definition.Mutation switch
        {
            MutationKind.FirstArgument => new(0, 0, Extra: 0),
            MutationKind.SecondArgument => new(0, 0, Extra: 1),
            _ => layout,
        };
        return true;
    }

    private static bool TryGetArgumentLayout(LayoutKind kind, in ClientCacheCommandKey args, out KeyLayout layout)
    {
        var length = args.ArgumentCount;
        switch (kind)
        {
            case LayoutKind.None:
                layout = new(0, 0);
                return true;
            case LayoutKind.First:
                layout = new(0, 1);
                return length > 0;
            case LayoutKind.FirstTwo:
                layout = new(0, 2);
                return length >= 2;
            case LayoutKind.AfterFirst:
                if (length == 1 && args.GetArgument(0).EqualsAsciiIgnoreCase("HELP"))
                {
                    layout = new(0, 0);
                    return true;
                }
                layout = new(1, 1);
                return length >= 2;
            case LayoutKind.AllExceptLast:
                layout = new(0, length - 1);
                return length >= 2;
            case LayoutKind.All:
            case LayoutKind.Pairs:
            case LayoutKind.Triples:
                return TryShape(kind, length, out layout);
            case LayoutKind.BitOp:
                layout = new(1, length - 1);
                return length >= 3;
            case LayoutKind.Counted:
                return TryCountedArguments(args, length, 0, allowZero: false, stride: 1, out layout);
            case LayoutKind.CountedAfterName:
                return TryCountedArguments(args, length, 1, allowZero: true, stride: 1, out layout);
            case LayoutKind.CountedWithDestination:
                if (!TryCountedArguments(args, length, 1, allowZero: false, stride: 1, out layout)) return false;
                layout = layout with { Extra = 0 };
                return true;
            case LayoutKind.CountedPairs:
                return TryCountedArguments(args, length, 0, allowZero: false, stride: 2, out layout);
            case LayoutKind.CountedAfterTimeout:
                return TryCountedArguments(args, length, 1, allowZero: false, stride: 1, out layout);
            default:
                layout = default;
                return false;
        }
    }

    private static bool TryCountedArguments(in ClientCacheCommandKey args, int length, int countIndex,
        bool allowZero, int stride, out KeyLayout layout)
    {
        var count = length > countIndex && args.GetArgument(countIndex).TryGetInt64(out var value)
            ? value : (long?)null;
        return TryCounted(length, countIndex, count, allowZero, stride, out layout);
    }

    /// <summary>
    /// Gets the layout of a command whose every key position is described, so a key-prefixed view
    /// can rewrite its keys. Commands without such a layout must be rejected by prefixed views.
    /// </summary>
    internal static bool TryGetPrefixableLayout(string operation, ReadOnlySpan<RespireValue> args, out KeyLayout layout)
    {
        if (Layouts.TryGetValue(operation, out var definition) && definition.Prefixable)
        {
            layout = Parse(definition.Kind, args);
            return true;
        }
        layout = default;
        return false;
    }

    internal static KeyRouting ValidateClusterKeys(string operation, ReadOnlySpan<RespireValue> args)
    {
        if (!TryGetLayout(operation, args, out var layout)) return default;
        int? slot = null;
        for (var index = 0; index < layout.Count; index++)
            ValidateKey(args[layout.Start + index * layout.Stride], operation, ref slot);
        if (layout.Extra >= 0)
        {
            ValidateKey(args[layout.Extra], operation, ref slot);
            return new(true, layout.Extra);
        }
        return layout.Count > 0 ? new(true, layout.Start) : KeyRouting.NoKeys;
    }

    private static void ValidateKey(RespireValue key, string operation, ref int? slot)
    {
        RespireValue.ThrowIfNull(key, "args");
        key.TryGetClusterSlot(out var current);
        if (slot.HasValue && slot.Value != current)
            throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
        slot = current;
    }

    private static KeyLayout Parse(LayoutKind kind, ReadOnlySpan<RespireValue> args)
    {
        switch (kind)
        {
            case LayoutKind.None:
                return new(0, 0);
            case LayoutKind.First:
                Require(args.Length >= 1);
                return new(0, 1);
            case LayoutKind.FirstTwo:
                Require(args.Length >= 2);
                return new(0, 2);
            case LayoutKind.AfterFirst:
                // HELP is the only keyless subcommand; every other subcommand takes the key next.
                if (args.Length == 1 && args[0].EqualsAsciiIgnoreCase("HELP"))
                {
                    return new(0, 0);
                }
                Require(args.Length >= 2);
                return new(1, 1);
            case LayoutKind.All:
            case LayoutKind.Pairs:
            case LayoutKind.Triples:
                Require(TryShape(kind, args.Length, out var shape));
                return shape;
            case LayoutKind.BitOp:
                Require(args.Length >= 3);
                return new(1, args.Length - 1);
            case LayoutKind.CountedAfterName:
                return Counted(args, 1, allowZero: true);
            case LayoutKind.Counted:
                return Counted(args, 0);
            case LayoutKind.CountedWithDestination:
                return Counted(args, 1) with { Extra = 0 };
            case LayoutKind.AllExceptLast:
                Require(args.Length >= 2);
                return new(0, args.Length - 1);
            case LayoutKind.CountedPairs:
                return Counted(args, 0, stride: 2);
            case LayoutKind.CountedAfterTimeout:
                return Counted(args, 1);
            case LayoutKind.StreamRead:
                return StreamKeys(args, group: false);
            case LayoutKind.StreamGroupRead:
                return StreamKeys(args, group: true);
            case LayoutKind.Migrate:
                return MigrateKeys(args);
            default:
                throw new InvalidOperationException("Unknown raw command key layout.");
        }
    }

    private static KeyLayout StreamKeys(ReadOnlySpan<RespireValue> args, bool group)
    {
        if (group) Require(args.Length >= 3 && args[0].EqualsAsciiIgnoreCase("GROUP"));
        for (var index = group ? 3 : 0; index < args.Length;)
        {
            if (args[index].EqualsAsciiIgnoreCase("STREAMS"))
            {
                var remaining = args.Length - index - 1;
                Require(remaining >= 2 && remaining % 2 == 0);
                return new(index + 1, remaining / 2);
            }
            if (args[index].EqualsAsciiIgnoreCase("COUNT") || args[index].EqualsAsciiIgnoreCase("BLOCK")) index += 2;
            else if (group && args[index].EqualsAsciiIgnoreCase("NOACK")) index++;
            else throw new ArgumentException("Invalid stream-read options before STREAMS.", "args");
        }
        throw new ArgumentException("Stream reads require STREAMS followed by keys and IDs.", "args");
    }

    private static KeyLayout MigrateKeys(ReadOnlySpan<RespireValue> args)
    {
        Require(args.Length >= 5);
        if (!args[2].IsEmpty) return new(2, 1);
        for (var index = 5; index < args.Length;)
        {
            if (args[index].EqualsAsciiIgnoreCase("KEYS"))
            {
                Require(index + 1 < args.Length);
                return new(index + 1, args.Length - index - 1);
            }
            if (args[index].EqualsAsciiIgnoreCase("COPY") || args[index].EqualsAsciiIgnoreCase("REPLACE")) index++;
            else if (args[index].EqualsAsciiIgnoreCase("AUTH")) index += 2;
            else if (args[index].EqualsAsciiIgnoreCase("AUTH2")) index += 3;
            else throw new ArgumentException("Invalid MIGRATE options before KEYS.", "args");
        }
        return new(0, 0);
    }

    private static KeyLayout Counted(ReadOnlySpan<RespireValue> args, int index, bool allowZero = false, int stride = 1)
    {
        Require(TryCounted(args.Length, index,
            args.Length > index && args[index].TryGetInt64(out var count) ? count : null,
            allowZero, stride, out var layout));
        return layout;
    }

    /// <summary>Layouts whose keys are every argument, or the first of each fixed-size group.</summary>
    private static bool TryShape(LayoutKind kind, int length, out KeyLayout layout)
    {
        var stride = kind switch
        {
            LayoutKind.All => 1,
            LayoutKind.Pairs => 2,
            LayoutKind.Triples => 3,
            _ => throw new InvalidOperationException("Not a fixed-shape key layout."),
        };
        if (length < stride || length % stride != 0)
        {
            layout = default;
            return false;
        }
        layout = new(0, length / stride, stride);
        return true;
    }

    /// <summary>A key count at <paramref name="index"/> followed by that many keys (or key groups).</summary>
    private static bool TryCounted(int length, int index, long? count, bool allowZero, int stride, out KeyLayout layout)
    {
        if (length <= index || count is not { } value || value < (allowZero ? 0 : 1)
            || value > (length - index - 1) / stride)
        {
            layout = default;
            return false;
        }
        layout = new(index + 1, (int)value, stride);
        return true;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ArgumentException("Arguments do not match the command's required key layout.", "args");
    }
}
