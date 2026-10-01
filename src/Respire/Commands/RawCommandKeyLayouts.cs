using System.Collections.Frozen;
using Respire.Internal;

namespace Respire.Commands;

/// <summary>Explicit key layouts shared by immediate and deferred raw execution.</summary>
internal static class RawCommandKeyLayouts
{
    internal readonly record struct KeyRouting(bool Known, int Index)
    {
        internal const int NoKeyIndex = -1;
        internal static KeyRouting NoKeys => new(true, NoKeyIndex);
    }

    internal readonly record struct KeyLayout(int Start, int Count, int Stride = 1, int Extra = -1);

    private enum LayoutKind
    {
        None, First, FirstTwo, All, Pairs, BitOp, CountedAfterName, Counted, CountedWithDestination,
        AllExceptLast, CountedPairs, Triples, CountedAfterTimeout, StreamRead, StreamGroupRead, Migrate,
    }
    private readonly record struct Definition(LayoutKind Kind, bool Deferred);

    private static readonly FrozenDictionary<string, Definition> Layouts = CreateLayouts();
    // Test-only enumeration keeps COMMAND GETKEYS coverage aligned with the full deferred allowlist.
    internal static IEnumerable<string> DeferredOperations => Layouts.Where(pair => pair.Value.Deferred).Select(pair => pair.Key);

    private static FrozenDictionary<string, Definition> CreateLayouts()
    {
        var layouts = new Dictionary<string, Definition>(StringComparer.Ordinal);
        Add(LayoutKind.None,
            "PING", "ECHO", "TIME");
        Add(LayoutKind.First,
            "GET", "SET", "GETSET", "SETNX", "SETEX", "PSETEX", "GETDEL",
            "GETEX", "APPEND", "STRLEN", "GETRANGE", "SETRANGE", "INCR", "INCRBY",
            "INCRBYFLOAT", "DECR", "DECRBY", "TYPE", "TTL", "PTTL", "EXPIRE",
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
            "XGROUP SETID", "XGROUP DESTROY", "XGROUP CREATECONSUMER", "XGROUP DELCONSUMER");
        Add(LayoutKind.FirstTwo,
            "RENAME", "RENAMENX", "COPY", "LCS", "SMOVE", "LMOVE", "RPOPLPUSH",
            "ZRANGESTORE", "GEOSEARCHSTORE");
        Add(LayoutKind.All,
            "DEL", "UNLINK", "EXISTS", "TOUCH", "MGET", "SDIFF", "SINTER",
            "SUNION", "SDIFFSTORE", "SINTERSTORE", "SUNIONSTORE", "PFCOUNT", "PFMERGE");
        Add(LayoutKind.Pairs,
            "MSET", "MSETNX");
        Add(LayoutKind.BitOp,
            "BITOP");
        Add(LayoutKind.CountedAfterName,
            "EVAL", "EVALSHA", "EVAL_RO", "EVALSHA_RO", "FCALL", "FCALL_RO");
        Add(LayoutKind.Counted,
            "LMPOP", "ZMPOP", "SINTERCARD", "ZDIFF", "ZINTER", "ZUNION", "ZINTERCARD");
        Add(LayoutKind.CountedWithDestination,
            "ZDIFFSTORE", "ZINTERSTORE", "ZUNIONSTORE");
        // Immediate-only additions do not expand the conservative deferred allowlist.
        AddImmediate(LayoutKind.All, "KEYDB.MEXISTS");
        // LMOVEM/BLMOVEM are Redis 8.10 commands, with source and destination in the first two positions.
        AddImmediate(LayoutKind.FirstTwo, "LMOVEM", "BLMOVE", "BLMOVEM", "BRPOPLPUSH");
        AddImmediate(LayoutKind.AllExceptLast, "BLPOP", "BRPOP", "BZPOPMIN", "BZPOPMAX", "JSON.MGET");
        AddImmediate(LayoutKind.CountedAfterTimeout, "BLMPOP", "BZMPOP");
        AddImmediate(LayoutKind.CountedPairs, "MSETEX");
        AddImmediate(LayoutKind.Triples, "JSON.MSET");
        AddImmediate(LayoutKind.StreamRead, "XREAD");
        AddImmediate(LayoutKind.StreamGroupRead, "XREADGROUP");
        AddImmediate(LayoutKind.Migrate, "MIGRATE");
        AddImmediate(LayoutKind.First,
            "BF.RESERVE", "BF.ADD", "BF.EXISTS", "BF.MADD", "BF.MEXISTS", "BF.INSERT", "BF.INFO", "BF.CARD", "BF.SCANDUMP", "BF.LOADCHUNK",
            "CF.RESERVE", "CF.ADD", "CF.ADDNX", "CF.INSERT", "CF.INSERTNX", "CF.DEL", "CF.EXISTS", "CF.MEXISTS", "CF.COUNT", "CF.INFO", "CF.SCANDUMP", "CF.LOADCHUNK",
            "CMS.INITBYDIM", "CMS.INITBYPROB", "CMS.INCRBY", "CMS.QUERY", "CMS.INFO",
            "TOPK.RESERVE", "TOPK.ADD", "TOPK.INCRBY", "TOPK.QUERY", "TOPK.COUNT", "TOPK.LIST", "TOPK.INFO",
            "TDIGEST.CREATE", "TDIGEST.RESET", "TDIGEST.ADD", "TDIGEST.MIN", "TDIGEST.MAX", "TDIGEST.QUANTILE", "TDIGEST.CDF", "TDIGEST.RANK", "TDIGEST.REVRANK", "TDIGEST.BYRANK", "TDIGEST.BYREVRANK", "TDIGEST.TRIMMED_MEAN", "TDIGEST.INFO");
        AddImmediate(LayoutKind.CountedWithDestination, "CMS.MERGE", "TDIGEST.MERGE");
        return layouts.ToFrozenDictionary(StringComparer.Ordinal);

        void Add(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, new(kind, Deferred: true));
        }
        void AddImmediate(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, new(kind, Deferred: false));
        }
    }

    internal static KeyLayout GetDeferredLayout(string operation, ReadOnlySpan<RespireValue> args)
    {
        if (!Layouts.TryGetValue(operation, out var definition) || !definition.Deferred)
            throw new NotSupportedException($"{operation} has no supported deferred key layout. Use a typed facet or immediate execution; unknown and module commands are not guessed.");
        return Parse(definition.Kind, args);
    }

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

    internal static bool TryGetMutationLayout(
        string operation, in ClientCacheCommandKey args, out KeyLayout layout)
    {
        if (!Layouts.TryGetValue(operation, out var definition))
        {
            layout = default;
            return false;
        }

        switch (definition.Kind)
        {
            case LayoutKind.All:
                if (args.ArgumentCount < 1)
                {
                    layout = default;
                    return false;
                }
                layout = new(0, args.ArgumentCount);
                return true;
            case LayoutKind.Pairs:
                if (args.ArgumentCount < 2 || args.ArgumentCount % 2 != 0)
                {
                    layout = default;
                    return false;
                }
                layout = new(0, args.ArgumentCount / 2, 2);
                return true;
            case LayoutKind.CountedPairs:
                if (args.ArgumentCount == 0
                    || !args.GetArgument(0).TryGetInt64(out var count)
                    || count < 1 || count > (args.ArgumentCount - 1) / 2)
                {
                    layout = default;
                    return false;
                }
                layout = new(1, (int)count, 2);
                return true;
            case LayoutKind.Triples:
                if (args.ArgumentCount < 3 || args.ArgumentCount % 3 != 0)
                {
                    layout = default;
                    return false;
                }
                layout = new(0, args.ArgumentCount / 3, 3);
                return true;
            default:
                layout = default;
                return false;
        }
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
            case LayoutKind.All:
                Require(args.Length >= 1);
                return new(0, args.Length);
            case LayoutKind.Pairs:
                Require(args.Length >= 2 && args.Length % 2 == 0);
                return new(0, args.Length / 2, 2);
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
            case LayoutKind.Triples:
                Require(args.Length >= 3 && args.Length % 3 == 0);
                return new(0, args.Length / 3, 3);
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
        Require(args.Length > index);
        Require(args[index].TryGetInt64(out var count) && count >= (allowZero ? 0 : 1)
            && count <= (args.Length - index - 1) / stride);
        return new(index + 1, (int)count, stride);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ArgumentException("Arguments do not match the command's required key layout.", "args");
    }
}
