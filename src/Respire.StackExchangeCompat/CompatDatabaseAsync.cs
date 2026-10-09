using StackExchange.Redis;
using RedisExpireWhen = StackExchange.Redis.ExpireWhen;

// IRedisAsync also includes synchronous wait helpers used by the official cache.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal abstract partial class CompatDatabaseAsync : IDatabaseAsync
{
    protected CompatDatabaseAsync(CompatDatabase database) => DatabaseOwner = database;
    protected CompatDatabaseAsync() { }
    protected CompatDatabase DatabaseOwner { get; set; } = null!;
    public int Database => DatabaseOwner.Number;
    public IConnectionMultiplexer Multiplexer => DatabaseOwner.Owner;
    protected abstract Task<T> Send<T>(RespireCommand command, RedisValue[] arguments, CommandFlags flags, Func<RedisResult, T> convert);

    protected static RedisValue Key(RedisKey key)
    {
        byte[]? bytes = key;
        ArgumentNullException.ThrowIfNull(bytes, nameof(key));
        return bytes.ToArray();
    }

    protected static RedisValue Value(RedisValue value)
    {
        if (value.IsNull) throw new ArgumentException("Null values cannot be sent as Redis arguments.", nameof(value));
        return ((byte[]?)value)!.ToArray();
    }

    private static RedisValue Scalar(RedisResult result) => result.IsNull ? RedisValue.Null : (byte[]?)result;
    private static RedisValue[] Values(RedisResult result) => ((RedisResult[])result)!.Select(Scalar).ToArray();
    private static Lease<byte>? Lease(RedisResult result)
    {
        if (result.IsNull) return null;
        var bytes = (byte[]?)result;
        var lease = StackExchange.Redis.Lease<byte>.Create(bytes!.Length, clear: false);
        bytes.CopyTo(lease.Memory);
        return lease;
    }

    public Task<RedisValue> HashGetAsync(RedisKey key, RedisValue hashField, CommandFlags flags)
        => Send(RespireCommands.Hash.HGET, [Key(key), Value(hashField)], flags, Scalar);
    public Task<RedisValue[]> HashGetAsync(RedisKey key, RedisValue[] hashFields, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(hashFields);
        if (hashFields.Length == 0) return Task.FromResult(Array.Empty<RedisValue>());
        return Send(RespireCommands.Hash.HMGET, [Key(key), .. hashFields.Select(Value)], flags, Values);
    }
    public Task<Lease<byte>?> HashGetLeaseAsync(RedisKey key, RedisValue hashField, CommandFlags flags)
        => Send(RespireCommands.Hash.HGET, [Key(key), Value(hashField)], flags, Lease);
    public Task HashSetAsync(RedisKey key, HashEntry[] hashFields, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(hashFields);
        if (hashFields.Length == 0) return Task.CompletedTask;
        var arguments = new RedisValue[1 + hashFields.Length * 2];
        arguments[0] = Key(key);
        for (var i = 0; i < hashFields.Length; i++)
        {
            arguments[1 + i * 2] = Value(hashFields[i].Name);
            arguments[2 + i * 2] = Value(hashFields[i].Value);
        }
        return Send(RespireCommands.Hash.HSET, arguments, flags, static result => (long)result);
    }
    public Task<bool> HashSetAsync(RedisKey key, RedisValue hashField, RedisValue value, When when, CommandFlags flags)
    {
        if (when is not (When.Always or When.NotExists)) throw Compatibility.Unsupported("HashSet When.Exists");
        if (value.IsNull) return Send(RespireCommands.Hash.HDEL, [Key(key), Value(hashField)], flags, static result => (long)result != 0);
        return when switch
        {
            When.Always => Send(RespireCommands.Hash.HSET, [Key(key), Value(hashField), Value(value)], flags, static result => (long)result != 0),
            When.NotExists => Send(RespireCommands.Hash.HSETNX, [Key(key), Value(hashField), Value(value)], flags, static result => (long)result != 0),
            _ => throw Compatibility.Unsupported("HashSet When.Exists"),
        };
    }
    public Task<bool> KeyDeleteAsync(RedisKey key, CommandFlags flags)
        => Send(RespireCommands.Key.DEL, [Key(key)], flags, static result => (long)result != 0);
    public Task<long> KeyDeleteAsync(RedisKey[] keys, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0) return Task.FromResult(0L);
        return Send(RespireCommands.Key.DEL, keys.Select(Key).ToArray(), flags, static result => (long)result);
    }
    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, CommandFlags flags)
        => KeyExpireAsync(key, expiry, RedisExpireWhen.Always, flags);
    public Task<bool> KeyExpireAsync(RedisKey key, TimeSpan? expiry, RedisExpireWhen when, CommandFlags flags)
        => Expire(key, expiry is null || expiry == TimeSpan.MaxValue ? null : expiry.Value.Ticks / TimeSpan.TicksPerMillisecond, false, when, flags);
    public Task<bool> KeyExpireAsync(RedisKey key, DateTime? expiry, CommandFlags flags)
        => KeyExpireAsync(key, expiry, RedisExpireWhen.Always, flags);
    public Task<bool> KeyExpireAsync(RedisKey key, DateTime? expiry, RedisExpireWhen when, CommandFlags flags)
    {
        if (expiry is null || expiry == DateTime.MaxValue) return Expire(key, null, true, when, flags);
        if (expiry.Value.Kind == DateTimeKind.Unspecified) throw new ArgumentException("Expiry must have a Local or Utc DateTimeKind.", nameof(expiry));
        var milliseconds = (expiry.Value.ToUniversalTime() - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;
        return Expire(key, milliseconds, true, when, flags);
    }
    private Task<bool> Expire(RedisKey key, long? milliseconds, bool absolute, RedisExpireWhen when, CommandFlags flags)
    {
        if (milliseconds is null)
        {
            if (when != RedisExpireWhen.Always) throw Compatibility.Unsupported("conditional PERSIST");
            return Send(RespireCommands.Key.PERSIST, [Key(key)], flags, static result => (long)result != 0);
        }
        RedisValue[] args = [Key(key), milliseconds.Value];
        var condition = when switch
        {
            RedisExpireWhen.Always => null,
            RedisExpireWhen.HasNoExpiry => "NX",
            RedisExpireWhen.HasExpiry => "XX",
            RedisExpireWhen.GreaterThanCurrentExpiry => "GT",
            RedisExpireWhen.LessThanCurrentExpiry => "LT",
            _ => throw new ArgumentOutOfRangeException(nameof(when)),
        };
        if (condition is not null) args = [.. args, condition];
        return Send(absolute ? RespireCommands.Key.PEXPIREAT : RespireCommands.Key.PEXPIRE, args, flags, static result => (long)result != 0);
    }
    public Task<RedisValue[]> ListRangeAsync(RedisKey key, long start, long stop, CommandFlags flags)
        => Send(RespireCommands.List.LRANGE, [Key(key), start, stop], flags, Values);
    public Task<long> ListRightPushAsync(RedisKey key, RedisValue value, When when, CommandFlags flags)
        => ListRightPushAsync(key, [value], when, flags);
    public Task<long> ListRightPushAsync(RedisKey key, RedisValue[] values, CommandFlags flags)
        => ListRightPushAsync(key, values, When.Always, flags);
    public Task<long> ListRightPushAsync(RedisKey key, RedisValue[] values, When when, CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(values);
        var command = when switch
        {
            When.Always => RespireCommands.List.RPUSH,
            When.Exists => RespireCommands.List.RPUSHX,
            _ => throw Compatibility.Unsupported("ListRightPush When.NotExists"),
        };
        if (values.Length == 0) return Send(RespireCommands.List.LLEN, [Key(key)], flags, static result => (long)result);
        return Send(command, [Key(key), .. values.Select(Value)], flags, static result => (long)result);
    }

    public void Wait(Task task) => DatabaseOwner.Owner.Wait(task);
    public T Wait<T>(Task<T> task) => DatabaseOwner.Owner.Wait(task);
    public void WaitAll(params Task[] tasks) => DatabaseOwner.Owner.WaitAll(tasks);
    public bool TryWait(Task task)
    {
        try { Wait(task); return true; }
        catch (TimeoutException) { return false; }
    }
}
