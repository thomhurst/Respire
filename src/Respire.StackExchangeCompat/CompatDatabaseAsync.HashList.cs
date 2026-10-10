using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

internal abstract partial class CompatDatabaseAsync
{
    public Task<HashEntry[]> HashGetAllAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Hash.HGETALL, [Key(key)], flags, static result =>
        {
            var values = ((RedisResult[])result)!;
            var entries = new HashEntry[values.Length / 2];
            for (var index = 0; index < entries.Length; index++)
                entries[index] = new HashEntry(Scalar(values[index * 2]), Scalar(values[index * 2 + 1]));
            return entries;
        });

    public Task<long> HashLengthAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Hash.HLEN, [Key(key)], flags, static result => (long)result);

    public Task<bool> HashDeleteAsync(RedisKey key, RedisValue hashField, CommandFlags flags)
        => Send(RespireCommands.Hash.HDEL, [Key(key), Value(hashField)], flags, static result => (long)result != 0);

    public Task<long> HashDeleteAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(hashFields);
        if (hashFields.Length == 0) return Task.FromResult(0L);
        return Send(RespireCommands.Hash.HDEL, [Key(key), .. hashFields.Select(Value)], flags, static result => (long)result);
    }

    public Task<long> ListLengthAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.List.LLEN, [Key(key)], flags, static result => (long)result);

    public Task<RedisValue> ListGetByIndexAsync(RedisKey key, long index, CommandFlags flags)
        => Send(RespireCommands.List.LINDEX, [Key(key), index], flags, Scalar);

    public Task<long> ListLeftPushAsync(RedisKey key, RedisValue value, When when, CommandFlags flags)
        => ListLeftPushAsync(key, [value], when, flags);

    public Task<long> ListLeftPushAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        => ListLeftPushAsync(key, values, When.Always, flags);

    public Task<long> ListLeftPushAsync(RedisKey key, RedisValue[] values, When when, CommandFlags flags)
        => ListPush(key, values, when, flags, left: true);

    public Task<long> ListRemoveAsync(RedisKey key, RedisValue value, long count, CommandFlags flags)
        => Send(RespireCommands.List.LREM, [Key(key), count, Value(value)], flags, static result => (long)result);

    public Task ListTrimAsync(RedisKey key, long start, long stop, CommandFlags flags)
        => Send(RespireCommands.List.LTRIM, [Key(key), start, stop], flags, static result => (string?)result);

    public Task<RedisValue> ListRightPopLeftPushAsync(RedisKey source, RedisKey destination, CommandFlags flags)
        => Send(RespireCommands.List.RPOPLPUSH, [Key(source), Key(destination)], flags, Scalar);

    private Task<long> ListPush(RedisKey key, RedisValue[] values, When when, CommandFlags flags, bool left)
    {
        ArgumentNullException.ThrowIfNull(values);
        var command = when switch
        {
            When.Always => left ? RespireCommands.List.LPUSH : RespireCommands.List.RPUSH,
            When.Exists => left ? RespireCommands.List.LPUSHX : RespireCommands.List.RPUSHX,
            _ => throw Compatibility.Unsupported(left ? "ListLeftPush When.NotExists" : "ListRightPush When.NotExists"),
        };
        if (values.Length == 0) return ListLengthAsync(key, flags);
        return Send(command, [Key(key), .. values.Select(Value)], flags, static result => (long)result);
    }
}
