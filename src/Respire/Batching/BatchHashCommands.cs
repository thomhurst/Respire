using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Hash (field → value map) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IHashCommands"/>; collection cardinality
/// uses <see cref="CountAsync"/>.
/// </summary>
public interface IBatchHashCommands
{
    /// <summary>Sets one field. True when the field was newly created. Redis: HSET.</summary>
    RespirePending<bool> SetAsync(RespireKey key, string field, RespireValue value);

    /// <summary>Sets many fields; returns how many were newly created. Redis: HSET.</summary>
    RespirePending<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Gets a field as a string, or null when missing. Redis: HGET.</summary>
    RespirePending<string?> GetStringAsync(RespireKey key, string field);

    /// <summary>Gets a field deserialized as <typeparamref name="T"/>. Redis: HGET.</summary>
    RespirePending<T?> GetAsync<T>(RespireKey key, string field);

    /// <summary>Gets a field's raw bytes, or null when missing. Redis: HGET.</summary>
    RespirePending<byte[]?> GetBytesAsync(RespireKey key, string field);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    RespirePending<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>The whole hash as a dictionary. Redis: HGETALL.</summary>
    RespirePending<Dictionary<string, string>> GetAllAsync(RespireKey key);

    /// <summary>Deletes fields; returns how many existed. Redis: HDEL.</summary>
    RespirePending<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Whether the field exists. Redis: HEXISTS.</summary>
    RespirePending<bool> ExistsAsync(RespireKey key, string field);

    /// <summary>Number of fields in the hash. Redis: HLEN.</summary>
    RespirePending<long> CountAsync(RespireKey key);

    /// <summary>Atomically adds to a numeric field and returns the new value. Redis: HINCRBY.</summary>
    RespirePending<long> IncrementAsync(RespireKey key, string field, long by = 1);

    /// <summary>Atomically adds a floating-point delta to a field. Redis: HINCRBYFLOAT.</summary>
    RespirePending<double> IncrementAsync(RespireKey key, string field, double by);

    /// <summary>All field names. Redis: HKEYS.</summary>
    RespirePending<string[]> FieldsAsync(RespireKey key);

    /// <summary>All values. Redis: HVALS.</summary>
    RespirePending<string[]> ValuesAsync(RespireKey key);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    RespirePending<RespireTtl[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Sets field TTLs using millisecond precision. Redis: HPEXPIRE.</summary>
    RespirePending<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets field TTLs with an NX, XX, GT, or LT condition. Redis: HPEXPIRE.</summary>
    RespirePending<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Sets absolute field expiry instants using Unix milliseconds. Redis: HPEXPIREAT.</summary>
    RespirePending<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields);

    /// <summary>Sets absolute field expiry instants with an NX, XX, GT, or LT condition. Redis: HPEXPIREAT.</summary>
    RespirePending<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Removes field expiry metadata. Redis: HPERSIST.</summary>
    RespirePending<HashFieldExpiryResult[]> PersistAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    RespirePending<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and sets their TTL using PX milliseconds. Redis: HGETEX.</summary>
    RespirePending<string?[]> GetExpireAsync(RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and sets their absolute expiry using PXAT Unix milliseconds. Redis: HGETEX.</summary>
    RespirePending<string?[]> GetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and removes their TTL metadata. Redis: HGETEX PERSIST.</summary>
    RespirePending<string?[]> GetPersistAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Sets fields and applies a TTL using PX milliseconds. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies a TTL using PX milliseconds. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields and applies an absolute expiry using PXAT Unix milliseconds. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies an absolute expiry using PXAT. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, SetWhen when,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields);
}

internal sealed class BatchHashCommands(IPendingSink sink) : IBatchHashCommands
{
    public RespirePending<bool> SetAsync(RespireKey key, string field, RespireValue value)
        => sink.Add<Cmd3, bool>(
            "HSET", new Cmd3(Verbs.HSet, sink.Client.Key(in key), field, value),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> SetAsync(
        RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, long>(
            "HSET", new Cmd1N(Verbs.HSet, sink.Client.Key(in key), HashCommands.FieldValuePairs(fields)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string?> GetStringAsync(RespireKey key, string field)
        => sink.Add<Cmd2, string?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAsync<T>(RespireKey key, string field)
        => sink.Add<Cmd2, T?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytesAsync(RespireKey key, string field)
        => sink.Add<Cmd2, byte[]?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.BytesOrNull(in v));

    public RespirePending<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, string?[]>(
            "HMGET", new Cmd1N(Verbs.HMGet, sink.Client.Key(in key), HashCommands.ToValues(fields)),
            static (c, v) => ResponseReader.NullableStringArray(in v));

    public RespirePending<Dictionary<string, string>> GetAllAsync(RespireKey key)
        => sink.Add<Cmd1, Dictionary<string, string>>(
            "HGETALL", new Cmd1(Verbs.HGetAll, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringMap(in v));

    public RespirePending<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, long>(
            "HDEL", new Cmd1N(Verbs.HDel, sink.Client.Key(in key), HashCommands.ToValues(fields)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> ExistsAsync(RespireKey key, string field)
        => sink.Add<Cmd2, bool>(
            "HEXISTS", new Cmd2(Verbs.HExists, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> CountAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "HLEN", new Cmd1(Verbs.HLen, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> IncrementAsync(RespireKey key, string field, long by = 1)
        => sink.Add<Cmd3, long>(
            "HINCRBY", new Cmd3(Verbs.HIncrBy, sink.Client.Key(in key), field, by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double> IncrementAsync(RespireKey key, string field, double by)
        => sink.Add<Cmd3, double>(
            "HINCRBYFLOAT", new Cmd3(Verbs.HIncrByFloat, sink.Client.Key(in key), field, by),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<string[]> FieldsAsync(RespireKey key)
        => sink.Add<Cmd1, string[]>(
            "HKEYS", new Cmd1(Verbs.HKeys, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<string[]> ValuesAsync(RespireKey key)
        => sink.Add<Cmd1, string[]>(
            "HVALS", new Cmd1(Verbs.HVals, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<RespireTtl[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, RespireTtl[]>(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
            static (c, v) => ResponseReader.TtlArray(in v));

    public RespirePending<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields)
        => ExpireAsync(key, expiry, HashFieldExpireWhen.Always, fields);

    public RespirePending<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, HashFieldExpiryResult[]>(
            "HPEXPIRE",
            new Cmd1N(
                RespireCommands.Hash.HPEXPIRE.Verb,
                sink.Client.Key(in key),
                HashCommands.ExpireFieldsBlock((long)expiry.TotalMilliseconds, when, fields)),
            static (c, v) => ResponseReader.HashFieldExpiryResultArray(in v));

    public RespirePending<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields)
        => ExpireAtAsync(key, expireAt, HashFieldExpireWhen.Always, fields);

    public RespirePending<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, HashFieldExpiryResult[]>(
            "HPEXPIREAT",
            new Cmd1N(
                RespireCommands.Hash.HPEXPIREAT.Verb,
                sink.Client.Key(in key),
                HashCommands.ExpireFieldsBlock(expireAt.ToUnixTimeMilliseconds(), when, fields)),
            static (c, v) => ResponseReader.HashFieldExpiryResultArray(in v));

    public RespirePending<HashFieldExpiryResult[]> PersistAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, HashFieldExpiryResult[]>(
            "HPERSIST",
            new Cmd1N(RespireCommands.Hash.HPERSIST.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
            static (c, v) => ResponseReader.HashFieldExpiryResultArray(in v));

    public RespirePending<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, string?[]>(
            "HGETDEL",
            new Cmd1N(RespireCommands.Hash.HGETDEL.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
            static (c, v) => ResponseReader.NullableStringArray(in v));

    public RespirePending<string?[]> GetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields)
        => GetExpireCore(key, "PX", (long)expiry.TotalMilliseconds, hasValue: true, fields);

    public RespirePending<string?[]> GetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields)
        => GetExpireCore(key, "PXAT", expireAt.ToUnixTimeMilliseconds(), hasValue: true, fields);

    public RespirePending<string?[]> GetPersistAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetExpireCore(key, "PERSIST", optionValue: 0, hasValue: false, fields);

    public RespirePending<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAsync(key, expiry, SetWhen.Always, fields);

    public RespirePending<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, SetWhen when,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireCore(key, "PX", (long)expiry.TotalMilliseconds, when, fields);

    public RespirePending<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAtAsync(key, expireAt, SetWhen.Always, fields);

    public RespirePending<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, SetWhen when,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireCore(key, "PXAT", expireAt.ToUnixTimeMilliseconds(), when, fields);

    private RespirePending<string?[]> GetExpireCore(
        RespireKey key, string option, long optionValue, bool hasValue, ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, string?[]>(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                sink.Client.Key(in key),
                HashCommands.GetExFieldsBlock(option, optionValue, hasValue, fields)),
            static (c, v) => ResponseReader.NullableStringArray(in v));

    private RespirePending<bool> SetExpireCore(
        RespireKey key, string option, long optionValue, SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, bool>(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                sink.Client.Key(in key),
                HashCommands.SetExFieldsBlock(option, optionValue, when, fields)),
            static (c, v) => ResponseReader.Flag(in v));
}
