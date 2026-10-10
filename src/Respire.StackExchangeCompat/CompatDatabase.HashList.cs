using StackExchange.Redis;

// IDatabase requires synchronous methods; these waits intentionally implement that contract.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatDatabase
{
    public HashEntry[] HashGetAll(RedisKey key, CommandFlags flags) => Wait(HashGetAllAsync(key, flags));
    public long HashLength(RedisKey key, CommandFlags flags) => Wait(HashLengthAsync(key, flags));
    public bool HashDelete(RedisKey key, RedisValue hashField, CommandFlags flags) => Wait(HashDeleteAsync(key, hashField, flags));
    public long HashDelete(RedisKey key, RedisValue[] hashFields, CommandFlags flags) => Wait(HashDeleteAsync(key, hashFields, flags));
    public long ListLength(RedisKey key, CommandFlags flags) => Wait(ListLengthAsync(key, flags));
    public RedisValue ListGetByIndex(RedisKey key, long index, CommandFlags flags) => Wait(ListGetByIndexAsync(key, index, flags));
    public long ListLeftPush(RedisKey key, RedisValue value, When when, CommandFlags flags) => Wait(ListLeftPushAsync(key, value, when, flags));
    public long ListLeftPush(RedisKey key, RedisValue[] values, CommandFlags flags) => Wait(ListLeftPushAsync(key, values, flags));
    public long ListLeftPush(RedisKey key, RedisValue[] values, When when, CommandFlags flags) => Wait(ListLeftPushAsync(key, values, when, flags));
    public long ListRemove(RedisKey key, RedisValue value, long count, CommandFlags flags) => Wait(ListRemoveAsync(key, value, count, flags));
    public void ListTrim(RedisKey key, long start, long stop, CommandFlags flags) => Wait(ListTrimAsync(key, start, stop, flags));
    public RedisValue ListRightPopLeftPush(RedisKey source, RedisKey destination, CommandFlags flags) => Wait(ListRightPopLeftPushAsync(source, destination, flags));
}
