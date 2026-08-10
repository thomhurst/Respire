using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Hash (field → value map) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IHashCommands"/>; collection cardinality
/// uses <see cref="Count"/>.
/// </summary>
public interface IBatchHashCommands
{
    /// <summary>
    /// Sets one field. The pending is true when an unconditional write creates the field, or when
    /// a conditional write is applied. Redis: HSET/HSETNX/HSETEX.
    /// </summary>
    RespirePending<bool> Set(
        RespireKey key, string field, RespireValue value, SetWhen when = SetWhen.Always);

    /// <summary>Sets many fields; returns how many were newly created. Redis: HSET.</summary>
    RespirePending<long> Set(RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Gets a field as a string, or null when missing. Redis: HGET.</summary>
    RespirePending<string?> GetString(RespireKey key, string field);

    /// <summary>Gets a field deserialized as <typeparamref name="T"/>. Redis: HGET.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Get<T>(RespireKey key, string field);

    /// <summary>Gets a field's raw bytes, or null when missing. Redis: HGET.</summary>
    RespirePending<byte[]?> GetBytes(RespireKey key, string field);

    /// <summary>Gets many fields in one round trip; missing fields yield null. Redis: HMGET.</summary>
    RespirePending<string?[]> GetMany(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>The whole hash as a dictionary. Redis: HGETALL.</summary>
    RespirePending<Dictionary<string, string>> GetAll(RespireKey key);

    /// <summary>The whole hash with values deserialized as <typeparamref name="T"/>. Redis: HGETALL.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<Dictionary<string, T>> GetAll<T>(RespireKey key);

    /// <summary>Deletes fields; returns how many existed. Redis: HDEL.</summary>
    RespirePending<long> Delete(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Whether the field exists. Redis: HEXISTS.</summary>
    RespirePending<bool> Exists(RespireKey key, string field);

    /// <summary>Number of fields in the hash. Redis: HLEN.</summary>
    RespirePending<long> Count(RespireKey key);

    /// <summary>Atomically adds to a numeric field and returns the new value. Redis: HINCRBY.</summary>
    RespirePending<long> Increment(RespireKey key, string field, long by = 1);

    /// <summary>Atomically adds a floating-point delta to a field. Redis: HINCRBYFLOAT.</summary>
    RespirePending<double> Increment(RespireKey key, string field, double by);

    /// <summary>All field names. Redis: HKEYS.</summary>
    RespirePending<string[]> Fields(RespireKey key);

    /// <summary>All values. Redis: HVALS.</summary>
    RespirePending<string[]> Values(RespireKey key);

    /// <summary>Expiry state for fields, in milliseconds. Redis: HPTTL.</summary>
    RespirePending<RespireTtl[]> Expiry(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Sets, updates, or removes field expiry metadata. Redis: HPEXPIRE/HPEXPIREAT/HPERSIST.</summary>
    RespirePending<HashFieldExpiryResult[]> Expire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets field expiry with an NX, XX, GT, or LT condition. Redis: HPEXPIRE/HPEXPIREAT.</summary>
    RespirePending<HashFieldExpiryResult[]> Expire(
        RespireKey key, RespireExpiry expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and deletes them atomically. Redis: HGETDEL.</summary>
    RespirePending<string?[]> GetDelete(RespireKey key, params ReadOnlySpan<string> fields);

    /// <summary>Gets fields and updates or removes their expiry metadata. Redis: HGETEX.</summary>
    RespirePending<string?[]> GetExpire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields);

    /// <summary>Sets fields and applies a relative, absolute, or retained expiry. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields);

    /// <summary>Sets fields with a FNX/FXX condition and applies an expiry. Redis: HSETEX.</summary>
    RespirePending<bool> SetExpire(
        RespireKey key, RespireExpiry expiry, SetWhen when,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields);
}

internal sealed class BatchHashCommands(IPendingSink sink) : IBatchHashCommands
{
    public RespirePending<bool> Set(
        RespireKey key, string field, RespireValue value, SetWhen when = SetWhen.Always)
        => when switch
        {
            SetWhen.Always => sink.Add<Cmd3, bool>(
                "HSET", new Cmd3(Verbs.HSet, sink.Client.Key(in key), field, value),
                static (c, v) => ResponseReader.Flag(in v)),
            SetWhen.NotExists => sink.Add<Cmd3, bool>(
                "HSETNX", new Cmd3(Verbs.HSetNx, sink.Client.Key(in key), field, value),
                static (c, v) => ResponseReader.Flag(in v)),
            SetWhen.Exists => sink.Add<Cmd1N, bool>(
                "HSETEX",
                new Cmd1N(
                    RespireCommands.Hash.HSETEX.Verb,
                    sink.Client.Key(in key),
                    HashCommands.SetExFieldsBlock("KEEPTTL", 0, hasValue: false, when, [(field, value)])),
                static (c, v) => ResponseReader.Flag(in v)),
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    public RespirePending<long> Set(
        RespireKey key, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, long>(
            "HSET", new Cmd1N(Verbs.HSet, sink.Client.Key(in key), HashCommands.FieldValuePairs(fields)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<string?> GetString(RespireKey key, string field)
        => sink.Add<Cmd2, string?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.StringOrNull(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> Get<T>(RespireKey key, string field)
        => sink.Add<Cmd2, T?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => c.DeserializeBorrowed<T>(in v));

    public RespirePending<byte[]?> GetBytes(RespireKey key, string field)
        => sink.Add<Cmd2, byte[]?>(
            "HGET", new Cmd2(Verbs.HGet, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.BytesOrNull(in v));

    public RespirePending<string?[]> GetMany(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, string?[]>(
            "HMGET", new Cmd1N(Verbs.HMGet, sink.Client.Key(in key), HashCommands.ToValues(fields)),
            static (c, v) => ResponseReader.NullableStringArray(in v));

    public RespirePending<Dictionary<string, string>> GetAll(RespireKey key)
        => sink.Add<Cmd1, Dictionary<string, string>>(
            "HGETALL", new Cmd1(Verbs.HGetAll, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringMap(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<Dictionary<string, T>> GetAll<T>(RespireKey key)
        => sink.Add<Cmd1, Dictionary<string, T>>(
            "HGETALL", new Cmd1(Verbs.HGetAll, sink.Client.Key(in key)),
            static (c, v) => c.DeserializeMap<T>(in v));

    public RespirePending<long> Delete(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, long>(
            "HDEL", new Cmd1N(Verbs.HDel, sink.Client.Key(in key), HashCommands.ToValues(fields)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<bool> Exists(RespireKey key, string field)
        => sink.Add<Cmd2, bool>(
            "HEXISTS", new Cmd2(Verbs.HExists, sink.Client.Key(in key), field),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> Count(RespireKey key)
        => sink.Add<Cmd1, long>(
            "HLEN", new Cmd1(Verbs.HLen, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> Increment(RespireKey key, string field, long by = 1)
        => sink.Add<Cmd3, long>(
            "HINCRBY", new Cmd3(Verbs.HIncrBy, sink.Client.Key(in key), field, by),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double> Increment(RespireKey key, string field, double by)
        => sink.Add<Cmd3, double>(
            "HINCRBYFLOAT", new Cmd3(Verbs.HIncrByFloat, sink.Client.Key(in key), field, by),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<string[]> Fields(RespireKey key)
        => sink.Add<Cmd1, string[]>(
            "HKEYS", new Cmd1(Verbs.HKeys, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<string[]> Values(RespireKey key)
        => sink.Add<Cmd1, string[]>(
            "HVALS", new Cmd1(Verbs.HVals, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<RespireTtl[]> Expiry(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, RespireTtl[]>(
            "HPTTL",
            new Cmd1N(RespireCommands.Hash.HPTTL.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
            static (c, v) => ResponseReader.TtlArray(in v));

    public RespirePending<HashFieldExpiryResult[]> Expire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
        => Expire(key, expiry, HashFieldExpireWhen.Always, fields);

    public RespirePending<HashFieldExpiryResult[]> Expire(
        RespireKey key, RespireExpiry expiry, HashFieldExpireWhen when, params ReadOnlySpan<string> fields)
    {
        if (expiry.IsPersist)
        {
            if (when != HashFieldExpireWhen.Always)
            {
                throw new ArgumentException("HPERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return sink.Add<Cmd1N, HashFieldExpiryResult[]>(
                "HPERSIST",
                new Cmd1N(RespireCommands.Hash.HPERSIST.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
                static (c, v) => ResponseReader.HashFieldExpiryResultArray(in v));
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return ExpireCore("HPEXPIRE", RespireCommands.Hash.HPEXPIRE.Verb, key, milliseconds, when, fields);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return ExpireCore("HPEXPIREAT", RespireCommands.Hash.HPEXPIREAT.Verb, key, unixMilliseconds, when, fields);
        }

        throw new ArgumentException(
            "Hash expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public RespirePending<string?[]> GetDelete(RespireKey key, params ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, string?[]>(
            "HGETDEL",
            new Cmd1N(RespireCommands.Hash.HGETDEL.Verb, sink.Client.Key(in key), HashCommands.FieldsBlock(fields)),
            static (c, v) => ResponseReader.NullableStringArray(in v));

    public RespirePending<string?[]> GetExpire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<string> fields)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return GetExpireCore(key, "PX", milliseconds, hasValue: true, fields);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return GetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, fields);
        }

        if (expiry.IsPersist)
        {
            return GetExpireCore(key, "PERSIST", optionValue: 0, hasValue: false, fields);
        }

        throw new ArgumentException(
            "HGETEX expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public RespirePending<bool> SetExpire(
        RespireKey key, RespireExpiry expiry, params ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => SetExpire(key, expiry, SetWhen.Always, fields);

    public RespirePending<bool> SetExpire(
        RespireKey key, RespireExpiry expiry, SetWhen when,
        params ReadOnlySpan<(string Field, RespireValue Value)> fields)
    {
        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return SetExpireCore(key, "PX", milliseconds, hasValue: true, when, fields);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return SetExpireCore(key, "PXAT", unixMilliseconds, hasValue: true, when, fields);
        }

        if (expiry.IsKeep)
        {
            return SetExpireCore(key, "KEEPTTL", optionValue: 0, hasValue: false, when, fields);
        }

        throw new ArgumentException(
            "HSETEX expiry must be relative, absolute, or RespireExpiry.Keep.", nameof(expiry));
    }

    private RespirePending<HashFieldExpiryResult[]> ExpireCore(
        string operation,
        Verb verb,
        RespireKey key,
        long value,
        HashFieldExpireWhen when,
        ReadOnlySpan<string> fields)
        => sink.Add<Cmd1N, HashFieldExpiryResult[]>(
            operation,
            new Cmd1N(verb, sink.Client.Key(in key), HashCommands.ExpireFieldsBlock(value, when, fields)),
            static (c, v) => ResponseReader.HashFieldExpiryResultArray(in v));

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
        RespireKey key, string option, long optionValue, bool hasValue, SetWhen when,
        ReadOnlySpan<(string Field, RespireValue Value)> fields)
        => sink.Add<Cmd1N, bool>(
            "HSETEX",
            new Cmd1N(
                RespireCommands.Hash.HSETEX.Verb,
                sink.Client.Key(in key),
                HashCommands.SetExFieldsBlock(option, optionValue, hasValue, when, fields)),
            static (c, v) => ResponseReader.Flag(in v));
}
