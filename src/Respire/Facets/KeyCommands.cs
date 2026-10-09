using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>Condition for key or hash field expiry updates. Redis: PEXPIRE/PEXPIREAT/HPEXPIRE/HPEXPIREAT.</summary>
public enum ExpireWhen
{
    /// <summary>Set or update the expiry unconditionally.</summary>
    Always,

    /// <summary>Only set expiry when the key or hash field has no expiry. Redis: NX.</summary>
    NotExists,

    /// <summary>Only set expiry when the key or hash field already has an expiry. Redis: XX.</summary>
    Exists,

    /// <summary>Only set expiry when the new expiry is greater than the current expiry. Redis: GT.</summary>
    GreaterThan,

    /// <summary>Only set expiry when the new expiry is less than the current expiry. Redis: LT.</summary>
    LessThan,
}

/// <summary>The data structure stored at a Redis key.</summary>
public enum RespireKeyType
{
    /// <summary>The server returned a key type this Respire version does not recognize.</summary>
    Unknown,

    /// <summary>The key does not exist.</summary>
    None,

    /// <summary>A string value.</summary>
    String,

    /// <summary>A list.</summary>
    List,

    /// <summary>A set.</summary>
    Set,

    /// <summary>A sorted set.</summary>
    SortedSet,

    /// <summary>A hash.</summary>
    Hash,

    /// <summary>A stream.</summary>
    Stream,

    /// <summary>A Redis vector set.</summary>
    VectorSet,

    /// <summary>A Redis sparse array.</summary>
    Array,
}

/// <summary>Generic key management commands.</summary>
public partial interface IKeyCommands
{
    /// <summary>Deletes keys; returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Deletes keys; returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Deletes keys asynchronously on the server (non-blocking reclaim). Redis: UNLINK.</summary>
    ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Deletes keys asynchronously on the server (non-blocking reclaim). Redis: UNLINK.</summary>
    ValueTask<long> UnlinkAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Whether the key exists. Redis: EXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets, updates, or removes a key's expiry. Returns false when the key is missing or the
    /// condition is not met. Redis: PEXPIRE/PEXPIREAT/PERSIST.
    /// </summary>
    ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The key's expiry state — distinguishes missing key, no expiry, and remaining TTL. Redis: PTTL.
    /// </summary>
    ValueTask<RespireTtl> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Absolute expiration, distinguishing missing and persistent keys. Redis 7+: PEXPIRETIME.</summary>
    ValueTask<RespireExpiryTime> ExpiryTimeAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Absolute expiration at the requested server precision. Redis 7+: EXPIRETIME/PEXPIRETIME.</summary>
    ValueTask<RespireExpiryTime> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, CancellationToken cancellationToken = default);

    /// <summary>The server's internal value encoding, or null for a missing key. Redis: OBJECT ENCODING.</summary>
    ValueTask<string?> EncodingAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Time since last access, with second resolution, or null for a missing key. Redis: OBJECT IDLETIME.</summary>
    /// <remarks>Redis rejects this command under LFU eviction policies.</remarks>
    ValueTask<TimeSpan?> IdleTimeAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>The logarithmic access frequency counter, or null for a missing key. Redis: OBJECT FREQ.</summary>
    /// <remarks>Requires an LFU eviction policy; otherwise Redis returns an error. This is not an exact access count.</remarks>
    ValueTask<long?> FrequencyAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>The server's internal value reference count, or null for a missing key. Redis: OBJECT REFCOUNT.</summary>
    ValueTask<long?> ReferenceCountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>The data structure stored at a key, or <see cref="RespireKeyType.None"/>. Redis: TYPE.</summary>
    ValueTask<RespireKeyType> TypeAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Renames a key; returns true on OK. Redis: RENAME.</summary>
    ValueTask<bool> RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default);

    /// <summary>Renames a key only when the target does not exist. Redis: RENAMENX.</summary>
    ValueTask<bool> TryRenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default);

    /// <summary>Copies a key, optionally replacing an existing target. Redis: COPY.</summary>
    ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        bool replace = false,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a key into the requested database, optionally replacing its destination. Redis 6.2+: COPY DB.</summary>
    /// <remarks>Both keys receive the client prefix. Cluster keys must share a hash slot.
    /// The server must support the requested database; unsupported or out-of-range databases return server errors.</remarks>
    ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        int destinationDatabase,
        bool replace = false,
        CancellationToken cancellationToken = default);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    ValueTask<long> TouchAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Reads one resumable page across Cluster primaries, selecting CLUSTERSCAN when supported.</summary>
    /// <remarks>Start with RespireClusterScanCursor.Start. Preserve database, match, type and the client's
    /// key prefix when resuming. Requires SCAN, CLUSTER SLOTS, CLUSTER NODES and INFO permissions.
    /// Modern selection also uses COMMAND INFO and CLUSTERSCAN; unknown metadata permits an uncached fallback.
    /// A failed call leaves its input cursor usable. Retry that cursor after transient failures.
    /// Duplicate keys and empty incomplete pages are permitted, especially during resharding.</remarks>
    ValueTask<RespireClusterScanPage> ScanClusterPageAsync(
        RespireClusterScanCursor cursor,
        string? match = null,
        RespireKeyType? type = null,
        int countHint = 250,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Iterates keys incrementally without blocking the server; the cursor is handled
    /// internally. In cluster mode, slot progress is validated as ownership changes.
    /// Redis: SCAN.
    /// </summary>
    IAsyncEnumerable<string> ScanAsync(
        string? match = null,
        RespireKeyType? type = null,
        int countHint = 250,
        CancellationToken cancellationToken = default);
}

internal sealed partial class KeyCommands(RespireClient client, TimeProvider? scanTimeProvider = null) : IKeyCommands
{
    public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(DeleteBorrowedAsync(keys, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> DeleteBorrowedAsync(ReadOnlySpan<RespireKey> keys, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("DEL", Verbs.Del, keys, CancellationToken.None, observation: observation);

    public ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(DeleteBorrowedAsync(keys, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> DeleteBorrowedAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("DEL", Verbs.Del, keys, cancellationToken, observation: observation);

    public ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(UnlinkBorrowedAsync(keys, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> UnlinkBorrowedAsync(ReadOnlySpan<RespireKey> keys, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("UNLINK", Verbs.Unlink, keys, CancellationToken.None, observation: observation);

    public ValueTask<long> UnlinkAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(UnlinkBorrowedAsync(keys, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> UnlinkBorrowedAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("UNLINK", Verbs.Unlink, keys, cancellationToken, observation: observation);

    public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(ExistsBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> ExistsBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.FlagAsync("EXISTS", new Cmd1(Verbs.Exists, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(ExpireBorrowedAsync(key, expiry, when, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> ExpireBorrowedAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var condition = ExpireWhenToken(when);
        if (expiry.IsPersist)
        {
            if (condition is not null)
            {
                throw new ArgumentException("PERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return client.FlagAsync("PERSIST", new Cmd1(Verbs.Persist, client.Key(in key)), cancellationToken, observation: observation);
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return condition is null
                ? client.FlagAsync(
                    "PEXPIRE", new Cmd2(Verbs.PExpire, client.Key(in key), milliseconds), cancellationToken, observation: observation)
                : client.FlagAsync(
                    "PEXPIRE", new Cmd3(Verbs.PExpire, client.Key(in key), milliseconds, condition), cancellationToken, observation: observation);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return condition is null
                ? client.FlagAsync(
                    "PEXPIREAT", new Cmd2(Verbs.PExpireAt, client.Key(in key), unixMilliseconds), cancellationToken, observation: observation)
                : client.FlagAsync(
                    "PEXPIREAT", new Cmd3(Verbs.PExpireAt, client.Key(in key), unixMilliseconds, condition), cancellationToken, observation: observation);
        }

        throw new ArgumentException(
            "Key expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public ValueTask<RespireTtl> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireTtl>.Start();
        try { return owner.Attach(ExpiryBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireTtl> ExpiryBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync(
            "PTTL", new Cmd1(Verbs.Pttl, client.Key(in key)), cancellationToken, this,
            static (KeyCommands _, in RespValue value) =>
                RespireTtl.FromRedisMilliseconds(ResponseReader.Integer(in value)), observation: observation);

    public ValueTask<RespireExpiryTime> ExpiryTimeAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireExpiryTime>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime> ExpiryTimeBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => ExpiryTimeBorrowedAsync(key, ExpiryTimePrecision.Milliseconds, cancellationToken, observation);

    public ValueTask<RespireExpiryTime> ExpiryTimeAsync(
        RespireKey key, ExpiryTimePrecision precision, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireExpiryTime>.Start();
        try { return owner.Attach(ExpiryTimeBorrowedAsync(key, precision, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireExpiryTime> ExpiryTimeBorrowedAsync(
        RespireKey key, ExpiryTimePrecision precision, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var (operation, verb) = ExpiryTimeCommand(precision);
        return client.ConvertResponseAsync(operation, new Cmd1(verb, client.Key(in key)), cancellationToken, precision,
            static (ExpiryTimePrecision p, in RespValue value) => RespireExpiryTime.FromRedis(ResponseReader.Integer(in value), p), observation: observation);
    }

    public ValueTask<string?> EncodingAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<string?>.Start();
        try { return owner.Attach(EncodingBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<string?> EncodingBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.StringOrNullAsync("OBJECT ENCODING", new Cmd1(RespireCommands.Key.OBJECT_ENCODING.Verb, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<TimeSpan?> IdleTimeAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<TimeSpan?>.Start();
        try { return owner.Attach(IdleTimeBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<TimeSpan?> IdleTimeBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync("OBJECT IDLETIME", new Cmd1(RespireCommands.Key.OBJECT_IDLETIME.Verb, client.Key(in key)), cancellationToken, this,
            static (KeyCommands _, in RespValue value) => ParseIdleTime(in value), observation: observation);

    public ValueTask<long?> FrequencyAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long?>.Start();
        try { return owner.Attach(FrequencyBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long?> FrequencyBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerOrNullAsync("OBJECT FREQ", new Cmd1(RespireCommands.Key.OBJECT_FREQ.Verb, client.Key(in key)), cancellationToken, observation: observation);

    public ValueTask<long?> ReferenceCountAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<long?>.Start();
        try { return owner.Attach(ReferenceCountBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long?> ReferenceCountBorrowedAsync(RespireKey key, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerOrNullAsync("OBJECT REFCOUNT", new Cmd1(RespireCommands.Key.OBJECT_REFCOUNT.Verb, client.Key(in key)), cancellationToken, observation: observation);

    internal static TimeSpan? ParseIdleTime(in RespValue value)
        => value.IsNull ? null : TimeSpan.FromSeconds(ResponseReader.Integer(in value));

    internal static (string Operation, Verb Verb) ExpiryTimeCommand(ExpiryTimePrecision precision)
        => precision switch
        {
            ExpiryTimePrecision.Milliseconds => ("PEXPIRETIME", RespireCommands.Key.PEXPIRETIME.Verb),
            ExpiryTimePrecision.Seconds => ("EXPIRETIME", RespireCommands.Key.EXPIRETIME.Verb),
            _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, null),
        };

    public ValueTask<RespireKeyType> TypeAsync(
        RespireKey key,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<RespireKeyType>.Start();
        try { return owner.Attach(TypeBorrowedAsync(key, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<RespireKeyType> TypeBorrowedAsync(
        RespireKey key,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.ConvertResponseAsync(
            "TYPE", new Cmd1(Verbs.Type, client.Key(in key)), cancellationToken, this,
            static (KeyCommands _, in RespValue value) => ParseKeyType(ResponseReader.String(in value)), observation: observation);

    public ValueTask<bool> RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(RenameBorrowedAsync(key, newKey, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> RenameBorrowedAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.OkResultAsync(
            "RENAME", new Cmd2(Verbs.Rename, client.Key(in key), client.Key(in newKey)), cancellationToken, observation: observation);

    public ValueTask<bool> TryRenameAsync(
        RespireKey key,
        RespireKey newKey,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(TryRenameBorrowedAsync(key, newKey, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> TryRenameBorrowedAsync(
        RespireKey key,
        RespireKey newKey,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.FlagAsync(
            "RENAMENX", new Cmd2(Verbs.RenameNx, client.Key(in key), client.Key(in newKey)), cancellationToken, observation: observation);

    public ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(CopyBorrowedAsync(source, destination, replace, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> CopyBorrowedAsync(
        RespireKey source,
        RespireKey destination,
        bool replace,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => replace
            ? client.FlagAsync(
                "COPY", new Cmd3(Verbs.Copy, client.Key(in source), client.Key(in destination), "REPLACE"),
                cancellationToken, observation: observation)
            : client.FlagAsync(
                "COPY", new Cmd2(Verbs.Copy, client.Key(in source), client.Key(in destination)),
                cancellationToken, observation: observation);

    public ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        int destinationDatabase,
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try { return owner.Attach(CopyBorrowedAsync(source, destination, destinationDatabase, replace, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<bool> CopyBorrowedAsync(
        RespireKey source,
        RespireKey destination,
        int destinationDatabase,
        bool replace,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        var (resolvedSource, resolvedDestination) = CopyDatabaseKeys(client, source, destination, destinationDatabase);
        return replace
            ? client.FlagAsync("COPY",
                new Cmd5(Verbs.Copy, resolvedSource, resolvedDestination, "DB", destinationDatabase, "REPLACE"),
                cancellationToken, observation: observation)
            : client.FlagAsync("COPY",
                new Cmd4(Verbs.Copy, resolvedSource, resolvedDestination, "DB", destinationDatabase),
                cancellationToken, observation: observation);
    }

    internal static (RespireValue Source, RespireValue Destination) CopyDatabaseKeys(
        RespireClient client, RespireKey source, RespireKey destination, int destinationDatabase)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(destinationDatabase);
        var resolvedSource = client.Key(in source);
        var resolvedDestination = client.Key(in destination);
        if (client.Core.Cluster is not null && resolvedSource.AsKey().ClusterSlot != resolvedDestination.AsKey().ClusterSlot)
            throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", "COPY");
        return (resolvedSource, resolvedDestination);
    }

    public ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(TouchBorrowedAsync(keys, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> TouchBorrowedAsync(ReadOnlySpan<RespireKey> keys, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("TOUCH", Verbs.Touch, keys, CancellationToken.None, observation: observation);

    public ValueTask<long> TouchAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
    {
        var owner = DispatchResponseSource<long>.Start();
        try { return owner.Attach(TouchBorrowedAsync(keys, cancellationToken, owner.Observation)); }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    private ValueTask<long> TouchBorrowedAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        => client.IntegerKeysAsync("TOUCH", Verbs.Touch, keys, cancellationToken, observation: observation);

    internal static string? ExpireWhenToken(ExpireWhen when)
        => when switch
        {
            ExpireWhen.Always => null,
            ExpireWhen.NotExists => "NX",
            ExpireWhen.Exists => "XX",
            ExpireWhen.GreaterThan => "GT",
            ExpireWhen.LessThan => "LT",
            _ => throw new ArgumentOutOfRangeException(nameof(when), when, null),
        };

    public async IAsyncEnumerable<string> ScanAsync(
        string? match = null,
        RespireKeyType? type = null,
        int countHint = 250,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? typeToken;
        RespireValue? effectiveMatch;
        var prefix = client.KeyPrefix;
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
            typeToken = FormatKeyType(type);
            // A prefixed scan matches the literal prefix and returns keys that round-trip through this view.
            effectiveMatch = ScanMatch(prefix, match);
        }
        catch (Exception error)
        {
            ErrorObservation.FinishFinal(default, error);
            throw;
        }

        if (client.Core.Cluster is not null)
        {
            var checkpoint = RespireClusterScanCursor.Start;
            var migrationDelayMs = 50;
            do
            {
                CollectionScan.CheckCancellation(cancellationToken);
                var page = await ScanClusterPageAsync(checkpoint, match, type, countHint, cancellationToken).ConfigureAwait(false);
                foreach (var key in page.Keys) yield return key;
                checkpoint = page.Cursor;
                if (page.WaitingOnMigration)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(migrationDelayMs), scanTimeProvider ?? TimeProvider.System,
                        cancellationToken).ConfigureAwait(false);
                    migrationDelayMs = Math.Min(migrationDelayMs * 2, 250);
                }
                else migrationDelayMs = 50;
            }
            while (!checkpoint.IsComplete);

            yield break;
        }

        await foreach (var key in ScanStandaloneAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return key;
        }

        async IAsyncEnumerable<string> ScanStandaloneAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            var cursor = "0";
            // Every page of this enumeration returns to the server that issued its cursor.
            var affinity = new ReadAffinity();
            do
            {
                CollectionScan.CheckCancellation(token);
                var args = (effectiveMatch, typeToken) switch
                {
                    (null, null) => new RespireValue[] { cursor, "COUNT", countHint },
                    (not null, null) => [cursor, "MATCH", effectiveMatch.Value, "COUNT", countHint],
                    (null, not null) => [cursor, "COUNT", countHint, "TYPE", typeToken],
                    _ => [cursor, "MATCH", effectiveMatch!.Value, "COUNT", countHint, "TYPE", typeToken],
                };
                var command = new CmdN(Verbs.Scan, args);
                var result = await client.ConvertCursorPageAsync("SCAN", command, affinity, token, prefix,
                    static (KeyPrefix? prefix, in RespValue reply) =>
                    {
                        var elements = CollectionScan.ParsePage(in reply, "SCAN", out _);
                        string[] page;
                        int pageCount;
                        if (prefix is null)
                        {
                            page = ResponseReader.StringArray(in elements[1]);
                            pageCount = page.Length;
                        }
                        else
                        {
                            var values = elements[1].AsArray();
                            page = new string[values.Length];
                            pageCount = 0;
                            foreach (ref readonly var value in values)
                                if (ScanKey(in value, prefix) is { } key) page[pageCount++] = key;
                        }
                        return (Cursor: elements[0].AsString(), Page: page, Count: pageCount);
                    }).ConfigureAwait(false);
                cursor = result.Cursor;

                for (var index = 0; index < result.Count; index++)
                {
                    yield return result.Page[index];
                }
            }
            while (cursor != "0");
        }
    }

    internal static RespireKeyType ParseKeyType(string value)
        => value switch
        {
            "none" => RespireKeyType.None,
            "string" => RespireKeyType.String,
            "list" => RespireKeyType.List,
            "set" => RespireKeyType.Set,
            "zset" => RespireKeyType.SortedSet,
            "hash" => RespireKeyType.Hash,
            "stream" => RespireKeyType.Stream,
            "vectorset" => RespireKeyType.VectorSet,
            "array" => RespireKeyType.Array,
            _ => RespireKeyType.Unknown,
        };

    private static string? FormatKeyType(RespireKeyType? type)
        => type switch
        {
            null => null,
            RespireKeyType.String => "string",
            RespireKeyType.List => "list",
            RespireKeyType.Set => "set",
            RespireKeyType.SortedSet => "zset",
            RespireKeyType.Hash => "hash",
            RespireKeyType.Stream => "stream",
            RespireKeyType.VectorSet => "vectorset",
            RespireKeyType.Array => "array",
            _ => throw new ArgumentOutOfRangeException(
                nameof(type), type, "SCAN TYPE requires a concrete Redis key type."),
        };

    private static RespireValue? ScanMatch(KeyPrefix? prefix, string? match)
    {
        if (prefix is null) return match is null ? (RespireValue?)null : new RespireValue(match);
        if (prefix.Text is { } text) return new RespireValue(EscapeGlob(text) + (match ?? "*"));
        var suffix = System.Text.Encoding.UTF8.GetBytes(match ?? "*");
        var escapedLength = prefix.Bytes.Length;
        foreach (var value in prefix.Bytes)
            if (IsGlobMeta(value)) escapedLength++;
        var pattern = new byte[checked(escapedLength + suffix.Length)];
        var offset = 0;
        foreach (var value in prefix.Bytes)
        {
            if (IsGlobMeta(value)) pattern[offset++] = (byte)'\\';
            pattern[offset++] = value;
        }
        suffix.CopyTo(pattern, offset);
        return new RespireValue(pattern);
    }

    private static string? ScanKey(in RespValue value, KeyPrefix prefix)
    {
        if (prefix.Text is { } text)
        {
            // Preserve text-prefix behavior, including UTF-16 pairs split at the prefix boundary.
            var key = value.AsString();
            return key.StartsWith(text, StringComparison.Ordinal) ? key[text.Length..] : null;
        }
        var bytes = value.AsSpan();
        return bytes.StartsWith(prefix.Bytes) ? Utf8String.GetString(bytes[prefix.Bytes.Length..]) : null;
    }

    /// <summary>Escapes Redis glob metacharacters so the text matches itself literally.</summary>
    private static string EscapeGlob(string value)
    {
        if (value.AsSpan().IndexOfAny(@"*?[]\") < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (IsGlobMeta(c))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsGlobMeta(int value) => value is '*' or '?' or '[' or ']' or '\\';
}
