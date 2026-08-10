using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// String (plain value) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IStringCommands"/>: same names and
/// parameter shapes, but each call returns a <see cref="RespirePending{T}"/> instead of awaiting,
/// and cancellation belongs to the execute/commit call. <c>GetLeaseAsync</c> has no deferred form —
/// a lease borrows the reply's pooled memory, which is released once the batch completes.
/// </summary>
public interface IBatchStringCommands
{
    /// <summary>Gets a key's value as a string, or null when missing. Redis: GET.</summary>
    RespirePending<string?> GetString(RespireKey key);

    /// <summary>Gets a key's value deserialized as <typeparamref name="T"/>, or default when missing. Redis: GET.</summary>
    RespirePending<T?> Get<T>(RespireKey key);

    /// <summary>Gets a key's raw bytes, or null when missing. Redis: GET.</summary>
    RespirePending<byte[]?> GetBytes(RespireKey key);

    /// <summary>
    /// Sets a key. The pending is false when a <paramref name="when"/> condition was not met. Redis: SET.
    /// </summary>
    RespirePending<bool> Set(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always);

    /// <summary>Sets a key to a serialized <typeparamref name="T"/>. Redis: SET.</summary>
    RespirePending<bool> Set<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always);

    /// <summary>Sets a key and returns its previous value. Redis: SET … GET.</summary>
    RespirePending<string?> GetAndSet(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always);

    /// <summary>
    /// Sets a serialized <typeparamref name="T"/> and deserializes the previous value.
    /// Redis: SET … GET.
    /// </summary>
    RespirePending<T?> GetAndSet<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always);

    /// <summary>Gets a key's value and deletes the key. Redis: GETDEL.</summary>
    RespirePending<string?> GetDelete(RespireKey key);

    /// <summary>Gets a key's value and updates or removes its expiry. Redis: GETEX.</summary>
    RespirePending<string?> GetExpire(RespireKey key, RespireExpiry expiry);

    /// <summary>Appends to a string and returns the new length. Redis: APPEND.</summary>
    RespirePending<long> Append(RespireKey key, RespireValue value);

    /// <summary>The string's length in bytes (0 when missing). Redis: STRLEN.</summary>
    RespirePending<long> Length(RespireKey key);

    /// <summary>A substring by byte offsets (negative offsets count from the end). Redis: GETRANGE.</summary>
    RespirePending<string> GetRange(RespireKey key, long start, long end);

    /// <summary>Atomically adds <paramref name="by"/> and returns the new value. Redis: INCR when <paramref name="by"/> is 1, INCRBY otherwise.</summary>
    RespirePending<long> Increment(RespireKey key, long by = 1);

    /// <summary>Atomically adds a floating-point delta and returns the new value. Redis: INCRBYFLOAT.</summary>
    RespirePending<double> Increment(RespireKey key, double by);

    /// <summary>Atomically subtracts <paramref name="by"/> and returns the new value. Redis: DECR when <paramref name="by"/> is 1, DECRBY otherwise.</summary>
    RespirePending<long> Decrement(RespireKey key, long by = 1);

    /// <summary>Gets many keys in one round trip; missing keys yield null. Redis: MGET.</summary>
    RespirePending<string?[]> GetMany(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Sets many keys atomically; the pending is true once the server replies OK. Redis: MSET.</summary>
    RespirePending<bool> SetMany(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Atomically sets many keys with a shared expiry and optional NX/XX condition. Redis: MSETEX.</summary>
    RespirePending<bool> SetManyExpire(
        RespireExpiry expiry,
        SetWhen when = SetWhen.Always,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs);

    /// <summary>Returns the longest common subsequence. Redis: LCS.</summary>
    RespirePending<string> Lcs(RespireKey firstKey, RespireKey secondKey);

    /// <summary>Returns the length of the longest common subsequence. Redis: LCS LEN.</summary>
    RespirePending<long> LcsLength(RespireKey firstKey, RespireKey secondKey);
}

internal sealed class BatchStringCommands(IPendingSink sink) : IBatchStringCommands
{
    public RespirePending<string?> GetString(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> Get<T>(RespireKey key)
        => sink.Add<Cmd1, T?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytes(RespireKey key)
        => sink.Add<Cmd1, byte[]?>(
            "GET", new Cmd1(Verbs.Get, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.BytesOrNull(in v));

    public RespirePending<bool> Set(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, bool>(
            "SET",
            new SetCommand(sink.Client.Key(in key), value, expiry, when, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<bool> Set<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, bool>(
            "SET",
            new SetCommand(sink.Client.Key(in key), sink.Client.Serialize(value), expiry, when, returnOld: false),
            static (c, v) => ResponseReader.OkOrNull(in v));

    public RespirePending<string?> GetAndSet(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, string?>(
            "SET",
            new SetCommand(sink.Client.Key(in key), value, expiry, when, returnOld: true),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<T?> GetAndSet<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => sink.Add<SetCommand, T?>(
            "SET",
            new SetCommand(sink.Client.Key(in key), sink.Client.Serialize(value), expiry, when, returnOld: true),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<string?> GetDelete(RespireKey key)
        => sink.Add<Cmd1, string?>(
            "GETDEL", new Cmd1(Verbs.GetDel, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<string?> GetExpire(RespireKey key, RespireExpiry expiry)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return sink.Add<Cmd3, string?>(
                "GETEX", new Cmd3(RespireCommands.String.GETEX.Verb, sink.Client.Key(in key), "PX", milliseconds),
                static (c, v) => ResponseReader.StringOrNull(in v));
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return sink.Add<Cmd3, string?>(
                "GETEX", new Cmd3(RespireCommands.String.GETEX.Verb, sink.Client.Key(in key), "PXAT", unixMilliseconds),
                static (c, v) => ResponseReader.StringOrNull(in v));
        }

        if (expiry.IsPersist)
        {
            return sink.Add<Cmd2, string?>(
                "GETEX", new Cmd2(RespireCommands.String.GETEX.Verb, sink.Client.Key(in key), "PERSIST"),
                static (c, v) => ResponseReader.StringOrNull(in v));
        }

        throw new ArgumentException(
            "GETEX expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public RespirePending<long> Append(RespireKey key, RespireValue value)
        => sink.Add<Cmd2, long>(
            "APPEND", new Cmd2(Verbs.Append, sink.Client.Key(in key), value),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> Length(RespireKey key)
        => sink.Add<Cmd1, long>(
            "STRLEN", new Cmd1(Verbs.StrLen, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string> GetRange(RespireKey key, long start, long end)
        => sink.Add<Cmd3, string>(
            "GETRANGE", new Cmd3(Verbs.GetRange, sink.Client.Key(in key), start, end),
            static (c, v) => ResponseReader.String(in v));

    public RespirePending<long> Increment(RespireKey key, long by = 1)
        => sink.Add<IncrementCommand, long>(
            by == 1 ? "INCR" : "INCRBY",
            new IncrementCommand(Verbs.Incr, Verbs.IncrBy, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double> Increment(RespireKey key, double by)
        => sink.Add<Cmd2, double>(
            "INCRBYFLOAT", new Cmd2(Verbs.IncrByFloat, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<long> Decrement(RespireKey key, long by = 1)
        => sink.Add<IncrementCommand, long>(
            by == 1 ? "DECR" : "DECRBY",
            new IncrementCommand(Verbs.Decr, Verbs.DecrBy, sink.Client.Key(in key), by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string?[]> GetMany(params ReadOnlySpan<RespireKey> keys)
    {
        return sink.Add<CmdN, string?[]>(
            "MGET", new CmdN(Verbs.MGet, sink.Client.MapKeys(keys)),
            keys,
            static (c, v) => ResponseReader.NullableStringArray(in v));
    }

    public RespirePending<bool> SetMany(params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        return sink.Add<CmdN, bool>(
            "MSET", new CmdN(Verbs.MSet, StringCommands.SetManyArgs(sink.Client, pairs)),
            pairs,
            static (c, v) => ResponseReader.Ok(in v));
    }

    public RespirePending<bool> SetManyExpire(
        RespireExpiry expiry,
        SetWhen when = SetWhen.Always,
        params ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        return sink.Add<MSetExCommand, bool>(
            "MSETEX",
            new MSetExCommand(
                RespireCommands.String.MSETEX.Verb,
                StringCommands.SetManyExpireArgs(sink.Client, expiry, when, pairs)),
            pairs,
            static (c, v) => ResponseReader.Flag(in v));
    }

    public RespirePending<string> Lcs(RespireKey firstKey, RespireKey secondKey)
    {
        return sink.Add<Cmd2, string>(
            "LCS",
            new Cmd2(RespireCommands.String.LCS.Verb, sink.Client.Key(in firstKey), sink.Client.Key(in secondKey)),
            firstKey, secondKey,
            static (c, v) => ResponseReader.String(in v));
    }

    public RespirePending<long> LcsLength(RespireKey firstKey, RespireKey secondKey)
    {
        return sink.Add<Cmd3, long>(
            "LCS",
            new Cmd3(RespireCommands.String.LCS.Verb, sink.Client.Key(in firstKey), sink.Client.Key(in secondKey), "LEN"),
            firstKey, secondKey,
            static (c, v) => ResponseReader.Integer(in v));
    }

}
