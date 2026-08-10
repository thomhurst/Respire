using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Generic key management commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IKeyCommands"/>, minus
/// <c>ScanAsync</c> — a cursor walk is many round trips and cannot be deferred.
/// </summary>
public interface IBatchKeyCommands
{
    /// <summary>Deletes keys; returns how many existed. Redis: DEL.</summary>
    RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Deletes keys asynchronously on the server (non-blocking reclaim). Redis: UNLINK.</summary>
    RespirePending<long> Unlink(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Whether the key exists. Redis: EXISTS.</summary>
    RespirePending<bool> Exists(RespireKey key);

    /// <summary>Sets a key's time to live. False when the key is missing. Redis: PEXPIRE.</summary>
    RespirePending<bool> Expire(RespireKey key, TimeSpan expiry);

    /// <summary>Sets an absolute expiry instant. False when the key is missing. Redis: PEXPIREAT.</summary>
    RespirePending<bool> ExpireAt(RespireKey key, DateTimeOffset expireAt);

    /// <summary>Removes a key's expiry. False when the key is missing or had none. Redis: PERSIST.</summary>
    RespirePending<bool> Persist(RespireKey key);

    /// <summary>
    /// The key's expiry state — distinguishes missing key, no expiry, and remaining TTL. Redis: PTTL.
    /// </summary>
    RespirePending<RespireTtl> Expiry(RespireKey key);

    /// <summary>The key's Redis type name ("string", "hash", …, or "none"). Redis: TYPE.</summary>
    RespirePending<string> Type(RespireKey key);

    /// <summary>Renames a key, overwriting any existing target; true once the server replies OK. Redis: RENAME.</summary>
    RespirePending<bool> Rename(RespireKey key, RespireKey newKey);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    RespirePending<long> Touch(params ReadOnlySpan<RespireKey> keys);
}

internal sealed class BatchKeyCommands(IPendingSink sink) : IBatchKeyCommands
{
    public RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys)
        => IntegerKeys("DEL", Verbs.Del, keys);

    public RespirePending<long> Unlink(params ReadOnlySpan<RespireKey> keys)
        => IntegerKeys("UNLINK", Verbs.Unlink, keys);

    public RespirePending<bool> Exists(RespireKey key)
        => sink.Add<Cmd1, bool>(
            "EXISTS", new Cmd1(Verbs.Exists, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> Expire(RespireKey key, TimeSpan expiry)
        => sink.Add<Cmd2, bool>(
            "PEXPIRE", new Cmd2(Verbs.PExpire, sink.Client.Key(in key), (long)expiry.TotalMilliseconds),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> ExpireAt(RespireKey key, DateTimeOffset expireAt)
        => sink.Add<Cmd2, bool>(
            "PEXPIREAT", new Cmd2(Verbs.PExpireAt, sink.Client.Key(in key), expireAt.ToUnixTimeMilliseconds()),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<bool> Persist(RespireKey key)
        => sink.Add<Cmd1, bool>(
            "PERSIST", new Cmd1(Verbs.Persist, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<RespireTtl> Expiry(RespireKey key)
        => sink.Add<Cmd1, RespireTtl>(
            "PTTL", new Cmd1(Verbs.Pttl, sink.Client.Key(in key)),
            static (c, v) => RespireTtl.FromRedisMilliseconds(ResponseReader.Integer(in v)));

    public RespirePending<string> Type(RespireKey key)
        => sink.Add<Cmd1, string>(
            "TYPE", new Cmd1(Verbs.Type, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.String(in v));

    public RespirePending<bool> Rename(RespireKey key, RespireKey newKey)
    {
        return sink.Add<Cmd2, bool>(
            "RENAME", new Cmd2(Verbs.Rename, sink.Client.Key(in key), sink.Client.Key(in newKey)),
            key, newKey,
            static (c, v) => ResponseReader.Ok(in v));
    }

    public RespirePending<long> Touch(params ReadOnlySpan<RespireKey> keys)
        => IntegerKeys("TOUCH", Verbs.Touch, keys);

    private RespirePending<long> IntegerKeys(string operation, Verb verb, ReadOnlySpan<RespireKey> keys)
    {
        return sink.Add<CmdN, long>(
            operation, new CmdN(verb, sink.Client.MapKeys(keys)),
            keys,
            static (c, v) => ResponseReader.Integer(in v));
    }
}
