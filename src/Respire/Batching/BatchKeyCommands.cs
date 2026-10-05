using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Generic key management commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IKeyCommands"/>, minus
/// <c>ScanAsync</c> and <c>ScanClusterPageAsync</c>: cursor walks and validated Cluster pages
/// require multiple round trips and cannot be deferred.
/// </summary>
public partial interface IBatchKeyCommands
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

    /// <summary>Absolute expiration, distinguishing missing and persistent keys. Redis 7+: EXPIRETIME/PEXPIRETIME.</summary>
    RespirePending<RespireExpiryTime> ExpiryTime(RespireKey key);

    /// <summary>Absolute expiration at the requested server precision. Redis 7+: EXPIRETIME/PEXPIRETIME.</summary>
    RespirePending<RespireExpiryTime> ExpiryTime(RespireKey key, ExpiryTimePrecision precision);

    /// <summary>The server's internal value encoding, or null for a missing key. Redis: OBJECT ENCODING.</summary>
    RespirePending<string?> Encoding(RespireKey key);

    /// <summary>Time since last access, with second resolution, or null for a missing key. Redis: OBJECT IDLETIME.</summary>
    /// <remarks>Redis rejects this command under LFU eviction policies.</remarks>
    RespirePending<TimeSpan?> IdleTime(RespireKey key);

    /// <summary>The logarithmic access frequency counter, or null for a missing key. Redis: OBJECT FREQ.</summary>
    /// <remarks>Requires an LFU eviction policy. Server errors surface through the pending result.</remarks>
    RespirePending<long?> Frequency(RespireKey key);

    /// <summary>The server's internal value reference count, or null for a missing key. Redis: OBJECT REFCOUNT.</summary>
    RespirePending<long?> ReferenceCount(RespireKey key);

    /// <summary>The data structure stored at a key, or <see cref="RespireKeyType.None"/>. Redis: TYPE.</summary>
    RespirePending<RespireKeyType> Type(RespireKey key);

    /// <summary>Renames a key, overwriting any existing target; true once the server replies OK. Redis: RENAME.</summary>
    RespirePending<bool> Rename(RespireKey key, RespireKey newKey);

    /// <summary>Renames a key only when the target does not exist. Redis: RENAMENX.</summary>
    RespirePending<bool> TryRename(RespireKey key, RespireKey newKey);

    /// <summary>Copies a key, optionally replacing an existing target. Redis: COPY.</summary>
    RespirePending<bool> Copy(RespireKey source, RespireKey destination, bool replace = false);

    /// <summary>Copies a key into the requested database. Redis 6.2+: COPY DB.</summary>
    /// <remarks>Both keys receive the client prefix and must share a Cluster hash slot.
    /// Unsupported or out-of-range databases return server errors through the pending result.</remarks>
    RespirePending<bool> Copy(RespireKey source, RespireKey destination, int destinationDatabase, bool replace = false);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    RespirePending<long> Touch(params ReadOnlySpan<RespireKey> keys);
}

internal sealed partial class BatchKeyCommands(IPendingSink sink) : IBatchKeyCommands
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

    public RespirePending<RespireExpiryTime> ExpiryTime(RespireKey key)
        => ExpiryTime(key, ExpiryTimePrecision.Milliseconds);

    public RespirePending<RespireExpiryTime> ExpiryTime(RespireKey key, ExpiryTimePrecision precision)
    {
        var (operation, verb) = KeyCommands.ExpiryTimeCommand(precision);
        return sink.Add<Cmd1, RespireExpiryTime>(operation, new Cmd1(verb, sink.Client.Key(in key)),
            precision == ExpiryTimePrecision.Seconds
                ? static (c, v) => RespireExpiryTime.FromRedis(ResponseReader.Integer(in v), ExpiryTimePrecision.Seconds)
                : static (c, v) => RespireExpiryTime.FromRedis(ResponseReader.Integer(in v), ExpiryTimePrecision.Milliseconds));
    }

    public RespirePending<string?> Encoding(RespireKey key)
        => sink.Add<Cmd1, string?>("OBJECT ENCODING", new Cmd1(RespireCommands.Key.OBJECT_ENCODING.Verb, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.StringOrNull(in v));

    public RespirePending<TimeSpan?> IdleTime(RespireKey key)
        => sink.Add<Cmd1, TimeSpan?>("OBJECT IDLETIME", new Cmd1(RespireCommands.Key.OBJECT_IDLETIME.Verb, sink.Client.Key(in key)),
            static (c, v) => KeyCommands.ParseIdleTime(in v));

    public RespirePending<long?> Frequency(RespireKey key)
        => sink.Add<Cmd1, long?>("OBJECT FREQ", new Cmd1(RespireCommands.Key.OBJECT_FREQ.Verb, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.IntegerOrNull(in v));

    public RespirePending<long?> ReferenceCount(RespireKey key)
        => sink.Add<Cmd1, long?>("OBJECT REFCOUNT", new Cmd1(RespireCommands.Key.OBJECT_REFCOUNT.Verb, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.IntegerOrNull(in v));

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

    public RespirePending<bool> Copy(RespireKey source, RespireKey destination, int destinationDatabase, bool replace = false)
    {
        var (resolvedSource, resolvedDestination) = KeyCommands.CopyDatabaseKeys(sink.Client, source, destination, destinationDatabase);
        return replace
            ? sink.Add<Cmd5, bool>("COPY",
                new Cmd5(Verbs.Copy, resolvedSource, resolvedDestination, "DB", destinationDatabase, "REPLACE"),
                source, destination, static (c, v) => ResponseReader.Flag(in v))
            : sink.Add<Cmd4, bool>("COPY",
                new Cmd4(Verbs.Copy, resolvedSource, resolvedDestination, "DB", destinationDatabase),
                source, destination, static (c, v) => ResponseReader.Flag(in v));
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
