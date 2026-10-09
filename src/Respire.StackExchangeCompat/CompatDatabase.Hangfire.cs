using StackExchange.Redis;
using RedisSortedSetEntry = StackExchange.Redis.SortedSetEntry;

namespace Respire.StackExchangeCompat;

// IDatabase requires synchronous methods; waits intentionally implement that contract.
#pragma warning disable SER308

internal sealed partial class CompatDatabase
{
    public bool KeyExists(RedisKey key, CommandFlags flags) => Wait(KeyExistsAsync(key, flags));
    public long KeyExists(RedisKey[] keys, CommandFlags flags) => Wait(KeyExistsAsync(keys, flags));
    public bool KeyPersist(RedisKey key, CommandFlags flags) => Wait(KeyPersistAsync(key, flags));
    public TimeSpan? KeyTimeToLive(RedisKey key, CommandFlags flags) => Wait(KeyTimeToLiveAsync(key, flags));
    public RedisValue StringGet(RedisKey key, CommandFlags flags) => Wait(StringGetAsync(key, flags));
    public RedisValue[] StringGet(RedisKey[] keys, CommandFlags flags) => Wait(StringGetAsync(keys, flags));
    public long StringIncrement(RedisKey key, long value, CommandFlags flags) => Wait(StringIncrementAsync(key, value, flags));
    public long StringDecrement(RedisKey key, long value, CommandFlags flags) => Wait(StringDecrementAsync(key, value, flags));
    public bool SetAdd(RedisKey key, RedisValue value, CommandFlags flags) => Wait(SetAddAsync(key, value, flags));
    public long SetAdd(RedisKey key, RedisValue[] values, CommandFlags flags) => Wait(SetAddAsync(key, values, flags));
    public bool SetRemove(RedisKey key, RedisValue value, CommandFlags flags) => Wait(SetRemoveAsync(key, value, flags));
    public long SetRemove(RedisKey key, RedisValue[] values, CommandFlags flags) => Wait(SetRemoveAsync(key, values, flags));
    public RedisValue[] SetMembers(RedisKey key, CommandFlags flags) => Wait(SetMembersAsync(key, flags));
    public long SetLength(RedisKey key, CommandFlags flags) => Wait(SetLengthAsync(key, flags));
    public bool SortedSetAdd(RedisKey key, RedisValue member, double score, CommandFlags flags) => Wait(SortedSetAddAsync(key, member, score, flags));
    public long SortedSetAdd(RedisKey key, RedisSortedSetEntry[] values, CommandFlags flags) => Wait(SortedSetAddAsync(key, values, flags));
    public bool SortedSetAdd(RedisKey key, RedisValue member, double score, When when, CommandFlags flags) => Wait(SortedSetAddAsync(key, member, score, when, flags));
    public bool SortedSetAdd(RedisKey key, RedisValue member, double score, SortedSetWhen when, CommandFlags flags) => Wait(SortedSetAddAsync(key, member, score, when, flags));
    public long SortedSetAdd(RedisKey key, RedisSortedSetEntry[] values, When when, CommandFlags flags) => Wait(SortedSetAddAsync(key, values, when, flags));
    public long SortedSetAdd(RedisKey key, RedisSortedSetEntry[] values, SortedSetWhen when, CommandFlags flags) => Wait(SortedSetAddAsync(key, values, when, flags));
    public bool SortedSetRemove(RedisKey key, RedisValue member, CommandFlags flags) => Wait(SortedSetRemoveAsync(key, member, flags));
    public long SortedSetRemove(RedisKey key, RedisValue[] members, CommandFlags flags) => Wait(SortedSetRemoveAsync(key, members, flags));
    public long SortedSetLength(RedisKey key, double min, double max, Exclude exclude, CommandFlags flags) => Wait(SortedSetLengthAsync(key, min, max, exclude, flags));
    public RedisValue[] SortedSetRangeByRank(RedisKey key, long start, long stop, Order order, CommandFlags flags) => Wait(SortedSetRangeByRankAsync(key, start, stop, order, flags));
    public RedisSortedSetEntry[] SortedSetRangeByRankWithScores(RedisKey key, long start, long stop, Order order, CommandFlags flags) => Wait(SortedSetRangeByRankWithScoresAsync(key, start, stop, order, flags));
    public RedisValue[] SortedSetRangeByScore(RedisKey key, double start, double stop, Exclude exclude, Order order, long skip, long take, CommandFlags flags) => Wait(SortedSetRangeByScoreAsync(key, start, stop, exclude, order, skip, take, flags));
    public RedisSortedSetEntry[] SortedSetRangeByScoreWithScores(RedisKey key, double start, double stop, Exclude exclude, Order order, long skip, long take, CommandFlags flags) => Wait(SortedSetRangeByScoreWithScoresAsync(key, start, stop, exclude, order, skip, take, flags));
    public IEnumerable<RedisSortedSetEntry> SortedSetScan(RedisKey key, RedisValue pattern, int pageSize, CommandFlags flags) => Scan(key, pattern, pageSize, 0, 0, flags);
    public IEnumerable<RedisSortedSetEntry> SortedSetScan(RedisKey key, RedisValue pattern, int pageSize, long cursor, int pageOffset, CommandFlags flags) => Scan(key, pattern, pageSize, cursor, pageOffset, flags);
}
