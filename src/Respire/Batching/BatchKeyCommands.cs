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

    /// <summary>Sets, updates, or removes a key's expiry. Redis: PEXPIRE/PEXPIREAT/PERSIST.</summary>
    RespirePending<bool> Expire(
        RespireKey key, RespireExpiry expiry, ExpireWhen when = ExpireWhen.Always);

    /// <summary>
    /// The key's expiry state — distinguishes missing key, no expiry, and remaining TTL. Redis: PTTL.
    /// </summary>
    RespirePending<RespireTtl> Expiry(RespireKey key);

    /// <summary>The data structure stored at a key, or <see cref="RespireKeyType.None"/>. Redis: TYPE.</summary>
    RespirePending<RespireKeyType> Type(RespireKey key);

    /// <summary>Renames a key, overwriting any existing target; true once the server replies OK. Redis: RENAME.</summary>
    RespirePending<bool> Rename(RespireKey key, RespireKey newKey);

    /// <summary>Renames a key only when the target does not exist. Redis: RENAMENX.</summary>
    RespirePending<bool> TryRename(RespireKey key, RespireKey newKey);

    /// <summary>Copies a key, optionally replacing an existing target. Redis: COPY.</summary>
    RespirePending<bool> Copy(RespireKey source, RespireKey destination, bool replace = false);

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

    public RespirePending<bool> Expire(
        RespireKey key, RespireExpiry expiry, ExpireWhen when = ExpireWhen.Always)
    {
        var condition = KeyCommands.ExpireWhenToken(when);
        if (expiry.IsPersist)
        {
            if (condition is not null)
            {
                throw new ArgumentException("PERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return sink.Add<Cmd1, bool>(
                "PERSIST", new Cmd1(Verbs.Persist, sink.Client.Key(in key)),
                static (c, v) => ResponseReader.Flag(in v));
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return condition is null
                ? sink.Add<Cmd2, bool>(
                    "PEXPIRE", new Cmd2(Verbs.PExpire, sink.Client.Key(in key), milliseconds),
                    static (c, v) => ResponseReader.Flag(in v))
                : sink.Add<Cmd3, bool>(
                    "PEXPIRE", new Cmd3(Verbs.PExpire, sink.Client.Key(in key), milliseconds, condition),
                    static (c, v) => ResponseReader.Flag(in v));
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return condition is null
                ? sink.Add<Cmd2, bool>(
                    "PEXPIREAT", new Cmd2(Verbs.PExpireAt, sink.Client.Key(in key), unixMilliseconds),
                    static (c, v) => ResponseReader.Flag(in v))
                : sink.Add<Cmd3, bool>(
                    "PEXPIREAT", new Cmd3(Verbs.PExpireAt, sink.Client.Key(in key), unixMilliseconds, condition),
                    static (c, v) => ResponseReader.Flag(in v));
        }

        throw new ArgumentException(
            "Key expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public RespirePending<RespireTtl> Expiry(RespireKey key)
        => sink.Add<Cmd1, RespireTtl>(
            "PTTL", new Cmd1(Verbs.Pttl, sink.Client.Key(in key)),
            static (c, v) => RespireTtl.FromRedisMilliseconds(ResponseReader.Integer(in v)));

    public RespirePending<RespireKeyType> Type(RespireKey key)
        => sink.Add<Cmd1, RespireKeyType>(
            "TYPE", new Cmd1(Verbs.Type, sink.Client.Key(in key)),
            static (c, v) => KeyCommands.ParseKeyType(ResponseReader.String(in v)));

    public RespirePending<bool> Rename(RespireKey key, RespireKey newKey)
    {
        return sink.Add<Cmd2, bool>(
            "RENAME", new Cmd2(Verbs.Rename, sink.Client.Key(in key), sink.Client.Key(in newKey)),
            key, newKey,
            static (c, v) => ResponseReader.Ok(in v));
    }

    public RespirePending<bool> TryRename(RespireKey key, RespireKey newKey)
    {
        return sink.Add<Cmd2, bool>(
            "RENAMENX", new Cmd2(Verbs.RenameNx, sink.Client.Key(in key), sink.Client.Key(in newKey)),
            key, newKey,
            static (c, v) => ResponseReader.Flag(in v));
    }

    public RespirePending<bool> Copy(RespireKey source, RespireKey destination, bool replace = false)
    {
        return replace
            ? sink.Add<Cmd3, bool>(
                "COPY",
                new Cmd3(Verbs.Copy, sink.Client.Key(in source), sink.Client.Key(in destination), "REPLACE"),
                source,
                destination,
                static (c, v) => ResponseReader.Flag(in v))
            : sink.Add<Cmd2, bool>(
                "COPY", new Cmd2(Verbs.Copy, sink.Client.Key(in source), sink.Client.Key(in destination)),
                source, destination,
                static (c, v) => ResponseReader.Flag(in v));
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
