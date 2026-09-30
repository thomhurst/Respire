using System.Collections.Frozen;
using Respire.Commands;

namespace Respire;

/// <summary>Conservative, explicit key layouts for the public deferred escape hatch.</summary>
internal static class DeferredRawCommands
{
    internal static RespirePending<RespireResult> Enqueue(
        IPendingSink sink, RespireCommand descriptor, RespireValue[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var operation = Normalize(descriptor.Name);
        var words = operation.Split(' ');
        var root = words[0];
        var behavior = RespireCommand.Classify(root);
        if (behavior is RespireCommandBehavior.ConnectionScoped or RespireCommandBehavior.Blocking
            || RespireCommand.IsBlocking(root, behavior, args))
            throw new NotSupportedException($"{operation} cannot run in a deferred command queue.");

        // Validate nulls while snapshotting, before counted layouts read their arguments.
        var tokens = new RespireValue[checked(words.Length + args.Length)];
        for (var index = 0; index < words.Length; index++) tokens[index] = words[index];
        for (var index = 0; index < args.Length; index++)
        {
            RespireValue.ThrowIfNull(args[index], nameof(args));
            tokens[words.Length + index] = args[index].Snapshot();
        }

        // Determine every key, never just guess that the first argument is the routing key.
        var layout = GetLayout(operation, tokens.AsSpan(words.Length));

        // Prefixing only mutates this private snapshot. A failed slot check leaves the sink untouched.
        int? slot = null;
        for (var index = 0; index < layout.Count; index++)
            PrefixKey(layout.Start + index * layout.Stride);
        if (layout.Extra >= 0) PrefixKey(layout.Extra);

        var firstKey = layout.Extra >= 0 ? layout.Extra : layout.Count > 0 ? layout.Start : -1;
        var command = new DynamicCommand(tokens, firstKey < 0 ? -1 : words.Length + firstKey, words.Length);
        return sink.Add<DynamicCommand, RespireResult>(operation, command, static (client, value) =>
        {
            // Unread pendings must not retain pooled response storage.
            var owned = value.ToOwned();
            return client.CreateResult(in owned);
        });

        void PrefixKey(int index)
        {
            var key = tokens[words.Length + index].AsKey();
            var resolved = sink.Client.Key(in key);
            tokens[words.Length + index] = resolved;
            if (sink.Client.Core.Cluster is null) return;
            // AsKey produces text/bytes, and prefixing preserves that representation. Every key hashes.
            if (!resolved.TryGetClusterSlot(out var current))
                throw new InvalidOperationException("The deferred command key has no Cluster slot.");
            if (slot.HasValue && slot.Value != current)
                // Match typed multi-key commands, including their locally detected CROSSSLOT contract.
                throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
            slot = current;
        }
    }

    private static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = name.ToUpperInvariant();
        var previousSpace = true;
        foreach (var character in name)
        {
            if (character == ' ')
            {
                if (previousSpace) throw new ArgumentException("Use a verb or known subcommand without inline arguments.", nameof(name));
                previousSpace = true;
                continue;
            }
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-'))
                throw new ArgumentException("Command names must contain ASCII command tokens separated by single spaces.", nameof(name));
            previousSpace = false;
        }
        if (previousSpace) throw new ArgumentException("Command names must not end with a space.", nameof(name));
        return operation;
    }

    internal readonly record struct KeyLayout(int Start, int Count, int Stride = 1, int Extra = -1);

    private enum LayoutKind { None, First, FirstTwo, All, Pairs, BitOp, CountedAfterName, Counted, CountedWithDestination }

    private static readonly FrozenDictionary<string, LayoutKind> Layouts = CreateLayouts();
    internal static IEnumerable<string> SupportedOperations => Layouts.Keys;

    private static FrozenDictionary<string, LayoutKind> CreateLayouts()
    {
        var layouts = new Dictionary<string, LayoutKind>(StringComparer.Ordinal);
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
        return layouts.ToFrozenDictionary(StringComparer.Ordinal);

        void Add(LayoutKind kind, params string[] operations)
        {
            foreach (var operation in operations) layouts.Add(operation, kind);
        }
    }

    internal static KeyLayout GetLayout(string operation, ReadOnlySpan<RespireValue> args)
    {
        if (!Layouts.TryGetValue(operation, out var kind))
            throw new NotSupportedException($"{operation} has no supported deferred key layout. Use a typed facet or immediate execution; unknown and module commands are not guessed.");
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
            default:
                throw new InvalidOperationException("Unknown deferred command key layout.");
        }
    }

    private static KeyLayout Counted(ReadOnlySpan<RespireValue> args, int index, bool allowZero = false)
    {
        Require(args.Length > index);
        Require(args[index].TryGetInt64(out var count) && count >= (allowZero ? 0 : 1)
            && count <= args.Length - index - 1);
        return new(index + 1, (int)count);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ArgumentException("Arguments do not match the command's required key layout.", "args");
    }
}
