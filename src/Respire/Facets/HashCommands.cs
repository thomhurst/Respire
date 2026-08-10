using Respire.Commands;

namespace Respire;

/// <summary>
/// Hash (field → value map) commands. Collection cardinality uses <see cref="CountAsync"/>.
/// </summary>
public interface IHashCommands
{
    /// <summary>Sets one field. Returns true when the field was newly created. Redis: HSET.</summary>
    ValueTask<bool> SetAsync(RespireKey key, string field, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets one field to a serialized <typeparamref name="T"/>; the write partner of
    /// <see cref="GetAsync{T}"/>. Returns true when the field was newly created. Redis: HSET.
    /// <para>
    /// Overload resolution mirrors <see cref="IStringCommands.SetAsync{T}"/>:
    /// an argument already typed as <see cref="RespireValue"/> picks the non-generic overload,
    /// while any other type (including <c>string</c>, whose implicit conversion loses to an exact
    /// match) picks this one. The two write identical bytes for strings, byte payloads, and
    /// numbers. <c>bool</c> is the exception — this overload writes <c>true</c>/<c>false</c> like
    /// every other typed write, a <see cref="RespireValue"/> writes Redis-native <c>1</c>/<c>0</c>,
    /// and <see cref="GetAsync{T}"/> reads both.
    /// </para>
    /// </summary>
    ValueTask<bool> SetAsync<T>(RespireKey key, string field, T value, CancellationToken cancellationToken = default);

    /// <summary>Sets many fields in one round trip; returns how many were newly created. Redis: HSET.</summary>
    ValueTask<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets many fields in one round trip; returns how many were newly created. Redis: HSET.</summary>
    ValueTask<long> SetAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Gets a field as a string, or null when missing. Redis: HGET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets a field deserialized as <typeparamref name="T"/>. Redis: HGET.</summary>
    ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a field deserialized as <typeparamref name="T"/>, reporting presence separately so a
    /// missing field is distinguishable from a stored <c>default(T)</c>. Redis: HGET.
    /// </summary>
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets a field's raw bytes, or null when missing. Redis: HGET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    ValueTask<string?[]> GetManyAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>The whole hash as a dictionary. Redis: HGETALL.</summary>
    ValueTask<Dictionary<string, string>> GetAllAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Deletes fields; returns how many existed. Redis: HDEL.</summary>
    ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Deletes fields; returns how many existed. Redis: HDEL.</summary>
    ValueTask<long> DeleteAsync(RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Whether the field exists. Redis: HEXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>Number of fields in the hash. Redis: HLEN.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds to a numeric field and returns the new value. Redis: HINCRBY.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, string field, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds a floating-point delta to a field. Redis: HINCRBYFLOAT.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, string field, double by, CancellationToken cancellationToken = default);

    /// <summary>All field names. Redis: HKEYS.</summary>
    ValueTask<string[]> FieldsAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>All values. Redis: HVALS.</summary>
    ValueTask<string[]> ValuesAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    ValueTask<HashFieldExpiry[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    ValueTask<HashFieldExpiry[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets field TTLs using millisecond precision. Redis: HPEXPIRE.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets field TTLs using millisecond precision. Redis: HPEXPIRE.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets field TTLs with an NX, XX, GT, or LT condition. Redis: HPEXPIRE.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Sets field TTLs with an NX, XX, GT, or LT condition. Redis: HPEXPIRE.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken);

    /// <summary>Sets absolute field expiry instants using Unix milliseconds. Redis: HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields);

    /// <summary>Sets absolute field expiry instants using Unix milliseconds. Redis: HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets absolute field expiry instants with an NX, XX, GT, or LT condition. Redis: HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Sets absolute field expiry instants with an NX, XX, GT, or LT condition. Redis: HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken);

    /// <summary>Removes field expiry metadata. Redis: HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> PersistAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Removes field expiry metadata. Redis: HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> PersistAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    ValueTask<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    ValueTask<string?[]> GetDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>
    /// Gets fields and sets their TTL using PX milliseconds. Use <see cref="RespireCommands.Hash.HGETEX"/>
    /// directly for second-precision EX/EXAT forms. Redis: HGETEX.
    /// </summary>
    ValueTask<string?[]> GetExpireAsync(RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and sets their TTL using PX milliseconds. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetExpireAsync(
        RespireKey key, TimeSpan expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Gets fields and sets their absolute expiry using PXAT Unix milliseconds. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetExpireAtAsync(RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and sets their absolute expiry using PXAT Unix milliseconds. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Gets fields and removes their TTL metadata. Redis: HGETEX PERSIST.</summary>
    ValueTask<string?[]> GetPersistAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and removes their TTL metadata. Redis: HGETEX PERSIST.</summary>
    ValueTask<string?[]> GetPersistAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>
    /// Sets fields and applies a TTL using PX milliseconds. Use <see cref="RespireCommands.Hash.HSETEX"/>
    /// directly for second-precision EX/EXAT and KEEPTTL forms. Redis: HSETEX.
    /// </summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields and applies a TTL using PX milliseconds. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Sets fields with a FNX/FXX condition and applies a TTL using PX milliseconds. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies a TTL using PX milliseconds. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Sets fields and applies an absolute expiry using PXAT Unix milliseconds. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields and applies an absolute expiry using PXAT Unix milliseconds. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Sets fields with a FNX/FXX condition and applies an absolute expiry using PXAT. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies an absolute expiry using PXAT. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);
}

internal sealed class HashCommands(RespireClient client) : IHashCommands
{
    public ValueTask<bool> SetAsync(RespireKey key, string field, RespireValue value, CancellationToken cancellationToken = default)
        => client.FlagAsync("HSET", new Cmd3(Verbs.HSet, client.Key(in key), field, value), cancellationToken);

    public ValueTask<bool> SetAsync<T>(RespireKey key, string field, T value, CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "HSET",
            new Cmd3(Verbs.HSet, client.Key(in key), field, client.SerializeRawCompatible(value)),
            cancellationToken);

    public ValueTask<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetAsync(key, fields, CancellationToken.None);

    public ValueTask<long> SetAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => client.IntegerAsync(
            "HSET", new Cmd1N(Verbs.HSet, client.Key(in key), FieldValuePairs(fields)), cancellationToken);

    public ValueTask<string?> GetStringAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.TryDeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    public ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetManyAsync(key, fields, CancellationToken.None);

    public ValueTask<string?[]> GetManyAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HMGET", new Cmd1N(Verbs.HMGet, client.Key(in key), ToValues(fields)), cancellationToken);

    public ValueTask<Dictionary<string, string>> GetAllAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringMapAsync("HGETALL", new Cmd1(Verbs.HGetAll, client.Key(in key)), cancellationToken);

    public ValueTask<long> DeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => DeleteAsync(key, fields, CancellationToken.None);

    public ValueTask<long> DeleteAsync(RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.IntegerAsync("HDEL", new Cmd1N(Verbs.HDel, client.Key(in key), ToValues(fields)), cancellationToken);

    public ValueTask<bool> ExistsAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.FlagAsync("HEXISTS", new Cmd2(Verbs.HExists, client.Key(in key), field), cancellationToken);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("HLEN", new Cmd1(Verbs.HLen, client.Key(in key)), cancellationToken);

    public ValueTask<long> IncrementAsync(RespireKey key, string field, long by = 1, CancellationToken cancellationToken = default)
        => client.IntegerAsync("HINCRBY", new Cmd3(Verbs.HIncrBy, client.Key(in key), field, by), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, string field, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("HINCRBYFLOAT", new Cmd3(Verbs.HIncrByFloat, client.Key(in key), field, by), cancellationToken);

    public ValueTask<string[]> FieldsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("HKEYS", new Cmd1(Verbs.HKeys, client.Key(in key)), cancellationToken);

    public ValueTask<string[]> ValuesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringArrayAsync("HVALS", new Cmd1(Verbs.HVals, client.Key(in key)), cancellationToken);

    public ValueTask<HashFieldExpiry[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => ExpiryAsync(key, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiry[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.HashFieldExpiryArrayAsync(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields)
        => ExpireAsync(key, expiry, HashFieldExpireWhen.Always, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => ExpireAsync(key, expiry, HashFieldExpireWhen.Always, fields, cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, TimeSpan expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
        => ExpireAsync(key, expiry, when, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
        => client.HashFieldExpiryResultArrayAsync(
            "HPEXPIRE",
            new Cmd1N(
                RespireCommands.Hash.HPEXPIRE.Verb,
                client.Key(in key),
                ExpireFieldsBlock((long)expiry.TotalMilliseconds, when, fields)),
            cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields)
        => ExpireAtAsync(key, expireAt, HashFieldExpireWhen.Always, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => ExpireAtAsync(key, expireAt, HashFieldExpireWhen.Always, fields, cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
        => ExpireAtAsync(key, expireAt, when, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
        => client.HashFieldExpiryResultArrayAsync(
            "HPEXPIREAT",
            new Cmd1N(
                RespireCommands.Hash.HPEXPIREAT.Verb,
                client.Key(in key),
                ExpireFieldsBlock(expireAt.ToUnixTimeMilliseconds(), when, fields)),
            cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> PersistAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => PersistAsync(key, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> PersistAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.HashFieldExpiryResultArrayAsync(
            "HPERSIST",
            new Cmd1N(RespireCommands.Hash.HPERSIST.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken);

    public ValueTask<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetDeleteAsync(key, fields, CancellationToken.None);

    public ValueTask<string?[]> GetDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETDEL",
            new Cmd1N(RespireCommands.Hash.HGETDEL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken);

    public ValueTask<string?[]> GetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<string> fields)
        => GetExpireAsync(key, expiry, fields, CancellationToken.None);

    public ValueTask<string?[]> GetExpireAsync(
        RespireKey key, TimeSpan expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                client.Key(in key),
                GetExFieldsBlock("PX", (long)expiry.TotalMilliseconds, hasValue: true, fields)),
            cancellationToken);

    public ValueTask<string?[]> GetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<string> fields)
        => GetExpireAtAsync(key, expireAt, fields, CancellationToken.None);

    public ValueTask<string?[]> GetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                client.Key(in key),
                GetExFieldsBlock("PXAT", expireAt.ToUnixTimeMilliseconds(), hasValue: true, fields)),
            cancellationToken);

    public ValueTask<string?[]> GetPersistAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetPersistAsync(key, fields, CancellationToken.None);

    public ValueTask<string?[]> GetPersistAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                client.Key(in key),
                GetExFieldsBlock("PERSIST", optionValue: 0, hasValue: false, fields)),
            cancellationToken);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAsync(key, expiry, SetWhen.Always, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => SetExpireAsync(key, expiry, SetWhen.Always, fields, cancellationToken);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, TimeSpan expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAsync(key, expiry, when, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        TimeSpan expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => client.FlagAsync(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                client.Key(in key),
                SetExFieldsBlock("PX", (long)expiry.TotalMilliseconds, when, fields)),
            cancellationToken);

    public ValueTask<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAtAsync(key, expireAt, SetWhen.Always, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => SetExpireAtAsync(key, expireAt, SetWhen.Always, fields, cancellationToken);

    public ValueTask<bool> SetExpireAtAsync(
        RespireKey key, DateTimeOffset expireAt, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAtAsync(key, expireAt, when, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAtAsync(
        RespireKey key,
        DateTimeOffset expireAt,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => client.FlagAsync(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                client.Key(in key),
                SetExFieldsBlock("PXAT", expireAt.ToUnixTimeMilliseconds(), when, fields)),
            cancellationToken);

    /// <summary>field value… — shared with the deferred (batch/transaction) facet.</summary>
    internal static RespireValue[] FieldValuePairs(ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var args = new RespireValue[fields.Length * 2];
        for (var i = 0; i < fields.Length; i++)
        {
            args[i * 2] = fields[i].Field;
            args[i * 2 + 1] = fields[i].Value;
        }

        return args;
    }

    internal static RespireValue[] ToValues(ReadOnlySpan<string> items)
    {
        var values = new RespireValue[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            values[i] = items[i];
        }

        return values;
    }

    internal static RespireValue[] FieldsBlock(ReadOnlySpan<string> fields)
    {
        ValidateFields(fields);
        var args = new RespireValue[2 + fields.Length];
        args[0] = "FIELDS";
        args[1] = fields.Length;
        for (var i = 0; i < fields.Length; i++)
        {
            args[2 + i] = fields[i];
        }

        return args;
    }

    internal static RespireValue[] ExpireFieldsBlock(
        long milliseconds, HashFieldExpireWhen when, ReadOnlySpan<string> fields)
    {
        ValidateFields(fields);
        var condition = FieldExpireWhenToken(when);
        var args = new RespireValue[1 + (condition is null ? 0 : 1) + 2 + fields.Length];
        var index = 0;
        args[index++] = milliseconds;
        if (condition is not null)
        {
            args[index++] = condition;
        }

        args[index++] = "FIELDS";
        args[index++] = fields.Length;
        for (var i = 0; i < fields.Length; i++)
        {
            args[index++] = fields[i];
        }

        return args;
    }

    internal static RespireValue[] GetExFieldsBlock(
        string option, long optionValue, bool hasValue, ReadOnlySpan<string> fields)
    {
        ValidateFields(fields);
        var args = new RespireValue[1 + (hasValue ? 1 : 0) + 2 + fields.Length];
        var index = 0;
        args[index++] = option;
        if (hasValue)
        {
            args[index++] = optionValue;
        }

        args[index++] = "FIELDS";
        args[index++] = fields.Length;
        for (var i = 0; i < fields.Length; i++)
        {
            args[index++] = fields[i];
        }

        return args;
    }

    internal static RespireValue[] SetExFieldsBlock(
        string option, long optionValue, SetWhen when, ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        ValidateFieldPairs(fields);
        var condition = HashSetWhenToken(when);
        var args = new RespireValue[(condition is null ? 0 : 1) + 4 + fields.Length * 2];
        var index = 0;
        if (condition is not null)
        {
            args[index++] = condition;
        }

        args[index++] = option;
        args[index++] = optionValue;
        args[index++] = "FIELDS";
        args[index++] = fields.Length;
        for (var i = 0; i < fields.Length; i++)
        {
            args[index++] = fields[i].Field;
            args[index++] = fields[i].Value;
        }

        return args;
    }

    private static string? FieldExpireWhenToken(HashFieldExpireWhen when)
        => when switch
        {
            HashFieldExpireWhen.Always => null,
            HashFieldExpireWhen.NotExists => "NX",
            HashFieldExpireWhen.Exists => "XX",
            HashFieldExpireWhen.GreaterThan => "GT",
            HashFieldExpireWhen.LessThan => "LT",
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    private static string? HashSetWhenToken(SetWhen when)
        => when switch
        {
            SetWhen.Always => null,
            SetWhen.NotExists => "FNX",
            SetWhen.Exists => "FXX",
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    private static void ValidateFields(ReadOnlySpan<string> fields)
    {
        if (fields.Length == 0)
        {
            throw new ArgumentException("At least one field is required.", nameof(fields));
        }
    }

    private static void ValidateFieldPairs(ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        if (fields.Length == 0)
        {
            throw new ArgumentException("At least one field/value pair is required.", nameof(fields));
        }
    }
}
