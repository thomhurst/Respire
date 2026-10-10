using System.Net;
using StackExchange.Redis;

#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatDatabase
{
    public bool LockTake(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags) => Wait(LockTakeAsync(key, value, expiry, flags));
    public bool LockExtend(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags) => Wait(LockExtendAsync(key, value, expiry, flags));
    public bool LockRelease(RedisKey key, RedisValue value, CommandFlags flags) => Wait(LockReleaseAsync(key, value, flags));
    public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags) => Wait(PublishAsync(channel, message, flags));
    public EndPoint? IdentifyEndpoint(RedisKey key, CommandFlags flags) => Wait(IdentifyEndpointAsync(key, flags));
}
