using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// String (plain value) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IStringCommands"/>: same names and
/// parameter shapes, but each call returns a <see cref="RespirePending{T}"/> instead of awaiting,
/// and cancellation belongs to the send/commit call. <c>GetLeaseAsync</c> has no deferred form —
/// a lease borrows the reply's pooled memory, which is released once the batch completes.
/// </summary>
public interface IBatchStringCommands
{
    /// <summary>Gets a key's value as a string, or null when missing. Redis: GET.</summary>
    RespirePending<string?> GetStringAsync(RespireKey key);

    /// <summary>Gets a key's value deserialized as <typeparamref name="T"/>, or default when missing. Redis: GET.</summary>
    RespirePending<T?> GetAsync<T>(RespireKey key);

    /// <summary>Gets a key's raw bytes, or null when missing. Redis: GET.</summary>
    RespirePending<byte[]?> GetBytesAsync(RespireKey key);

    /// <summary>
    /// Sets a key. The pending is false when a <paramref name="when"/> condition was not met. Redis: SET.
    /// </summary>
    RespirePending<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireTtl expiry = default,
        SetWhen when = SetWhen.Always);

    /// <summary>Sets a key to a serialized <typeparamref name="T"/>. Redis: SET.</summary>
    RespirePending<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireTtl expiry = default,
        SetWhen when = SetWhen.Always);

    /// <summary>Sets a key and returns its previous value. Redis: SET … GET.</summary>
    RespirePending<string?> GetSetAsync(RespireKey key, RespireValue value);

    /// <summary>Gets a key's value and deletes the key. Redis: GETDEL.</summary>
    RespirePending<string?> GetDeleteAsync(RespireKey key);

    /// <summary>Appends to a string and returns the new length. Redis: APPEND.</summary>
    RespirePending<long> AppendAsync(RespireKey key, RespireValue value);

    /// <summary>The string's length in bytes (0 when missing). Redis: STRLEN.</summary>
    RespirePending<long> LengthAsync(RespireKey key);

    /// <summary>A substring by byte offsets (negative offsets count from the end). Redis: GETRANGE.</summary>
    RespirePending<string> GetRangeAsync(RespireKey key, long start, long end);

    /// <summary>Atomically adds <paramref name="by"/> and returns the new value. Redis: INCR when <paramref name="by"/> is 1, INCRBY otherwise.</summary>
    RespirePending<long> IncrementAsync(RespireKey key, long by = 1);

    /// <summary>Atomically adds a floating-point delta and returns the new value. Redis: INCRBYFLOAT.</summary>
    RespirePending<double> IncrementAsync(RespireKey key, double by);

    /// <summary>Atomically subtracts <paramref name="by"/> and returns the new value. Redis: DECR when <paramref name="by"/> is 1, DECRBY otherwise.</summary>
    RespirePending<long> DecrementAsync(RespireKey key, long by = 1);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    RespirePending<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Sets many keys atomically; the pending is true once the server replies OK. Redis: MSET.</summary>
    RespirePending<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets many keys with a shared expiry and optional NX/XX condition. Redis: MSETEX.</summary>
    RespirePending<bool> SetManyAsync(
        RespireTtl expiry,
        SetWhen when = SetWhen.Always,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Returns the longest common subsequence. Redis: LCS.</summary>
    RespirePending<string> LongestCommonSubsequenceAsync(RespireKey firstKey, RespireKey secondKey);

    /// <summary>Returns the length of the longest common subsequence. Redis: LCS LEN.</summary>
    RespirePending<long> LongestCommonSubsequenceLengthAsync(RespireKey firstKey, RespireKey secondKey);
}

internal sealed class BatchStringCommands(IPendingSink sink) : IBatchStringCommands
{
    public RespirePending<string?> GetStringAsync(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAsync<T>(RespireKey key)
        => sink.Add<Cmd1, T?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytesAsync(RespireKey key)
        => sink.Add<Cmd1, byte[]?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.BytesOrNull(in v));

    public RespirePending<bool> SetAsync(
        RespireKey key, RespireValue value, RespireTtl expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, bool>(
            "SET",
            new SetCommand(sink.Client.Key(in key), value, expiry, when, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<bool> SetAsync<T>(
        RespireKey key, T value, RespireTtl expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, bool>(
            "SET",
            new SetCommand(sink.Client.Key(in key), sink.Client.Serialize(value), expiry, when, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<string?> GetSetAsync(RespireKey key, RespireValue value)
        => sink.Add<SetCommand, string?>(
            "SET",
            new SetCommand(sink.Client.Key(in key), value, RespireTtl.None, SetWhen.Always, returnOld: true),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<string?> GetDeleteAsync(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "GETDEL", new Cmd1(Verbs.GetDel, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<long> AppendAsync(RespireKey key, RespireValue value)
        => sink.Add<Cmd2, long>(
            "APPEND", new Cmd2(Verbs.Append, sink.Client.Key(in key), value),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> LengthAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "STRLEN", new Cmd1(Verbs.StrLen, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string> GetRangeAsync(RespireKey key, long start, long end)
        => sink.Add<Cmd3, string>(
            "GETRANGE", new Cmd3(Verbs.GetRange, sink.Client.Key(in key), start, end),
            static (c, v) => ResponseReader.String(in v));

    public RespirePending<long> IncrementAsync(RespireKey key, long by = 1)
        => sink.Add<IncrementCommand, long>(
            by == 1 ? "INCR" : "INCRBY",
            new IncrementCommand(Verbs.Incr, Verbs.IncrBy, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double> IncrementAsync(RespireKey key, double by)
        => sink.Add<Cmd2, double>(
            "INCRBYFLOAT", new Cmd2(Verbs.IncrByFloat, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<long> DecrementAsync(RespireKey key, long by = 1)
        => sink.Add<IncrementCommand, long>(
            by == 1 ? "DECR" : "DECRBY",
            new IncrementCommand(Verbs.Decr, Verbs.DecrBy, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string?[]> GetManyAsync(params ReadOnlySpan<RespireKey> keys)
    {
        sink.ValidateClusterKeys(keys);
        return sink.Add<CmdN, string?[]>(
            "MGET", new CmdN(Verbs.MGet, sink.Client.MapKeys(keys)),
            static (c, v) => ResponseReader.NullableStringArray(in v));
    }

    public RespirePending<bool> SetManyAsync(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        sink.ValidateClusterKeys(pairs);
        return sink.Add<CmdN, bool>(
            "MSET", new CmdN(Verbs.MSet, StringCommands.SetManyArgs(sink.Client, pairs)),
            static (c, v) => ResponseReader.Ok(in v));
    }

    public RespirePending<bool> SetManyAsync(
        RespireTtl expiry,
        SetWhen when = SetWhen.Always,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        sink.ValidateClusterKeys(pairs);
        return sink.Add<MSetExCommand, bool>(
            "MSETEX",
            new MSetExCommand(
                RespireCommands.String.MSETEX.Verb,
                StringCommands.SetManyExpireArgs(sink.Client, expiry, when, pairs)),
            static (c, v) => ResponseReader.Flag(in v));
    }

    public RespirePending<string> LongestCommonSubsequenceAsync(RespireKey firstKey, RespireKey secondKey)
    {
        sink.ValidateClusterKeys(firstKey, secondKey);
        return sink.Add<Cmd2, string>(
            "LCS",
            new Cmd2(RespireCommands.String.LCS.Verb, sink.Client.Key(in firstKey), sink.Client.Key(in secondKey)),
            static (c, v) => ResponseReader.String(in v));
    }

    public RespirePending<long> LongestCommonSubsequenceLengthAsync(RespireKey firstKey, RespireKey secondKey)
    {
        sink.ValidateClusterKeys(firstKey, secondKey);
        return sink.Add<Cmd3, long>(
            "LCS",
            new Cmd3(RespireCommands.String.LCS.Verb, sink.Client.Key(in firstKey), sink.Client.Key(in secondKey), "LEN"),
            static (c, v) => ResponseReader.Integer(in v));
    }

}
