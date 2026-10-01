using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>Condition for SET-style writes.</summary>
public enum SetWhen
{
    /// <summary>Unconditional write.</summary>
    Always,

    /// <summary>
    /// Only write when the target does not exist. Maps to NX for string and geo commands,
    /// HSETNX for a single hash field, and FNX for multi-field hash expiry writes.
    /// </summary>
    NotExists,

    /// <summary>
    /// Only write when the target exists. Maps to XX for string and geo commands, and FXX for
    /// hash-field writes.
    /// </summary>
    Exists,
}

/// <summary>
/// String (plain value) commands. Unlike collection facets' <c>CountAsync</c>,
/// <see cref="LengthAsync"/> returns a byte length.
/// </summary>
public partial interface IStringCommands
{
    /// <summary>Gets a key's value as a string, or null when missing. Redis: GET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value deserialized as <typeparamref name="T"/>, or default when missing. Redis: GET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value deserialized as <typeparamref name="T"/>, reporting presence separately
    /// so a missing key is distinguishable from a stored <c>default(T)</c>. Callers can instead
    /// make a value type nullable, such as <c>GetAsync&lt;int?&gt;</c>; this form keeps
    /// <typeparamref name="T"/> non-nullable and exposes an explicit presence flag. Redis: GET.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's raw bytes, or null when missing. Redis: GET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value as a readable stream, or null when missing. The stream holds this
    /// connection's receive path until consumed or disposed, so later replies may wait behind it;
    /// always dispose it. Waiting for the caller to read does not count toward the connection's
    /// idle-read timeout. This path bypasses the client-side value cache. Redis: GET.
    /// </summary>
    ValueTask<Stream?> GetStreamAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value as a zero-copy lease over pooled memory — dispose it. Redis: GET.
    /// </summary>
    ValueTask<RespireLease> GetLeaseAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a key. Returns false when a <paramref name="when"/> condition was not met. Redis: SET.
    /// </summary>
    ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a key from exactly <paramref name="length"/> bytes read from <paramref name="value"/>.
    /// The stream remains open. Cancellation or a read failure during transmission closes the
    /// connection to preserve RESP framing, failing other commands pipelined on it; later
    /// commands on that connection wait for the complete frame. The command timeout covers the whole
    /// upload. Respire does not retry streamed writes.
    /// </summary>
    ValueTask<bool> SetAsync(
        RespireKey key,
        Stream value,
        long length,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a key from a sequence without combining its segments into one payload buffer. The
    /// sequence's memory must stay unchanged until the returned task completes; framing and
    /// timeout behavior match the <see cref="Stream"/> overload.
    /// </summary>
    ValueTask<bool> SetAsync(
        RespireKey key,
        ReadOnlySequence<byte> value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a key to a serialized <typeparamref name="T"/>. Redis: SET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a key and returns its previous value. Redis: SET … GET.</summary>
    ValueTask<string?> GetAndSetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a serialized <typeparamref name="T"/> and deserializes the previous value.
    /// Redis: SET … GET.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAndSetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value and deletes the key. Redis: GETDEL.</summary>
    ValueTask<string?> GetAndDeleteAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets and deserializes a key's value, then deletes the key. Redis: GETDEL.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAndDeleteAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value and updates or removes its expiry. Redis: GETEX.</summary>
    ValueTask<string?> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, CancellationToken cancellationToken = default);

    /// <summary>Gets and deserializes a key's value, then updates or removes its expiry. Redis: GETEX.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?> GetAndExpireAsync<T>(
        RespireKey key, RespireExpiry expiry, CancellationToken cancellationToken = default);

    /// <summary>Appends to a string and returns the new length. Redis: APPEND.</summary>
    ValueTask<long> AppendAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>The string's length in bytes (0 when missing). Redis: STRLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>A substring by byte offsets (negative offsets count from the end). Redis: GETRANGE.</summary>
    ValueTask<string> GetRangeAsync(RespireKey key, long start, long end, CancellationToken cancellationToken = default);

    /// <summary>Overwrites bytes from a zero-based offset and returns the new length. Redis: SETRANGE.</summary>
    ValueTask<long> SetRangeAsync(
        RespireKey key, long offset, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds <paramref name="by"/> and returns the new value. Redis: INCR when <paramref name="by"/> is 1, INCRBY otherwise.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds a floating-point delta and returns the new value. Pass a negative delta to subtract. Redis: INCRBYFLOAT.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, double by, CancellationToken cancellationToken = default);

    /// <summary>Atomically subtracts <paramref name="by"/> and returns the new value. Redis: DECR when <paramref name="by"/> is 1, DECRBY otherwise.</summary>
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    ValueTask<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    ValueTask<string?[]> GetManyAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Gets and deserializes many keys; missing keys yield default. Redis: MGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> GetManyAsync<T>(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Gets and deserializes many keys; missing keys yield default. Redis: MGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T?[]> GetManyAsync<T>(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Sets many keys atomically; returns true on OK. Redis: MSET.</summary>
    ValueTask<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Sets many keys atomically; returns true on OK. Redis: MSET.</summary>
    ValueTask<bool> SetManyAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken);

    /// <summary>Atomically sets every pair only when all keys are absent; returns false without writing if any key exists. Redis: MSETNX.</summary>
    /// <remarks>Requires at least one pair. Cluster keys must share a slot after prefixing. Values are raw; binary buffers must remain unchanged until completion. Duplicate keys follow server semantics.</remarks>
    ValueTask<bool> SetManyIfNotExistsAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets every pair only when all keys are absent; returns false without writing if any key exists. Redis: MSETNX.</summary>
    /// <remarks>Requires at least one pair. Cluster keys must share a slot after prefixing. Values are raw; binary buffers must remain unchanged until completion. Duplicate keys follow server semantics.</remarks>
    ValueTask<bool> SetManyIfNotExistsAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically sets many keys with a shared expiry. Use
    /// <see cref="RespireCommands.String.MSETEX"/> directly for second-precision EX/EXAT forms.
    /// Redis: MSETEX.
    /// </summary>
    ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets many keys with a shared expiry. Redis: MSETEX.</summary>
    ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        CancellationToken cancellationToken);

    /// <summary>Atomically sets many keys with a shared expiry and an NX/XX condition. Redis: MSETEX.</summary>
    ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        SetWhen when,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets many keys with a shared expiry and an NX/XX condition. Redis: MSETEX.</summary>
    ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the longest common subsequence. Use <see cref="RespireCommands.String.LCS"/> directly
    /// for the IDX range-reporting shape. Redis: LCS.
    /// </summary>
    ValueTask<string> LcsAsync(
        RespireKey firstKey, RespireKey secondKey, CancellationToken cancellationToken = default);

    /// <summary>Returns the length of the longest common subsequence. Redis: LCS LEN.</summary>
    ValueTask<long> LcsLengthAsync(
        RespireKey firstKey, RespireKey secondKey, CancellationToken cancellationToken = default);
}

internal sealed partial class StringCommands(RespireClient client) : IStringCommands
{
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.CachedGetAsync(
            client.ResolveKey(key),
            cancellationToken,
            static (RespireClient _, in Protocol.RespValue value) => ResponseReader.StringOrNull(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.CachedGetAsync(
            client.ResolveKey(key),
            cancellationToken,
            static (RespireClient state, in Protocol.RespValue value) => state.DeserializeBorrowed<T>(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.CachedGetAsync(
            client.ResolveKey(key),
            cancellationToken,
            static (RespireClient state, in Protocol.RespValue value) => state.TryDeserializeBorrowed<T>(in value));

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.CachedGetAsync(
            client.ResolveKey(key),
            cancellationToken,
            static (RespireClient _, in Protocol.RespValue value) => ResponseReader.BytesOrNull(in value));

    public ValueTask<Stream?> GetStreamAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.SendBulkStreamAsync(
            "GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<RespireLease> GetLeaseAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.LeaseAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        SetCommand.ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry, when, returnOld: false), cancellationToken);
    }

    public ValueTask<bool> SetAsync(
        RespireKey key, Stream value, long length, RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        // A short seekable source is rejected before any bytes are written, so it cannot close
        // the shared connection mid-frame.
        if (value.CanSeek && value.Length - value.Position < length)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                "The declared length exceeds the bytes remaining in the seekable source stream.");
        SetCommand.ValidateExpiry(expiry);
        return client.OkOrNullAsync("SET",
            new StreamedSetCommand(client.Key(in key), value, length, expiry, when), cancellationToken);
    }

    public ValueTask<bool> SetAsync(
        RespireKey key, ReadOnlySequence<byte> value, RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always, CancellationToken cancellationToken = default)
    {
        SetCommand.ValidateExpiry(expiry);
        return client.OkOrNullAsync("SET",
            new StreamedSetCommand(client.Key(in key), new SequencePayloadStream(value), value.Length, expiry, when),
            cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> SetAsync<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
    {
        SetCommand.ValidateExpiry(expiry);
        return client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), client.Serialize(value), expiry, when, returnOld: false),
            cancellationToken);
    }

    public ValueTask<string?> GetAndSetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        SetCommand.ValidateExpiry(expiry);
        return client.StringOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry, when, returnOld: true),
            cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAndSetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
    {
        SetCommand.ValidateExpiry(expiry);
        return client.DeserializeAsync<T, SetCommand>(
            "SET", new SetCommand(client.Key(in key), client.Serialize(value), expiry, when, returnOld: true),
            cancellationToken);
    }

    public ValueTask<string?> GetAndDeleteAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("GETDEL", new Cmd1(Verbs.GetDel, client.Key(in key)), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAndDeleteAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd1>(
            "GETDEL", new Cmd1(Verbs.GetDel, client.Key(in key)), cancellationToken);

    public ValueTask<string?> GetAndExpireAsync(
        RespireKey key, RespireExpiry expiry, CancellationToken cancellationToken = default)
    {
        GetExCommand.ValidateExpiry(expiry);
        return client.StringOrNullAsync(
            "GETEX", new GetExCommand(client.Key(in key), expiry), cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetAndExpireAsync<T>(
        RespireKey key, RespireExpiry expiry, CancellationToken cancellationToken = default)
    {
        GetExCommand.ValidateExpiry(expiry);
        return client.DeserializeAsync<T, GetExCommand>(
            "GETEX", new GetExCommand(client.Key(in key), expiry), cancellationToken);
    }

    public ValueTask<long> AppendAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default)
    {
        RespireValue.ThrowIfNull(value, nameof(value));
        return client.IntegerAsync("APPEND", new Cmd2(Verbs.Append, client.Key(in key), value), cancellationToken);
    }

    public ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("STRLEN", new Cmd1(Verbs.StrLen, client.Key(in key)), cancellationToken);

    public ValueTask<string> GetRangeAsync(RespireKey key, long start, long end, CancellationToken cancellationToken = default)
        => client.StringAsync("GETRANGE", new Cmd3(Verbs.GetRange, client.Key(in key), start, end), cancellationToken);

    public ValueTask<long> SetRangeAsync(
        RespireKey key, long offset, RespireValue value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        RespireValue.ThrowIfNull(value, nameof(value));
        return client.IntegerAsync(
            "SETRANGE", new Cmd3(Verbs.SetRange, client.Key(in key), offset, value), cancellationToken);
    }

    public ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            by == 1 ? "INCR" : "INCRBY",
            new IncrementCommand(Verbs.Incr, Verbs.IncrBy, client.Key(in key), by), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("INCRBYFLOAT", new Cmd2(Verbs.IncrByFloat, client.Key(in key), by), cancellationToken);

    public ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            by == 1 ? "DECR" : "DECRBY",
            new IncrementCommand(Verbs.Decr, Verbs.DecrBy, client.Key(in key), by), cancellationToken);

    public ValueTask<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys)
        => GetManyAsync(keys, CancellationToken.None);

    public ValueTask<string?[]> GetManyAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.Core.ClientCache is null && client.Core.Cluster is null
            ? client.NullableStringArrayAsync(
                "MGET", new CmdN(Verbs.MGet, client.MapKeys(keys)), cancellationToken)
            : client.CachedGetManyAsync(
                keys,
                cancellationToken,
                static (RespireClient _, in Protocol.RespValue value) => value.IsNull ? null : value.AsString());

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> GetManyAsync<T>(params ReadOnlySpan<RespireKey> keys)
        => GetManyAsync<T>(keys, CancellationToken.None);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?[]> GetManyAsync<T>(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.Core.ClientCache is null && client.Core.Cluster is null
            ? client.DeserializeNullableArrayAsync<T, CmdN>(
                "MGET", new CmdN(Verbs.MGet, client.MapKeys(keys)), cancellationToken)
            : client.CachedGetManyAsync(
                keys,
                cancellationToken,
                static (RespireClient client, in Protocol.RespValue value) => client.DeserializeBorrowed<T>(in value));

    public ValueTask<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
        => SetManyAsync(pairs, CancellationToken.None);

    public ValueTask<bool> SetManyAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken)
        => client.OkResultAsync(
            "MSET", new CmdN(Verbs.MSet, SetManyArgs(client, pairs)), cancellationToken);

    public ValueTask<bool> SetManyIfNotExistsAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
        => SetManyIfNotExistsAsync(pairs, CancellationToken.None);

    public ValueTask<bool> SetManyIfNotExistsAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<bool>(cancellationToken);
        return client.FlagAsync(
            "MSETNX", new CmdN(RespireCommands.String.MSETNX.Verb, SetManyIfNotExistsArgs(client, pairs)), cancellationToken);
    }

    public ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry, params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
        => SetManyExpireAsync(expiry, SetWhen.Always, pairs, CancellationToken.None);

    public ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        CancellationToken cancellationToken)
        => SetManyExpireAsync(expiry, SetWhen.Always, pairs, cancellationToken);

    public ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        SetWhen when,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
        => SetManyExpireAsync(expiry, when, pairs, CancellationToken.None);

    public ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        CancellationToken cancellationToken)
        => client.FlagAsync(
            "MSETEX",
            new MSetExCommand(
                RespireCommands.String.MSETEX.Verb,
                SetManyExpireArgs(client, expiry, when, pairs)),
            cancellationToken);

    public ValueTask<string> LcsAsync(
        RespireKey firstKey, RespireKey secondKey, CancellationToken cancellationToken = default)
        => client.StringAsync(
            "LCS",
            new Cmd2(RespireCommands.String.LCS.Verb, client.Key(in firstKey), client.Key(in secondKey)),
            cancellationToken);

    public ValueTask<long> LcsLengthAsync(
        RespireKey firstKey, RespireKey secondKey, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "LCS",
            new Cmd3(RespireCommands.String.LCS.Verb, client.Key(in firstKey), client.Key(in secondKey), "LEN"),
            cancellationToken);

    /// <summary>MSET key value… — shared with the deferred (batch/transaction) facet.</summary>
    internal static RespireValue[] SetManyArgs(
        RespireClient client, ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        var args = new RespireValue[pairs.Length * 2];
        for (var i = 0; i < pairs.Length; i++)
        {
            args[i * 2] = client.Key(in pairs[i].Key);
            args[i * 2 + 1] = pairs[i].Value;
        }

        return args;
    }

    internal static RespireValue[] SetManyIfNotExistsArgs(
        RespireClient client, ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        ValidatePairs(pairs);
        var args = SetManyArgs(client, pairs);
        if (client.Core.Cluster is not null)
            EnsureSameSlot(args, stride: 2, operation: "MSETNX");
        return args;
    }

    private static void EnsureSameSlot(ReadOnlySpan<RespireValue> args, int stride, string operation)
    {
        int? slot = null;
        for (var index = 0; index < args.Length; index += stride)
        {
            if (args[index].TryGetClusterSlot(out var keySlot))
            {
                if (slot is { } expected && keySlot != expected)
                {
                    // Match other multi-key facets: this server-shaped error is raised locally before I/O.
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
                }
                slot = keySlot;
            }
        }
    }

    /// <summary>MSETEX numkeys key value… [NX|XX] expiry — shared with the deferred facet.</summary>
    internal static RespireValue[] SetManyExpireArgs(
        RespireClient client,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        if (expiry.IsPersist)
        {
            throw new ArgumentException(
                "MSETEX does not support RespireExpiry.Persist; use SetManyAsync without an expiry instead.",
                nameof(expiry));
        }

        ValidatePairs(pairs);
        var condition = StringSetWhenToken(when);
        var args = new RespireValue[
            1 + pairs.Length * 2 + (condition is null ? 0 : 1) + expiry.TokenCount];
        var index = 0;
        args[index++] = pairs.Length;
        for (var i = 0; i < pairs.Length; i++)
        {
            args[index++] = client.Key(in pairs[i].Key);
            args[index++] = pairs[i].Value;
        }

        if (condition is not null)
        {
            args[index++] = condition;
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            args[index++] = "PX";
            args[index++] = milliseconds;
        }
        else if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            args[index++] = "PXAT";
            args[index++] = unixMilliseconds;
        }
        else if (expiry.IsKeep)
        {
            args[index++] = "KEEPTTL";
        }

        return args;
    }
    private static string? StringSetWhenToken(SetWhen when)
        => when switch
        {
            SetWhen.Always => null,
            SetWhen.NotExists => "NX",
            SetWhen.Exists => "XX",
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    private static void ValidatePairs(ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        if (pairs.Length == 0)
        {
            throw new ArgumentException("At least one key/value pair is required.", nameof(pairs));
        }
    }
}
