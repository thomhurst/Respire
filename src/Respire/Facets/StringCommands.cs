using Respire.Commands;

namespace Respire;

/// <summary>Condition for SET-style writes.</summary>
public enum SetWhen
{
    /// <summary>Unconditional write.</summary>
    Always,

    /// <summary>Only set if the key does not exist (NX).</summary>
    NotExists,

    /// <summary>Only set if the key already exists (XX).</summary>
    Exists,
}

/// <summary>String (plain value) commands.</summary>
public interface IStringCommands
{
    /// <summary>Gets a key's value as a string, or null when missing. Redis: GET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets a key's value deserialized as <typeparamref name="T"/>, or default when missing. Redis: GET.</summary>
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

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
        TimeSpan? expiry = null,
        SetWhen when = SetWhen.Always,
        bool keepTtl = false,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a key to a serialized <typeparamref name="T"/>. Redis: SET.</summary>
    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        TimeSpan? expiry = null,
        SetWhen when = SetWhen.Always,
        bool keepTtl = false,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a key and returns its previous value. Redis: SET … GET.</summary>
    ValueTask<string?> GetSetAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default);

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

    /// <summary>Sets many keys in one atomic round trip. Redis: MSET.</summary>
    ValueTask SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);
}

internal sealed class StringCommands(RespireClient client) : IStringCommands
{
    public ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default)
        => client.DeserializeAsync<T, Cmd1>("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.BytesOrNullAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<RespireLease> GetLeaseAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.LeaseAsync("GET", new Cmd1(Verbs.Get, client.Key(in key)), cancellationToken);

    public ValueTask<bool> SetAsync(
        RespireKey key, RespireValue value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always,
        bool keepTtl = false, CancellationToken cancellationToken = default)
        => client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry, when, keepTtl, returnOld: false), cancellationToken);

    public ValueTask<bool> SetAsync<T>(
        RespireKey key, T value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always,
        bool keepTtl = false, CancellationToken cancellationToken = default)
        => client.OkOrNullAsync(
            "SET", new SetCommand(client.Key(in key), client.Serialize(value), expiry, when, keepTtl, returnOld: false),
            cancellationToken);

    public ValueTask<string?> GetSetAsync(RespireKey key, RespireValue value, CancellationToken cancellationToken = default)
        => client.StringOrNullAsync(
            "SET", new SetCommand(client.Key(in key), value, expiry: null, SetWhen.Always, keepTtl: false, returnOld: true),
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
        => by == 1
            ? client.IntegerAsync("INCR", new Cmd1(Verbs.Incr, client.Key(in key)), cancellationToken)
            : client.IntegerAsync("INCRBY", new Cmd2(Verbs.IncrBy, client.Key(in key), by), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("INCRBYFLOAT", new Cmd2(Verbs.IncrByFloat, client.Key(in key), by), cancellationToken);

    public ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default)
        => by == 1
            ? client.IntegerAsync("DECR", new Cmd1(Verbs.Decr, client.Key(in key)), cancellationToken)
            : client.IntegerAsync("DECRBY", new Cmd2(Verbs.DecrBy, client.Key(in key), by), cancellationToken);

    public ValueTask<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys)
        => client.NullableStringArrayAsync("MGET", new CmdN(Verbs.MGet, client.MapKeys(keys)), CancellationToken.None);

    public ValueTask SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        var args = new RespireValue[pairs.Length * 2];
        for (var i = 0; i < pairs.Length; i++)
        {
            args[i * 2] = client.Key(in pairs[i].Key);
            args[i * 2 + 1] = pairs[i].Value;
        }

        return client.OkAsync("MSET", new CmdN(Verbs.MSet, args), CancellationToken.None);
    }
}
