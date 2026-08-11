using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Hash (field → value map) commands. Collection cardinality uses <see cref="CountAsync"/>.
/// </summary>
public interface IHashCommands
{
    /// <summary>Sets one field. Returns true when the field was newly created. Redis: HSET.</summary>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets one field. Returns true when an unconditional write creates the field, or when a
    /// conditional write is applied. Redis: HSET/HSETNX/HSETEX.
    /// </summary>
    ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value, SetWhen when,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets one field to a serialized <typeparamref name="T"/>; the write partner of
    /// <see cref="GetAsync{T}"/>. Returns true when an unconditional write creates the field, or
    /// when a conditional write is applied. Redis: HSET/HSETNX/HSETEX.
    /// <para>
    /// Overload resolution mirrors <see cref="IStringCommands.SetAsync{T}"/>:
    /// an argument already typed as <see cref="RespireValue"/> picks the non-generic overload,
    /// while any other type (including <c>string</c>, whose implicit conversion loses to an exact
    /// match) picks this one. The two write identical bytes for strings, byte payloads, numbers,
    /// and booleans. Boolean writes use Redis-native <c>1</c>/<c>0</c>; <see cref="GetAsync{T}"/>
    /// also reads <c>true</c>/<c>false</c> for compatibility with existing data.
    /// </para>
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally sets one field to a serialized <typeparamref name="T"/>. Redis:
    /// HSET/HSETNX/HSETEX.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value, SetWhen when,
        CancellationToken cancellationToken = default);

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
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a field deserialized as <typeparamref name="T"/>, reporting presence separately so a
    /// missing field is distinguishable from a stored <c>default(T)</c>. Redis: HGET.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
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

    /// <summary>The whole hash with values deserialized as <typeparamref name="T"/>. Redis: HGETALL.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<Dictionary<string, T>> GetAllAsync<T>(
        RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Iterates fields and values incrementally. Redis: HSCAN.</summary>
    IAsyncEnumerable<KeyValuePair<string, string>> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default);

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
    ValueTask<RespireTtl[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets, updates, or removes field expiry metadata. Redis: HPEXPIRE/HPEXPIREAT/HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets, updates, or removes field expiry metadata. Redis: HPEXPIRE/HPEXPIREAT/HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets field expiry with an NX, XX, GT, or LT condition. Redis: HPEXPIRE/HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Sets field expiry with an NX, XX, GT, or LT condition. Redis: HPEXPIRE/HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
#pragma warning disable CS0618 // Defaults preserve compatibility with existing interface implementations.
    ValueTask<string?[]> GetAndDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetDeleteAsync(key, fields);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    ValueTask<string?[]> GetAndDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => GetDeleteAsync(key, fields, cancellationToken);
#pragma warning restore CS0618

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    [Obsolete("Use GetAndDeleteAsync.")]
    ValueTask<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    [Obsolete("Use GetAndDeleteAsync.")]
    ValueTask<string?[]> GetDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
#pragma warning disable CS0618 // Defaults preserve compatibility with existing interface implementations.
    ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
        => GetExpireAsync(key, expiry, fields);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => GetExpireAsync(key, expiry, fields, cancellationToken);
#pragma warning restore CS0618

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    [Obsolete("Use GetAndExpireAsync.")]
    ValueTask<string?[]> GetExpireAsync(RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    [Obsolete("Use GetAndExpireAsync.")]
    ValueTask<string?[]> GetExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets fields and applies a relative, absolute, or retained expiry. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields and applies a relative, absolute, or retained expiry. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);

    /// <summary>Sets fields with a FNX/FXX condition and applies an expiry. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies an expiry. Redis: HSETEX.</summary>
    ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken);
}

internal sealed class HashCommands(RespireClient client) : IHashCommands
{
    public ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(key, field, value, SetWhen.Always, cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value, SetWhen when,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(key, field, value, when, cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(
            key, field, client.SerializeRawCompatible(value), SetWhen.Always, cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value, SetWhen when,
        CancellationToken cancellationToken = default)
        => SetCoreAsync(key, field, client.SerializeRawCompatible(value), when, cancellationToken);

    private ValueTask<bool> SetCoreAsync(
        RespireKey key, string field, RespireValue value, SetWhen when, CancellationToken cancellationToken)
        => when switch
        {
            SetWhen.Always => client.FlagAsync(
                "HSET", new Cmd3(Verbs.HSet, client.Key(in key), field, value), cancellationToken),
            SetWhen.NotExists => client.FlagAsync(
                "HSETNX", new Cmd3(Verbs.HSetNx, client.Key(in key), field, value), cancellationToken),
            SetWhen.Exists => client.FlagAsync(
                "HSETEX",
                new Cmd1N(
                    RespireCommands.Hash.HSETEX.Verb,
                    client.Key(in key),
                    SetExFieldsBlock(option: null, 0, hasValue: false, when, [(field, value)])),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

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

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
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

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<Dictionary<string, T>> GetAllAsync<T>(
        RespireKey key, CancellationToken cancellationToken = default)
        => client.DeserializeMapAsync<T, Cmd1>(
            "HGETALL", new Cmd1(Verbs.HGetAll, client.Key(in key)), cancellationToken);

    public IAsyncEnumerable<KeyValuePair<string, string>> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default)
        => CollectionScan.EnumerateAsync(
            client, "HSCAN", RespireCommands.Hash.HSCAN.Verb, key, match, countHint,
            ParseScanEntries, cancellationToken);

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

    private static KeyValuePair<string, string>[] ParseScanEntries(in RespValue page)
    {
        var elements = page.AsArray();
        var entries = new KeyValuePair<string, string>[elements.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new KeyValuePair<string, string>(
                elements[i * 2].AsString(), elements[i * 2 + 1].AsString());
        }

        return entries;
    }

    public ValueTask<RespireTtl[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => ExpiryAsync(key, fields, CancellationToken.None);

    public ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.TtlArrayAsync(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
        => ExpireAsync(key, expiry, HashFieldExpireWhen.Always, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => ExpireAsync(key, expiry, HashFieldExpireWhen.Always, fields, cancellationToken);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
        => ExpireAsync(key, expiry, when, fields, CancellationToken.None);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
    {
        if (expiry.IsPersist)
        {
            if (when != HashFieldExpireWhen.Always)
            {
                throw new ArgumentException("HPERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return client.HashFieldExpiryResultArrayAsync(
                "HPERSIST",
                new Cmd1N(RespireCommands.Hash.HPERSIST.Verb, client.Key(in key), FieldsBlock(fields)),
                cancellationToken);
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return ExpireCore(
                "HPEXPIRE", RespireCommands.Hash.HPEXPIRE.Verb, key, milliseconds, when, fields, cancellationToken);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return ExpireCore(
                "HPEXPIREAT", RespireCommands.Hash.HPEXPIREAT.Verb, key, unixMilliseconds, when, fields, cancellationToken);
        }

        throw new ArgumentException(
            "Hash expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireCore(
        string operation,
        Verb verb,
        RespireKey key,
        long value,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
        => client.HashFieldExpiryResultArrayAsync(
            operation,
            new Cmd1N(verb, client.Key(in key), ExpireFieldsBlock(value, when, fields)),
            cancellationToken);

    public ValueTask<string?[]> GetAndDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetAndDeleteAsync(key, fields, CancellationToken.None);

    public ValueTask<string?[]> GetAndDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETDEL",
            new Cmd1N(RespireCommands.Hash.HGETDEL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken);

    [Obsolete("Use GetAndDeleteAsync.")]
    public ValueTask<string?[]> GetDeleteAsync(RespireKey key, params ReadOnlySpan<string> fields)
        => GetAndDeleteAsync(key, fields);

    [Obsolete("Use GetAndDeleteAsync.")]
    public ValueTask<string?[]> GetDeleteAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => GetAndDeleteAsync(key, fields, cancellationToken);

    public ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
        => GetAndExpireAsync(key, expiry, fields, CancellationToken.None);

    public ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return GetExpireCore(key, "PX", milliseconds, hasValue: true, fields, cancellationToken);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return GetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, fields, cancellationToken);
        }

        if (expiry.IsPersist)
        {
            return GetExpireCore(key, "PERSIST", optionValue: 0, hasValue: false, fields, cancellationToken);
        }

        throw new ArgumentException(
            "HGETEX expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    [Obsolete("Use GetAndExpireAsync.")]
    public ValueTask<string?[]> GetExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
        => GetAndExpireAsync(key, expiry, fields);

    [Obsolete("Use GetAndExpireAsync.")]
    public ValueTask<string?[]> GetExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
        => GetAndExpireAsync(key, expiry, fields, cancellationToken);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAsync(key, expiry, SetWhen.Always, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => SetExpireAsync(key, expiry, SetWhen.Always, fields, cancellationToken);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpireAsync(key, expiry, when, fields, CancellationToken.None);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return SetExpireCore(key, "PX", milliseconds, hasValue: true, when, fields, cancellationToken);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return SetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, when, fields, cancellationToken);
        }

        if (expiry.IsKeep)
        {
            return SetExpireCore(key, "KEEPTTL", optionValue: 0, hasValue: false, when, fields, cancellationToken);
        }

        throw new ArgumentException(
            "HSETEX expiry must be relative, absolute, or RespireExpiry.Keep.", nameof(expiry));
    }

    private ValueTask<string?[]> GetExpireCore(
        RespireKey key,
        string option,
        long optionValue,
        bool hasValue,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
        => client.NullableStringArrayAsync(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                client.Key(in key),
                GetExFieldsBlock(option, optionValue, hasValue, fields)),
            cancellationToken);

    private ValueTask<bool> SetExpireCore(
        RespireKey key,
        string option,
        long optionValue,
        bool hasValue,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
        => client.FlagAsync(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                client.Key(in key),
                SetExFieldsBlock(option, optionValue, hasValue, when, fields)),
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
        string? option,
        long optionValue,
        bool hasValue,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        ValidateFieldPairs(fields);
        var condition = HashSetWhenToken(when);
        var args = new RespireValue[
            (condition is null ? 0 : 1) + (option is null ? 0 : 1) + 2
            + (hasValue ? 1 : 0) + fields.Length * 2];
        var index = 0;
        if (condition is not null)
        {
            args[index++] = condition;
        }

        if (option is not null)
        {
            args[index++] = option;
        }
        if (hasValue)
        {
            args[index++] = optionValue;
        }
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
