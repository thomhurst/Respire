using Respire.Commands;

namespace Respire;

/// <summary>Condition for SET-style writes.</summary>
public enum SetWhen
{
    /// <summary>Unconditional write.</summary>
    Always,

    /// <summary>
    /// Only write when the target does not exist. Maps to NX for string and geo commands, and
    /// FNX for hash-field writes (none of the supplied fields may exist).
    /// </summary>
    NotExists,

    /// <summary>
    /// Only write when the target exists. Maps to XX for string and geo commands, and FXX for
    /// hash-field writes (all supplied fields must exist).
    /// </summary>
    Exists,
}

/// <summary>
/// String (plain value) commands. Unlike collection facets' <c>CountAsync</c>,
/// <see cref="LengthAsync"/> returns a byte length.
/// </summary>
public interface IStringCommands
{
    /// <summary>Gets a key's value as a string, or null when missing. Redis: GET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value deserialized as <typeparamref name="T"/>, or default when missing. Redis: GET.</summary>
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value deserialized as <typeparamref name="T"/>, reporting presence separately
    /// so a missing key is distinguishable from a stored <c>default(T)</c>. Callers can instead
    /// make a value type nullable, such as <c>GetAsync&lt;int?&gt;</c>; this form keeps
    /// <typeparamref name="T"/> non-nullable and exposes an explicit presence flag. Redis: GET.
    /// </summary>
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's raw bytes, or null when missing. Redis: GET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

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

    /// <summary>Sets a key to a serialized <typeparamref name="T"/>. Redis: SET.</summary>
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
    ValueTask<T?> GetAndSetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value and deletes the key. Redis: GETDEL.</summary>
    ValueTask<string?> GetDeleteAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Appends to a string and returns the new length. Redis: APPEND.</summary>
    ValueTask<long> AppendAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default);

    /// <summary>The string's length in bytes (0 when missing). Redis: STRLEN.</summary>
    ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>A substring by byte offsets (negative offsets count from the end). Redis: GETRANGE.</summary>
    ValueTask<string> GetRangeAsync(RespireKey key, long start, long end, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds <paramref name="by"/> and returns the new value. Redis: INCR when <paramref name="by"/> is 1, INCRBY otherwise.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds a floating-point delta and returns the new value. Redis: INCRBYFLOAT.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, double by, CancellationToken cancellationToken = default);

    /// <summary>Atomically subtracts <paramref name="by"/> and returns the new value. Redis: DECR when <paramref name="by"/> is 1, DECRBY otherwise.</summary>
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    ValueTask<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    ValueTask<string?[]> GetManyAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Gets and deserializes many keys; missing keys yield default. Redis: MGET.</summary>
    ValueTask<T?[]> GetManyAsync<T>(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Gets and deserializes many keys; missing keys yield default. Redis: MGET.</summary>
    ValueTask<T?[]> GetManyAsync<T>(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Sets many keys atomically; returns true when Redis confirms the write. Redis: MSET.</summary>
    ValueTask<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Sets many keys atomically; returns true when Redis confirms the write. Redis: MSET.</summary>
    ValueTask<bool> SetManyAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically sets many keys with a shared expiry and an optional NX/XX condition. Use
    /// <see cref="RespireCommands.String.MSETEX"/> directly for second-precision EX/EXAT forms.
    /// Redis: MSETEX.
    /// </summary>
    ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry,
        SetWhen when = SetWhen.Always,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets many keys with a shared expiry and optional NX/XX condition. Redis: MSETEX.</summary>
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

internal sealed class StringCommands(RespireClient client) : IStringCommands
{
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd1>("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.TryDeserializeAsync<T, Cmd1>("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<RespireLease> GetLeaseAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.LeaseAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry, when, returnOld: false), cancellationToken);

    public ValueTask<bool> SetAsync<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), client.Serialize(value), expiry, when, returnOld: false),
            cancellationToken);

    public ValueTask<string?> GetAndSetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => client.StringOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry, when, returnOld: true),
            cancellationToken);

    public ValueTask<T?> GetAndSetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, SetCommand>(
            "SET", new SetCommand(client.Key(in key), client.Serialize(value), expiry, when, returnOld: true),
            cancellationToken);

    public ValueTask<string?> GetDeleteAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("GETDEL", new Cmd1(Verbs.GetDel, client.Key(in key)), cancellationToken);

    public ValueTask<long> AppendAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default)
        => client.IntegerAsync("APPEND", new Cmd2(Verbs.Append, client.Key(in key), value), cancellationToken);

    public ValueTask<long> LengthAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("STRLEN", new Cmd1(Verbs.StrLen, client.Key(in key)), cancellationToken);

    public ValueTask<string> GetRangeAsync(RespireKey key, long start, long end, CancellationToken cancellationToken = default)
        => client.StringAsync("GETRANGE", new Cmd3(Verbs.GetRange, client.Key(in key), start, end), cancellationToken);

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
        => client.NullableStringArrayAsync("MGET", new CmdN(Verbs.MGet, client.MapKeys(keys)), cancellationToken);

    public ValueTask<T?[]> GetManyAsync<T>(params ReadOnlySpan<RespireKey> keys)
        => GetManyAsync<T>(keys, CancellationToken.None);

    public ValueTask<T?[]> GetManyAsync<T>(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.DeserializeNullableArrayAsync<T, CmdN>(
            "MGET", new CmdN(Verbs.MGet, client.MapKeys(keys)), cancellationToken);

    public ValueTask<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
        => SetManyAsync(pairs, CancellationToken.None);

    public ValueTask<bool> SetManyAsync(
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs, CancellationToken cancellationToken)
        => client.ConfirmedOkAsync(
            "MSET", new CmdN(Verbs.MSet, SetManyArgs(client, pairs)), cancellationToken);

    public ValueTask<bool> SetManyExpireAsync(
        RespireExpiry expiry, SetWhen when = SetWhen.Always,
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

    /// <summary>MSETEX numkeys key value… [NX|XX] expiry — shared with the deferred facet.</summary>
    internal static RespireValue[] SetManyExpireArgs(
        RespireClient client,
        RespireExpiry expiry,
        SetWhen when,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
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
