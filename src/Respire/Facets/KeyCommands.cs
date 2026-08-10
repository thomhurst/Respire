using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Condition for key expiry updates. Redis: PEXPIRE/PEXPIREAT.</summary>
public enum ExpireWhen
{
    /// <summary>Set or update the expiry unconditionally.</summary>
    Always,

    /// <summary>Only set expiry when the key has no expiry. Redis: NX.</summary>
    NotExists,

    /// <summary>Only set expiry when the key already has an expiry. Redis: XX.</summary>
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
}

/// <summary>Generic key management commands.</summary>
public interface IKeyCommands
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

    /// <summary>The data structure stored at a key, or <see cref="RespireKeyType.None"/>. Redis: TYPE.</summary>
    ValueTask<RespireKeyType> TypeAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Renames a key. Redis: RENAME.</summary>
    ValueTask RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default);

    /// <summary>Renames a key only when the target does not exist. Redis: RENAMENX.</summary>
    ValueTask<bool> TryRenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default);

    /// <summary>Copies a key, optionally replacing an existing target. Redis: COPY.</summary>
    ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        bool replace = false,
        CancellationToken cancellationToken = default);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    ValueTask<long> TouchAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Iterates keys incrementally without blocking the server; the cursor is handled
    /// internally. In cluster mode, every known master is scanned with its own cursor.
    /// Redis: SCAN.
    /// </summary>
    IAsyncEnumerable<string> ScanAsync(
        string? match = null,
        int countHint = 250,
        string? type = null,
        CancellationToken cancellationToken = default);
}

internal sealed class KeyCommands(RespireClient client) : IKeyCommands
{
    public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("DEL", Verbs.Del, keys, CancellationToken.None);

    public ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerKeysAsync("DEL", Verbs.Del, keys, cancellationToken);

    public ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("UNLINK", Verbs.Unlink, keys, CancellationToken.None);

    public ValueTask<long> UnlinkAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerKeysAsync("UNLINK", Verbs.Unlink, keys, cancellationToken);

    public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.FlagAsync("EXISTS", new Cmd1(Verbs.Exists, client.Key(in key)), cancellationToken);

    public ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default)
    {
        var condition = ExpireWhenToken(when);
        if (expiry.IsPersist)
        {
            if (condition is not null)
            {
                throw new ArgumentException("PERSIST does not support NX, XX, GT, or LT.", nameof(when));
            }

            return client.FlagAsync("PERSIST", new Cmd1(Verbs.Persist, client.Key(in key)), cancellationToken);
        }

        if (expiry.TryGetRelativeMilliseconds(out var milliseconds))
        {
            return condition is null
                ? client.FlagAsync(
                    "PEXPIRE", new Cmd2(Verbs.PExpire, client.Key(in key), milliseconds), cancellationToken)
                : client.FlagAsync(
                    "PEXPIRE", new Cmd3(Verbs.PExpire, client.Key(in key), milliseconds, condition), cancellationToken);
        }

        if (expiry.TryGetAbsoluteUnixMilliseconds(out var unixMilliseconds))
        {
            return condition is null
                ? client.FlagAsync(
                    "PEXPIREAT", new Cmd2(Verbs.PExpireAt, client.Key(in key), unixMilliseconds), cancellationToken)
                : client.FlagAsync(
                    "PEXPIREAT", new Cmd3(Verbs.PExpireAt, client.Key(in key), unixMilliseconds, condition), cancellationToken);
        }

        throw new ArgumentException(
            "Key expiry must be relative, absolute, or RespireExpiry.Persist.", nameof(expiry));
    }

    public async ValueTask<RespireTtl> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default)
        => RespireTtl.FromRedisMilliseconds(
            await client.IntegerAsync("PTTL", new Cmd1(Verbs.Pttl, client.Key(in key)), cancellationToken).ConfigureAwait(false));

    public async ValueTask<RespireKeyType> TypeAsync(
        RespireKey key,
        CancellationToken cancellationToken = default)
        => ParseKeyType(await client.StringAsync(
            "TYPE", new Cmd1(Verbs.Type, client.Key(in key)), cancellationToken).ConfigureAwait(false));

    public ValueTask RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default)
        => client.OkAsync(
            "RENAME", new Cmd2(Verbs.Rename, client.Key(in key), client.Key(in newKey)), cancellationToken);

    public ValueTask<bool> TryRenameAsync(
        RespireKey key,
        RespireKey newKey,
        CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "RENAMENX", new Cmd2(Verbs.RenameNx, client.Key(in key), client.Key(in newKey)), cancellationToken);

    public ValueTask<bool> CopyAsync(
        RespireKey source,
        RespireKey destination,
        bool replace = false,
        CancellationToken cancellationToken = default)
        => replace
            ? client.FlagAsync(
                "COPY", new Cmd3(Verbs.Copy, client.Key(in source), client.Key(in destination), "REPLACE"),
                cancellationToken)
            : client.FlagAsync(
                "COPY", new Cmd2(Verbs.Copy, client.Key(in source), client.Key(in destination)),
                cancellationToken);

    public ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("TOUCH", Verbs.Touch, keys, CancellationToken.None);

    public ValueTask<long> TouchAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.IntegerKeysAsync("TOUCH", Verbs.Touch, keys, cancellationToken);

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
        int countHint = 250,
        string? type = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
        if (type is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
        }

        // A key-prefixed view scans inside its prefix and returns keys with the prefix stripped,
        // so results round-trip through the same view's commands. The prefix is glob-escaped —
        // a prefix like "tenant:*:" must match itself literally, never act as a wildcard.
        var prefix = client.KeyPrefix;
        var effectiveMatch = prefix is null ? match : EscapeGlob(prefix) + (match ?? "*");

        if (client.Core.Cluster is { } cluster)
        {
            var masters = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var master in masters)
            {
                await foreach (var key in ScanNodeAsync(master, cancellationToken).ConfigureAwait(false))
                {
                    yield return key;
                }
            }

            yield break;
        }

        await foreach (var key in ScanNodeAsync(connection: null, cancellationToken).ConfigureAwait(false))
        {
            yield return key;
        }

        async IAsyncEnumerable<string> ScanNodeAsync(
            Respire.Networking.RespireConnection? connection,
            [EnumeratorCancellation] CancellationToken token)
        {
            var cursor = "0";
            do
            {
                var args = (effectiveMatch, type) switch
                {
                    (null, null) => new RespireValue[] { cursor, "COUNT", countHint },
                    (not null, null) => [cursor, "MATCH", effectiveMatch, "COUNT", countHint],
                    (null, not null) => [cursor, "COUNT", countHint, "TYPE", type],
                    _ => [cursor, "MATCH", effectiveMatch, "COUNT", countHint, "TYPE", type],
                };
                var command = new CmdN(Verbs.Scan, args);
                var reply = connection is null
                    ? await client.SendAsync("SCAN", command, token).ConfigureAwait(false)
                    : await client.SendOnConnectionAsync("SCAN", connection, command, token).ConfigureAwait(false);

                var elements = reply.AsArray();
                cursor = elements[0].AsString();
                var page = ResponseReader.StringArray(in elements[1]);
                reply.Dispose();

                foreach (var key in page)
                {
                    if (prefix is null)
                    {
                        yield return key;
                    }
                    else if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        yield return key[prefix.Length..];
                    }

                    // Keys outside the literal prefix never leave a prefixed view.
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
            _ => RespireKeyType.Unknown,
        };

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
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
