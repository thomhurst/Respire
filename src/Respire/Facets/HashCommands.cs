using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Hash (field → value map) commands. Collection cardinality uses <see cref="CountAsync"/>.
/// </summary>
public partial interface IHashCommands
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

    /// <summary>Gets a binary field's raw bytes, or null when missing. Redis: HGET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    /// <remarks>With ClientSideCache.ReuseHashFields enabled, only uncached fields are fetched;
    /// hash invalidation evicts all fields. Cached and fetched values can come from different
    /// times; the result is not an atomic hash snapshot.</remarks>
    ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    /// <remarks>With ClientSideCache.ReuseHashFields enabled, only uncached fields are fetched;
    /// hash invalidation evicts all fields. Cached and fetched values can come from different
    /// times; the result is not an atomic hash snapshot.</remarks>
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

    /// <summary>Removes fields; returns how many existed. Redis: HDEL.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Removes fields; returns how many existed. Redis: HDEL.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Whether the named field exists. Redis: HEXISTS.</summary>
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

    /// <summary>Byte length of a field's value, or zero when the key or field is missing. Redis: HSTRLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, string field, CancellationToken cancellationToken = default);

    /// <summary>A random field name, or null when the hash is missing. Redis: HRANDFIELD (Redis 6.2+).</summary>
    ValueTask<string?> RandomFieldAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Random field names. Positive count selects distinct fields; negative count allows repeats. Redis: HRANDFIELD (Redis 6.2+).</summary>
    ValueTask<string[]> RandomFieldsAsync(RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Random field/value pairs, preserving repeated fields for negative count. Redis: HRANDFIELD WITHVALUES (Redis 6.2+).</summary>
    ValueTask<KeyValuePair<string, string>[]> RandomFieldsWithValuesAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default);

    /// <summary>Absolute field expiry times with millisecond resolution. Redis: HPEXPIRETIME (Redis 7.4+).</summary>
    ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Absolute field expiry times with millisecond resolution. Redis: HPEXPIRETIME (Redis 7.4+).</summary>
    ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Absolute field expiry times at the requested resolution. Redis: HEXPIRETIME/HPEXPIRETIME (Redis 7.4+).</summary>
    ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, params ReadOnlySpan<string> fields);

    /// <summary>Absolute field expiry times at the requested resolution. Redis: HEXPIRETIME/HPEXPIRETIME (Redis 7.4+).</summary>
    ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    ValueTask<RespireTtl[]> ExpiryAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Expiry state for binary fields, in milliseconds. Redis: HPTTL (Redis 7.4+).</summary>
    ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<RespireKey> fields, CancellationToken cancellationToken);

    /// <summary>Expiry state for one binary field, in milliseconds. Redis: HPTTL (Redis 7.4+).</summary>
    ValueTask<RespireTtl> ExpiryAsync(RespireKey key, RespireKey field, CancellationToken cancellationToken = default);

    /// <summary>Sets, updates, or removes field expiry metadata. Redis: HPEXPIRE/HPEXPIREAT/HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets, updates, or removes field expiry metadata. Redis: HPEXPIRE/HPEXPIREAT/HPERSIST.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Sets field expiry with an NX, XX, GT, or LT condition. Redis: HPEXPIRE/HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Sets field expiry with an NX, XX, GT, or LT condition. Redis: HPEXPIRE/HPEXPIREAT.</summary>
    ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken);

    /// <summary>Gets fields and removes them atomically. Redis: HGETDEL.</summary>
    ValueTask<string?[]> GetAndRemoveAsync(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and removes them atomically. Redis: HGETDEL.</summary>
    ValueTask<string?[]> GetAndRemoveAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    ValueTask<string?[]> GetAndExpireAsync(
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

internal sealed partial class HashCommands(RespireClient client) : IHashCommands
{
    public ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetBorrowedAsync(key, field, value, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetBorrowedAsync(
        RespireKey key, string field, RespireValue value,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => SetCoreAsync(key, field, value, SetWhen.Always, cancellationToken, observation);

    public ValueTask<bool> SetAsync(
        RespireKey key, string field, RespireValue value, SetWhen when,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetBorrowedAsync(key, field, value, when, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetBorrowedAsync(
        RespireKey key, string field, RespireValue value, SetWhen when,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => SetCoreAsync(key, field, value, when, cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetBorrowedAsync<T>(key, field, value, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<bool> SetBorrowedAsync<T>(
        RespireKey key, string field, T value,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => SetCoreAsync(
            key, field, client.SerializeRawCompatible(value), SetWhen.Always, cancellationToken, observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key, string field, T value, SetWhen when,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetBorrowedAsync<T>(key, field, value, when, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<bool> SetBorrowedAsync<T>(
        RespireKey key, string field, T value, SetWhen when,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => SetCoreAsync(key, field, client.SerializeRawCompatible(value), when, cancellationToken, observation);

    private ValueTask<bool> SetCoreAsync(
        RespireKey key, string field, RespireValue value, SetWhen when, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => when switch
        {
            SetWhen.Always => client.FlagAsync(
                "HSET", new Cmd3(Verbs.HSet, client.Key(in key), field, value), cancellationToken, observation: observation),
            SetWhen.NotExists => client.FlagAsync(
                "HSETNX", new Cmd3(Verbs.HSetNx, client.Key(in key), field, value), cancellationToken, observation: observation),
            SetWhen.Exists => client.FlagAsync(
                "HSETEX",
                new Cmd1N(
                    RespireCommands.Hash.HSETEX.Verb,
                    client.Key(in key),
                    SetExFieldsBlock(option: null, 0, hasValue: false, when, [(field, value)])),
                cancellationToken, observation: observation),
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    public ValueTask<long> SetAsync(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(SetBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> SetBorrowedAsync(RespireKey key, ReadOnlySpan<(string Field, RespireValue Value)> fields, RespireTelemetry.ErrorObservation observation)
        => SetBorrowedAsync(key, fields, CancellationToken.None, observation);

    public ValueTask<long> SetAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(SetBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> SetBorrowedAsync(
        RespireKey key,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync(
            "HSET", new Cmd1N(Verbs.HSet, client.Key(in key), FieldValuePairs(fields)), cancellationToken, observation: observation);

    public ValueTask<string?> GetStringAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(GetStringBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> GetStringBorrowedAsync(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<T?>.Start();
        try { return owner.Attach(GetBorrowedAsync<T>(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> GetBorrowedAsync<T>(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireGet<T>>.Start();
        try { return owner.Attach(TryGetBorrowedAsync<T>(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<RespireGet<T>> TryGetBorrowedAsync<T>(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.TryDeserializeAsync<T, Cmd2>("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken, observation: observation);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<byte[]?>.Start();
        try { return owner.Attach(GetBytesBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<byte[]?> GetBytesBorrowedAsync(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.BytesOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), field), cancellationToken, observation: observation);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<byte[]?>.Start();
        try { return owner.Attach(GetBytesBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<byte[]?> GetBytesBorrowedAsync(RespireKey key, RespireKey field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var fieldSnapshot = field.Snapshot();
        return client.BytesOrNullAsync("HGET", new Cmd2(Verbs.HGet, client.Key(in key), fieldSnapshot.AsValue()), cancellationToken, observation: observation);
    }

    public ValueTask<string?[]> GetManyAsync(RespireKey key, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetManyBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetManyBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => GetManyBorrowedAsync(key, fields, CancellationToken.None, observation);

    public ValueTask<string?[]> GetManyAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetManyBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetManyBorrowedAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.NullableStringArrayAsync(
            "HMGET", new Cmd1N(Verbs.HMGet, client.Key(in key), ToValues(fields)), cancellationToken, observation: observation);

    public ValueTask<Dictionary<string, string>> GetAllAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<Dictionary<string, string>>.Start();
        try { return owner.Attach(GetAllBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<Dictionary<string, string>> GetAllBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringMapAsync("HGETALL", new Cmd1(Verbs.HGetAll, client.Key(in key)), cancellationToken, observation: observation);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<Dictionary<string, T>> GetAllAsync<T>(
        RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<Dictionary<string, T>>.Start();
        try { return owner.Attach(GetAllBorrowedAsync<T>(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<Dictionary<string, T>> GetAllBorrowedAsync<T>(
        RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DeserializeMapAsync<T, Cmd1>(
            "HGETALL", new Cmd1(Verbs.HGetAll, client.Key(in key)), cancellationToken, observation: observation);

    public IAsyncEnumerable<KeyValuePair<string, string>> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default)
        => CollectionScan.EnumerateAsync(
            client, "HSCAN", RespireCommands.Hash.HSCAN.Verb, key, match, countHint,
            ParseScanEntries, cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RemoveBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RemoveBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => RemoveBorrowedAsync(key, fields, CancellationToken.None, observation);

    public ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(RemoveBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> RemoveBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("HDEL", new Cmd1N(Verbs.HDel, client.Key(in key), ToValues(fields)), cancellationToken, observation: observation);

    public ValueTask<bool> ExistsAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(ExistsBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> ExistsBorrowedAsync(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.FlagAsync("HEXISTS", new Cmd2(Verbs.HExists, client.Key(in key), field), cancellationToken, observation: observation);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(CountBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> CountBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("HLEN", new Cmd1(Verbs.HLen, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<long> IncrementAsync(RespireKey key, string field, long by = 1, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(IncrementBorrowedAsync(key, field, by, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> IncrementBorrowedAsync(RespireKey key, string field, long by, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("HINCRBY", new Cmd3(Verbs.HIncrBy, client.Key(in key), field, by), cancellationToken, observation: observation);

    public ValueTask<double> IncrementAsync(RespireKey key, string field, double by, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<double>.Start();
        try { return owner.Attach(IncrementBorrowedAsync(key, field, by, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<double> IncrementBorrowedAsync(RespireKey key, string field, double by, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.DoubleAsync("HINCRBYFLOAT", new Cmd3(Verbs.HIncrByFloat, client.Key(in key), field, by), cancellationToken, observation: observation);

    public ValueTask<string[]> FieldsAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(FieldsBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> FieldsBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringArrayAsync("HKEYS", new Cmd1(Verbs.HKeys, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<string[]> ValuesAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(ValuesBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> ValuesBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringArrayAsync("HVALS", new Cmd1(Verbs.HVals, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<long> LengthAsync(RespireKey key, string field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(LengthBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> LengthBorrowedAsync(RespireKey key, string field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerAsync("HSTRLEN",
            new Cmd2(RespireCommands.Hash.HSTRLEN.Verb, client.Key(in key), field), cancellationToken, observation: observation);

    public ValueTask<string?> RandomFieldAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(RandomFieldBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> RandomFieldBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringOrNullAsync("HRANDFIELD",
            new Cmd1(RespireCommands.Hash.HRANDFIELD.Verb, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<string[]> RandomFieldsAsync(RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string[]>.Start();
        try { return owner.Attach(RandomFieldsBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string[]> RandomFieldsBorrowedAsync(RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringArrayAsync("HRANDFIELD",
            new Cmd2(RespireCommands.Hash.HRANDFIELD.Verb, client.Key(in key), count), cancellationToken, observation: observation);

    public ValueTask<KeyValuePair<string, string>[]> RandomFieldsWithValuesAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<KeyValuePair<string, string>[]>.Start();
        try { return owner.Attach(RandomFieldsWithValuesBorrowedAsync(key, count, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<KeyValuePair<string, string>[]> RandomFieldsWithValuesBorrowedAsync(
        RespireKey key, long count, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync("HRANDFIELD",
            new Cmd3(RespireCommands.Hash.HRANDFIELD.Verb, client.Key(in key), count, "WITHVALUES"),
            cancellationToken, this,
            static (HashCommands _, in RespValue value) => ParseRandomPairs(in value), observation: observation);

    // Params spans must be last, so each default/explicit-precision form has a separate
    // span-plus-token overload. Keep all four forms for variadic calls and cancellation.
    public ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(RespireKey key, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<RespireExpiryTime[]>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime[]> ExpiryTimeBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => ExpiryTimeBorrowedAsync(key, ExpiryTimePrecision.Milliseconds, fields, CancellationToken.None, observation);

    public ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<RespireExpiryTime[]>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime[]> ExpiryTimeBorrowedAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => ExpiryTimeBorrowedAsync(key, ExpiryTimePrecision.Milliseconds, fields, cancellationToken, observation);

    public ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<RespireExpiryTime[]>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, precision, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime[]> ExpiryTimeBorrowedAsync(
        RespireKey key, ExpiryTimePrecision precision, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => ExpiryTimeBorrowedAsync(key, precision, fields, CancellationToken.None, observation);

    public ValueTask<RespireExpiryTime[]> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<RespireExpiryTime[]>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, precision, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime[]> ExpiryTimeBorrowedAsync(
        RespireKey key, ExpiryTimePrecision precision, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var (operation, verb) = ExpiryTimeCommand(precision);
        return client.ConvertResponseAsync(operation,
            new Cmd1N(verb, client.Key(in key), FieldsBlock(fields)), cancellationToken, precision,
            static (ExpiryTimePrecision p, in RespValue value) => ParseExpiryTimes(in value, p), observation: observation);
    }

    internal static (string Operation, Verb Verb) ExpiryTimeCommand(ExpiryTimePrecision precision)
        => precision switch
        {
            ExpiryTimePrecision.Milliseconds => ("HPEXPIRETIME", RespireCommands.Hash.HPEXPIRETIME.Verb),
            ExpiryTimePrecision.Seconds => ("HEXPIRETIME", RespireCommands.Hash.HEXPIRETIME.Verb),
            _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, null),
        };

    internal static RespireExpiryTime[] ParseExpiryTimes(in RespValue value, ExpiryTimePrecision precision)
    {
        var elements = value.AsArray();
        var result = new RespireExpiryTime[elements.Length];
        for (var i = 0; i < elements.Length; i++)
        {
            result[i] = RespireExpiryTime.FromRedis(elements[i].AsInteger(), precision);
        }
        return result;
    }

    internal static KeyValuePair<string, string>[] ParseRandomPairs(in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length == 0)
        {
            return [];
        }

        // RESP3 returns nested pairs; RESP2 returns a flat field/value array.
        var nested = elements[0].Type == RespDataType.Array;
        if (!nested && elements.Length % 2 != 0)
        {
            throw new RespireProtocolException("Expected complete field/value pairs from HRANDFIELD.");
        }
        var result = new KeyValuePair<string, string>[nested ? elements.Length : elements.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var pair = nested ? elements[i].AsArray() : elements.Slice(i * 2, 2);
            if (pair.Length != 2)
            {
                throw new RespireProtocolException("Expected two elements per HRANDFIELD pair.");
            }
            result[i] = new KeyValuePair<string, string>(pair[0].AsString(), pair[1].AsString());
        }
        return result;
    }

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
    {
        var owner = DispatchResponseSource<RespireTtl[]>.Start();
        try { return owner.Attach(ExpiryBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireTtl[]> ExpiryBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => ExpiryBorrowedAsync(key, fields, CancellationToken.None, observation);

    public ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<RespireTtl[]>.Start();
        try { return owner.Attach(ExpiryBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireTtl[]> ExpiryBorrowedAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.TtlArrayAsync(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken, observation: observation);

    public ValueTask<RespireTtl[]> ExpiryAsync(
        RespireKey key, ReadOnlySpan<RespireKey> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<RespireTtl[]>.Start();
        try { return owner.Attach(ExpiryBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireTtl[]> ExpiryBorrowedAsync(
        RespireKey key, ReadOnlySpan<RespireKey> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (fields.IsEmpty) throw new ArgumentException("At least one hash field is required.", nameof(fields));
        var fieldSnapshots = new RespireKey[fields.Length];
        for (var i = 0; i < fields.Length; i++) fieldSnapshots[i] = fields[i].Snapshot();
        return client.TtlArrayAsync(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, client.Key(in key), FieldsBlock(fieldSnapshots)),
            cancellationToken, observation: observation);
    }

    public ValueTask<RespireTtl> ExpiryAsync(
        RespireKey key, RespireKey field, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireTtl>.Start();
        try { return owner.Attach(ExpiryBorrowedAsync(key, field, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireTtl> ExpiryBorrowedAsync(
        RespireKey key, RespireKey field, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => await client.SingleTtlArrayAsync(
            "HPTTL", new Cmd1N(RespireCommands.Hash.HPTTL.Verb, client.Key(in key), FieldsBlock([field])),
            cancellationToken, observation: observation).ConfigureAwait(false);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<HashFieldExpiryResult[]>.Start();
        try { return owner.Attach(ExpireBorrowedAsync(key, expiry, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => ExpireBorrowedAsync(key, expiry, ExpireWhen.Always, fields, CancellationToken.None, observation);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<HashFieldExpiryResult[]>.Start();
        try { return owner.Attach(ExpireBorrowedAsync(key, expiry, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => ExpireBorrowedAsync(key, expiry, ExpireWhen.Always, fields, cancellationToken, observation);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key, RespireExpiry expiry, ExpireWhen when, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<HashFieldExpiryResult[]>.Start();
        try { return owner.Attach(ExpireBorrowedAsync(key, expiry, when, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ExpireWhen when, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => ExpireBorrowedAsync(key, expiry, when, fields, CancellationToken.None, observation);

    public ValueTask<HashFieldExpiryResult[]> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<HashFieldExpiryResult[]>.Start();
        try { return owner.Attach(ExpireBorrowedAsync(key, expiry, when, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireBorrowedAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (expiry.IsPersist)
        {
            if (when != ExpireWhen.Always)
            {
                throw new ArgumentException("HPERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return client.HashFieldExpiryResultArrayAsync(
                "HPERSIST",
                new Cmd1N(RespireCommands.Hash.HPERSIST.Verb, client.Key(in key), FieldsBlock(fields)),
                cancellationToken, observation: observation);
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return ExpireCore(
                "HPEXPIRE", RespireCommands.Hash.HPEXPIRE.Verb, key, milliseconds, when, fields, cancellationToken, observation);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return ExpireCore(
                "HPEXPIREAT", RespireCommands.Hash.HPEXPIREAT.Verb, key, unixMilliseconds, when, fields, cancellationToken, observation);
        }

        throw new ArgumentException(
            "Hash expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    private ValueTask<HashFieldExpiryResult[]> ExpireCore(
        string operation,
        Verb verb,
        RespireKey key,
        long value,
        ExpireWhen when,
        ReadOnlySpan<string> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.HashFieldExpiryResultArrayAsync(
            operation,
            new Cmd1N(verb, client.Key(in key), ExpireFieldsBlock(value, when, fields)),
            cancellationToken, observation: observation);

    public ValueTask<string?[]> GetAndRemoveAsync(RespireKey key, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetAndRemoveBorrowedAsync(key, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetAndRemoveBorrowedAsync(RespireKey key, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => GetAndRemoveBorrowedAsync(key, fields, CancellationToken.None, observation);

    public ValueTask<string?[]> GetAndRemoveAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetAndRemoveBorrowedAsync(key, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetAndRemoveBorrowedAsync(
        RespireKey key, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.NullableStringArrayAsync(
            "HGETDEL",
            new Cmd1N(RespireCommands.Hash.HGETDEL.Verb, client.Key(in key), FieldsBlock(fields)),
            cancellationToken, observation: observation);

    public ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetAndExpireBorrowedAsync(key, expiry, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetAndExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, RespireTelemetry.ErrorObservation observation)
        => GetAndExpireBorrowedAsync(key, expiry, fields, CancellationToken.None, observation);

    public ValueTask<string?[]> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<string?[]>.Start();
        try { return owner.Attach(GetAndExpireBorrowedAsync(key, expiry, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?[]> GetAndExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<string> fields, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return GetExpireCore(key, "PX", milliseconds, hasValue: true, fields, cancellationToken, observation);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return GetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, fields, cancellationToken, observation);
        }

        if (expiry.IsPersist)
        {
            return GetExpireCore(key, "PERSIST", optionValue: 0, hasValue: false, fields, cancellationToken, observation);
        }

        throw new ArgumentException(
            "HGETEX expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetExpireBorrowedAsync(key, expiry, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, ReadOnlySpan<(string Field, RespireValue Value)> fields, RespireTelemetry.ErrorObservation observation)
        => SetExpireBorrowedAsync(key, expiry, SetWhen.Always, fields, CancellationToken.None, observation);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetExpireBorrowedAsync(key, expiry, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetExpireBorrowedAsync(
        RespireKey key,
        RespireExpiry expiry,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => SetExpireBorrowedAsync(key, expiry, SetWhen.Always, fields, cancellationToken, observation);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key, RespireExpiry expiry, SetWhen when, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetExpireBorrowedAsync(key, expiry, when, fields, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetExpireBorrowedAsync(
        RespireKey key, RespireExpiry expiry, SetWhen when, ReadOnlySpan<(string Field, RespireValue Value)> fields, RespireTelemetry.ErrorObservation observation)
        => SetExpireBorrowedAsync(key, expiry, when, fields, CancellationToken.None, observation);

    public ValueTask<bool> SetExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(SetExpireBorrowedAsync(key, expiry, when, fields, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> SetExpireBorrowedAsync(
        RespireKey key,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return SetExpireCore(key, "PX", milliseconds, hasValue: true, when, fields, cancellationToken, observation);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return SetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, when, fields, cancellationToken, observation);
        }

        if (expiry.IsKeep)
        {
            return SetExpireCore(key, "KEEPTTL", optionValue: 0, hasValue: false, when, fields, cancellationToken, observation);
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
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.NullableStringArrayAsync(
            "HGETEX",
            new Cmd1N(
                RespireCommands.Hash.HGETEX.Verb,
                client.Key(in key),
                GetExFieldsBlock(option, optionValue, hasValue, fields)),
            cancellationToken, observation: observation);

    private ValueTask<bool> SetExpireCore(
        RespireKey key,
        string option,
        long optionValue,
        bool hasValue,
        SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.FlagAsync(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                client.Key(in key),
                SetExFieldsBlock(option, optionValue, hasValue, when, fields)),
            cancellationToken, observation: observation);

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

    internal static RespireValue[] FieldsBlock(ReadOnlySpan<RespireKey> fields)
    {
        if (fields.IsEmpty) throw new ArgumentException("At least one hash field is required.", nameof(fields));
        var args = new RespireValue[fields.Length + 2];
        args[0] = "FIELDS";
        args[1] = fields.Length;
        for (var i = 0; i < fields.Length; i++) args[i + 2] = fields[i].Snapshot().AsValue();
        return args;
    }

    internal static RespireValue[] ExpireFieldsBlock(
        long milliseconds, ExpireWhen when, ReadOnlySpan<string> fields)
    {
        ValidateFields(fields);
        var condition = KeyCommands.ExpireWhenToken(when);
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
