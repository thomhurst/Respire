using System.Net;
using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

internal abstract partial class CompatDatabaseAsync
{
    private const string ExtendLock = "if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE',KEYS[1],ARGV[2]) else return 0 end";
    private const string ReleaseLock = "if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('DEL',KEYS[1]) else return 0 end";
    private static long LockMilliseconds(TimeSpan expiry)
    {
        if (expiry <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiry), "Lock expiry must be positive.");
        return Math.Max(1, expiry.Ticks / TimeSpan.TicksPerMillisecond);
    }
    public Task<bool> LockTakeAsync(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags)
        => Send(RespireCommands.String.SET, [Key(key), Value(value), "PX", LockMilliseconds(expiry), "NX"], flags, static result => !result.IsNull);
    public Task<bool> LockExtendAsync(RedisKey key, RedisValue value, TimeSpan expiry, CommandFlags flags)
        => Send(RespireCommands.Scripting.EVAL, [ExtendLock, 1, Key(key), Value(value), LockMilliseconds(expiry)], flags, static result => (long)result != 0);
    public Task<bool> LockReleaseAsync(RedisKey key, RedisValue value, CommandFlags flags)
        => Send(RespireCommands.Scripting.EVAL, [ReleaseLock, 1, Key(key), Value(value)], flags, static result => (long)result != 0);
    public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags)
    {
        if (this is not CompatDatabase) throw Compatibility.Unsupported("Publish on a batch; use the database directly");
        if (flags is not (CommandFlags.None or CommandFlags.DemandMaster))
            throw Compatibility.Unsupported($"Publish CommandFlags {flags}; use None or DemandMaster");
        if (channel.IsPattern) throw Compatibility.Unsupported("publishing a pattern channel");
        byte[]? bytes = channel;
        ArgumentNullException.ThrowIfNull(bytes, nameof(channel));
        var nativeChannel = new RespireChannel(bytes.AsMemory());
        var nativeValue = Value(message).ToRespireValue();
        return DatabaseOwner.Owner.Run(token => DatabaseOwner.Client.PublishAsync(nativeChannel, nativeValue, token).AsTask());
    }
    public Task<EndPoint?> IdentifyEndpointAsync(RedisKey key, CommandFlags flags)
    {
        if (this is not CompatDatabase) throw Compatibility.Unsupported("IdentifyEndpoint on a batch; use the database directly");
        if ((flags & ~(CommandFlags.DemandMaster | CommandFlags.NoRedirect)) != 0)
            throw Compatibility.Unsupported($"IdentifyEndpoint CommandFlags {flags}; only primary endpoint selection is supported");
        byte[]? bytes = key;
        var slot = bytes is null ? (int?)null : DatabaseOwner.Client.ResolveKey(new RespireKey(bytes)).ClusterSlot;
        return DatabaseOwner.Owner.Run<EndPoint?>(async token =>
        {
            var connection = await DatabaseOwner.Client.AcquireConnectionAsync(slot, token).ConfigureAwait(false);
            return RespireConnectionMultiplexer.ToEndPoint(new(connection.Host, connection.Port));
        });
    }
}
