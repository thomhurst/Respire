using System.Globalization;
using StackExchange.Redis;
using RedisSortedSetEntry = StackExchange.Redis.SortedSetEntry;

namespace Respire.StackExchangeCompat;

internal abstract partial class CompatDatabaseAsync
{
    public Task<bool> KeyExistsAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Key.EXISTS, [Key(key)], flags, static result => (long)result != 0);

    public Task<long> KeyExistsAsync(RedisKey[] keys, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Length == 0 ? Task.FromResult(0L)
            : Send(RespireCommands.Key.EXISTS, keys.Select(Key).ToArray(), flags, static result => (long)result);
    }

    public Task<bool> KeyPersistAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Key.PERSIST, [Key(key)], flags, static result => (long)result != 0);

    public Task<TimeSpan?> KeyTimeToLiveAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Key.PTTL, [Key(key)], flags, static result =>
            (long)result < 0 ? (TimeSpan?)null : TimeSpan.FromMilliseconds((long)result));

    public Task<RedisValue> StringGetAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.String.GET, [Key(key)], flags, Scalar);

    public Task<RedisValue[]> StringGetAsync(RedisKey[] keys, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Length == 0 ? Task.FromResult(Array.Empty<RedisValue>())
            : Send(RespireCommands.String.MGET, keys.Select(Key).ToArray(), flags, Values);
    }

    public Task<long> StringIncrementAsync(RedisKey key, long value, CommandFlags flags)
        => value switch
        {
            1 => Send(RespireCommands.String.INCR, [Key(key)], flags, static result => (long)result),
            -1 => Send(RespireCommands.String.DECR, [Key(key)], flags, static result => (long)result),
            >= 0 => Send(RespireCommands.String.INCRBY, [Key(key), value], flags, static result => (long)result),
            _ => Send(RespireCommands.String.DECRBY, [Key(key), unchecked(-value)], flags, static result => (long)result),
        };

    public Task<long> StringDecrementAsync(RedisKey key, long value, CommandFlags flags)
        => StringIncrementAsync(key, unchecked(-value), flags);

    public Task<bool> SetAddAsync(RedisKey key, RedisValue value, CommandFlags flags)
        => Send(RespireCommands.Set.SADD, [Key(key), Value(value)], flags, static result => (long)result != 0);

    public Task<long> SetAddAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        => SetChange(RespireCommands.Set.SADD, key, values, flags);

    public Task<bool> SetRemoveAsync(RedisKey key, RedisValue value, CommandFlags flags)
        => Send(RespireCommands.Set.SREM, [Key(key), Value(value)], flags, static result => (long)result != 0);

    public Task<long> SetRemoveAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        => SetChange(RespireCommands.Set.SREM, key, values, flags);

    private Task<long> SetChange(RespireCommand command, RedisKey key, RedisValue[] values, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Length == 0 ? Task.FromResult(0L)
            : Send(command, [Key(key), .. values.Select(Value)], flags, static result => (long)result);
    }

    public Task<RedisValue[]> SetMembersAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Set.SMEMBERS, [Key(key)], flags, Values);

    public Task<long> SetLengthAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Set.SCARD, [Key(key)], flags, static result => (long)result);

    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, CommandFlags flags)
        => Send(RespireCommands.SortedSet.ZADD, [Key(key), Score(score), Value(member)], flags, static result => (long)result != 0);

    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, When when, CommandFlags flags)
        => when == When.Always ? SortedSetAddAsync(key, member, score, flags) : throw Compatibility.Unsupported($"SortedSetAdd When.{when}");

    public Task<bool> SortedSetAddAsync(RedisKey key, RedisValue member, double score, SortedSetWhen when, CommandFlags flags)
        => when == SortedSetWhen.Always ? SortedSetAddAsync(key, member, score, flags) : throw Compatibility.Unsupported($"SortedSetAdd SortedSetWhen.{when}");

    public Task<long> SortedSetAddAsync(RedisKey key, RedisSortedSetEntry[] values, When when, CommandFlags flags)
        => when == When.Always ? SortedSetAddAsync(key, values, flags) : throw Compatibility.Unsupported($"SortedSetAdd When.{when}");

    public Task<long> SortedSetAddAsync(RedisKey key, RedisSortedSetEntry[] values, SortedSetWhen when, CommandFlags flags)
        => when == SortedSetWhen.Always ? SortedSetAddAsync(key, values, flags) : throw Compatibility.Unsupported($"SortedSetAdd SortedSetWhen.{when}");

    public Task<long> SortedSetAddAsync(RedisKey key, RedisSortedSetEntry[] values, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0) return Task.FromResult(0L);
        var arguments = new RedisValue[1 + values.Length * 2];
        arguments[0] = Key(key);
        for (var index = 0; index < values.Length; index++)
        {
            arguments[1 + index * 2] = Score(values[index].Score);
            arguments[2 + index * 2] = Value(values[index].Element);
        }
        return Send(RespireCommands.SortedSet.ZADD, arguments, flags, static result => (long)result);
    }

    public Task<bool> SortedSetRemoveAsync(RedisKey key, RedisValue member, CommandFlags flags)
        => Send(RespireCommands.SortedSet.ZREM, [Key(key), Value(member)], flags, static result => (long)result != 0);

    public Task<long> SortedSetRemoveAsync(RedisKey key, RedisValue[] members, CommandFlags flags)
        => SetChange(RespireCommands.SortedSet.ZREM, key, members, flags);

    public Task<long> SortedSetLengthAsync(RedisKey key, double min, double max, Exclude exclude, CommandFlags flags)
        => double.IsNegativeInfinity(min) && double.IsPositiveInfinity(max)
            ? Send(RespireCommands.SortedSet.ZCARD, [Key(key)], flags, static result => (long)result)
            : Send(RespireCommands.SortedSet.ZCOUNT,
                [Key(key), Bound(min, exclude, Exclude.Start), Bound(max, exclude, Exclude.Stop)], flags, static result => (long)result);

    public Task<RedisValue[]> SortedSetRangeByRankAsync(RedisKey key, long start, long stop, Order order, CommandFlags flags)
        => Send(RankCommand(order), [Key(key), start, stop], flags, Values);

    public Task<RedisSortedSetEntry[]> SortedSetRangeByRankWithScoresAsync(RedisKey key, long start, long stop, Order order, CommandFlags flags)
        => Send(RankCommand(order), [Key(key), start, stop, "WITHSCORES"], flags, Entries);

    public Task<RedisValue[]> SortedSetRangeByScoreAsync(RedisKey key, double start, double stop, Exclude exclude, Order order, long skip, long take, CommandFlags flags)
        => Send(ScoreCommand(order), ScoreArguments(key, start, stop, exclude, order, skip, take, false), flags, Values);

    public Task<RedisSortedSetEntry[]> SortedSetRangeByScoreWithScoresAsync(RedisKey key, double start, double stop, Exclude exclude, Order order, long skip, long take, CommandFlags flags)
        => Send(ScoreCommand(order), ScoreArguments(key, start, stop, exclude, order, skip, take, true), flags, Entries);

    private static RespireCommand RankCommand(Order order) => order switch
    {
        Order.Ascending => RespireCommands.SortedSet.ZRANGE,
        Order.Descending => RespireCommands.SortedSet.ZREVRANGE,
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private static RespireCommand ScoreCommand(Order order) => order switch
    {
        Order.Ascending => RespireCommands.SortedSet.ZRANGEBYSCORE,
        Order.Descending => RespireCommands.SortedSet.ZREVRANGEBYSCORE,
        _ => throw new ArgumentOutOfRangeException(nameof(order)),
    };

    private static RedisValue[] ScoreArguments(RedisKey key, double start, double stop, Exclude exclude, Order order, long skip, long take, bool scores)
    {
        if ((order == Order.Ascending) == (start > stop))
        {
            (start, stop) = (stop, start);
            exclude = exclude switch
            {
                Exclude.Start => Exclude.Stop,
                Exclude.Stop => Exclude.Start,
                _ => exclude,
            };
        }
        RedisValue[] arguments = [Key(key), Bound(start, exclude, Exclude.Start), Bound(stop, exclude, Exclude.Stop)];
        if (scores) arguments = [.. arguments, "WITHSCORES"];
        if (skip != 0 || take != -1) arguments = [.. arguments, "LIMIT", skip, take];
        return arguments;
    }

    private static RedisValue Bound(double score, Exclude exclude, Exclude edge)
    {
        if ((exclude & ~Exclude.Both) != 0) throw new ArgumentOutOfRangeException(nameof(exclude));
        var value = Score(score);
        return (exclude & edge) != 0 ? "(" + (string?)value : value;
    }

    private static RedisValue Score(double score)
    {
        if (double.IsPositiveInfinity(score)) return "+inf";
        if (double.IsNegativeInfinity(score)) return "-inf";
        return score.ToString("R", CultureInfo.InvariantCulture);
    }

    private static RedisSortedSetEntry[] Entries(RedisResult result)
    {
        var values = ((RedisResult[])result)!;
        // RESP3 score ranges contain nested [member, score] pairs; RESP2 and ZSCAN are flat.
        if (values.Length != 0 && values[0].Resp2Type == ResultType.Array)
            return values.Select(static pair =>
            {
                var entry = ((RedisResult[])pair)!;
                return new RedisSortedSetEntry(Scalar(entry[0]), (double)entry[1]);
            }).ToArray();
        var entries = new RedisSortedSetEntry[values.Length / 2];
        for (var index = 0; index < entries.Length; index++)
            entries[index] = new RedisSortedSetEntry(Scalar(values[index * 2]), (double)values[index * 2 + 1]);
        return entries;
    }

    public IAsyncEnumerable<RedisSortedSetEntry> SortedSetScanAsync(RedisKey key, RedisValue pattern, int pageSize, long cursor, int pageOffset, CommandFlags flags)
        => Scan(key, pattern, pageSize, cursor, pageOffset, flags);

    internal CompatSortedSetScan Scan(RedisKey key, RedisValue pattern, int pageSize, long cursor, int pageOffset, CommandFlags flags)
    {
        if (this is CompatTransaction) throw Compatibility.Unsupported("SortedSetScan on ITransaction; enumerate outside the transaction");
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (pageOffset < 0) throw new ArgumentOutOfRangeException(nameof(pageOffset));
        if ((flags & CommandFlags.FireAndForget) != 0) throw Compatibility.Unsupported("SortedSetScan FireAndForget");
        var savedKey = Key(key);
        var savedPattern = pattern.IsNullOrEmpty ? RedisValue.Null : Value(pattern);
        return new CompatSortedSetScan(DatabaseOwner, pageSize, cursor, pageOffset, next =>
        {
            RedisValue[] arguments = [savedKey, unchecked((ulong)next).ToString(CultureInfo.InvariantCulture)];
            if (!savedPattern.IsNull && savedPattern != "*") arguments = [.. arguments, "MATCH", savedPattern];
            arguments = [.. arguments, "COUNT", pageSize];
            return Send(RespireCommands.SortedSet.ZSCAN, arguments, flags, static result =>
            {
                var reply = ((RedisResult[])result)!;
                var nextCursor = unchecked((long)ulong.Parse((string)reply[0]!, CultureInfo.InvariantCulture));
                return new CompatSortedSetScan.Page(nextCursor, Entries(reply[1]));
            });
        });
    }
}
