using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

// IDatabase requires synchronous methods; waits intentionally implement that contract.
#pragma warning disable SER308

internal sealed partial class CompatDatabase
{
    public RedisResult ScriptEvaluate(string script, RedisKey[]? keys, RedisValue[]? values, CommandFlags flags)
        => Wait(ScriptEvaluateAsync(script, keys, values, flags));

    public RedisValue[] SortedSetRangeByValue(RedisKey key, RedisValue min, RedisValue max, Exclude exclude,
        long skip, long take, CommandFlags flags)
        => Wait(SortedSetRangeByValueAsync(key, min, max, exclude, skip, take, flags));

    public RedisValue[] SortedSetRangeByValue(RedisKey key, RedisValue min, RedisValue max, Exclude exclude,
        Order order, long skip, long take, CommandFlags flags)
        => Wait(SortedSetRangeByValueAsync(key, min, max, exclude, order, skip, take, flags));

    public long SortedSetRemoveRangeByValue(RedisKey key, RedisValue min, RedisValue max, Exclude exclude, CommandFlags flags)
        => Wait(SortedSetRemoveRangeByValueAsync(key, min, max, exclude, flags));
}
