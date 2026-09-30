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

        // Determine every key, never just guess that the first argument is the routing key.
        var layout = GetLayout(operation, args);
        var tokens = new RespireValue[checked(words.Length + args.Length)];
        for (var index = 0; index < words.Length; index++) tokens[index] = words[index];
        for (var index = 0; index < args.Length; index++)
        {
            RespireValue.ThrowIfNull(args[index], nameof(args));
            tokens[words.Length + index] = args[index].Snapshot();
        }

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
            if (sink.Client.Core.Cluster is null || !resolved.TryGetClusterSlot(out var current)) return;
            if (slot.HasValue && slot.Value != current)
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

    private readonly record struct KeyLayout(int Start, int Count, int Stride = 1, int Extra = -1);

    private static KeyLayout GetLayout(string operation, ReadOnlySpan<RespireValue> args)
    {
        switch (operation)
        {
            case "PING": case "ECHO": case "TIME":
                return new(0, 0);
            case "GET": case "SET": case "GETSET": case "SETNX": case "SETEX": case "PSETEX":
            case "GETDEL": case "GETEX": case "APPEND": case "STRLEN": case "GETRANGE": case "SETRANGE":
            case "INCR": case "INCRBY": case "INCRBYFLOAT": case "DECR": case "DECRBY":
            case "TYPE": case "TTL": case "PTTL": case "EXPIRE": case "PEXPIRE": case "EXPIREAT":
            case "PEXPIREAT": case "EXPIRETIME": case "PEXPIRETIME": case "PERSIST": case "DUMP": case "RESTORE":
            case "HGET": case "HSET": case "HSETNX": case "HMGET": case "HMSET": case "HGETALL":
            case "HDEL": case "HEXISTS": case "HLEN": case "HKEYS": case "HVALS": case "HSTRLEN":
            case "HINCRBY": case "HINCRBYFLOAT": case "HRANDFIELD":
            case "LPUSH": case "RPUSH": case "LPUSHX": case "RPUSHX": case "LPOP": case "RPOP":
            case "LLEN": case "LRANGE": case "LINDEX": case "LSET": case "LINSERT": case "LREM": case "LTRIM": case "LPOS":
            case "SADD": case "SREM": case "SCARD": case "SMEMBERS": case "SISMEMBER": case "SMISMEMBER":
            case "SPOP": case "SRANDMEMBER":
            case "ZADD": case "ZREM": case "ZCARD": case "ZSCORE": case "ZMSCORE": case "ZINCRBY":
            case "ZCOUNT": case "ZLEXCOUNT": case "ZRANGE": case "ZREVRANGE": case "ZRANGEBYSCORE":
            case "ZREVRANGEBYSCORE": case "ZRANGEBYLEX": case "ZREVRANGEBYLEX": case "ZRANK": case "ZREVRANK":
            case "ZREMRANGEBYRANK": case "ZREMRANGEBYSCORE": case "ZREMRANGEBYLEX": case "ZPOPMIN": case "ZPOPMAX": case "ZRANDMEMBER":
            case "GETBIT": case "SETBIT": case "BITCOUNT": case "BITPOS": case "BITFIELD": case "BITFIELD_RO":
            case "PFADD": case "GEOADD": case "GEODIST": case "GEOHASH": case "GEOPOS": case "GEOSEARCH":
            case "XADD": case "XACK": case "XDEL": case "XTRIM": case "XLEN": case "XRANGE": case "XREVRANGE":
            case "XPENDING": case "XCLAIM": case "XAUTOCLAIM":
            case "OBJECT ENCODING": case "OBJECT FREQ": case "OBJECT IDLETIME": case "OBJECT REFCOUNT":
            case "MEMORY USAGE": case "XINFO STREAM": case "XINFO GROUPS": case "XINFO CONSUMERS":
            case "XGROUP CREATE": case "XGROUP SETID": case "XGROUP DESTROY": case "XGROUP CREATECONSUMER": case "XGROUP DELCONSUMER":
                Require(args.Length >= 1);
                return new(0, 1);
            case "RENAME": case "RENAMENX": case "COPY": case "LCS": case "SMOVE": case "LMOVE":
            case "RPOPLPUSH": case "ZRANGESTORE": case "GEOSEARCHSTORE":
                Require(args.Length >= 2);
                return new(0, 2);
            case "DEL": case "UNLINK": case "EXISTS": case "TOUCH": case "MGET":
            case "SDIFF": case "SINTER": case "SUNION": case "SDIFFSTORE": case "SINTERSTORE": case "SUNIONSTORE":
            case "PFCOUNT": case "PFMERGE":
                Require(args.Length >= 1);
                return new(0, args.Length);
            case "MSET": case "MSETNX":
                Require(args.Length >= 2 && args.Length % 2 == 0);
                return new(0, args.Length / 2, 2);
            case "BITOP":
                Require(args.Length >= 3);
                return new(1, args.Length - 1);
            case "EVAL": case "EVALSHA": case "EVAL_RO": case "EVALSHA_RO": case "FCALL": case "FCALL_RO":
                return Counted(args, 1, allowZero: true);
            case "LMPOP": case "ZMPOP": case "SINTERCARD": case "ZDIFF": case "ZINTER": case "ZUNION": case "ZINTERCARD":
                return Counted(args, 0);
            case "ZDIFFSTORE": case "ZINTERSTORE": case "ZUNIONSTORE":
                return Counted(args, 1) with { Extra = 0 };
            default:
                throw new NotSupportedException($"{operation} has no supported deferred key layout. Use a typed facet or immediate execution; unknown and module commands are not guessed.");
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
