using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Generic key management commands.</summary>
public interface IKeyCommands
{
    /// <summary>Deletes keys; returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Deletes keys asynchronously on the server (non-blocking reclaim). Redis: UNLINK.</summary>
    ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Whether the key exists. Redis: EXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Sets a key's time to live. Returns false when the key is missing. Redis: PEXPIRE.</summary>
    ValueTask<bool> ExpireAsync(RespireKey key, TimeSpan expiry, CancellationToken cancellationToken = default);

    /// <summary>Sets an absolute expiry instant. Returns false when the key is missing. Redis: PEXPIREAT.</summary>
    ValueTask<bool> ExpireAtAsync(RespireKey key, DateTimeOffset expireAt, CancellationToken cancellationToken = default);

    /// <summary>Removes a key's expiry. Returns false when the key is missing or had none. Redis: PERSIST.</summary>
    ValueTask<bool> PersistAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// The key's expiry state — distinguishes missing key, no expiry, and remaining TTL. Redis: PTTL.
    /// </summary>
    ValueTask<RespireExpiry> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>The key's Redis type name ("string", "hash", …, or "none"). Redis: TYPE.</summary>
    ValueTask<string> TypeAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Renames a key, overwriting any existing target. Redis: RENAME.</summary>
    ValueTask RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default);

    /// <summary>Touches keys (updates access time); returns how many existed. Redis: TOUCH.</summary>
    ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>
    /// Iterates keys incrementally without blocking the server; the cursor is handled
    /// internally. Redis: SCAN.
    /// </summary>
    IAsyncEnumerable<string> ScanAsync(string? match = null, int pageSize = 250, CancellationToken cancellationToken = default);
}

internal sealed class KeyCommands(RespireClient client) : IKeyCommands
{
    public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("DEL", Verbs.Del, keys);

    public ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("UNLINK", Verbs.Unlink, keys);

    public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.FlagAsync("EXISTS", new Cmd1(Verbs.Exists, client.Key(in key)), cancellationToken);

    public ValueTask<bool> ExpireAsync(RespireKey key, TimeSpan expiry, CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "PEXPIRE", new Cmd2(Verbs.PExpire, client.Key(in key), (long)expiry.TotalMilliseconds), cancellationToken);

    public ValueTask<bool> ExpireAtAsync(RespireKey key, DateTimeOffset expireAt, CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "PEXPIREAT", new Cmd2(Verbs.PExpireAt, client.Key(in key), expireAt.ToUnixTimeMilliseconds()), cancellationToken);

    public ValueTask<bool> PersistAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.FlagAsync("PERSIST", new Cmd1(Verbs.Persist, client.Key(in key)), cancellationToken);

    public async ValueTask<RespireExpiry> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default)
        => RespireExpiry.FromPttl(
            await client.IntegerAsync("PTTL", new Cmd1(Verbs.Pttl, client.Key(in key)), cancellationToken).ConfigureAwait(false));

    public ValueTask<string> TypeAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.StringAsync("TYPE", new Cmd1(Verbs.Type, client.Key(in key)), cancellationToken);

    public ValueTask RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default)
        => client.OkAsync("RENAME", new Cmd2(Verbs.Rename, client.Key(in key), client.Key(in newKey)), cancellationToken);

    public ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys)
        => client.IntegerKeysAsync("TOUCH", Verbs.Touch, keys);

    public async IAsyncEnumerable<string> ScanAsync(
        string? match = null, int pageSize = 250, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A key-prefixed view scans inside its prefix and returns keys with the prefix stripped,
        // so results round-trip through the same view's commands. The prefix is glob-escaped —
        // a prefix like "tenant:*:" must match itself literally, never act as a wildcard.
        var prefix = client.KeyPrefix;
        var effectiveMatch = prefix is null ? match : EscapeGlob(prefix) + (match ?? "*");

        var cursor = "0";
        do
        {
            var args = effectiveMatch is null
                ? new RespireValue[] { cursor, "COUNT", pageSize }
                : new RespireValue[] { cursor, "MATCH", effectiveMatch, "COUNT", pageSize };
            var reply = await client.SendAsync("SCAN", new CmdN(Verbs.Scan, args), cancellationToken).ConfigureAwait(false);

            string[] page;
            var elements = reply.AsArray();
            cursor = elements[0].AsString();
            page = ResponseReader.StringArray(in elements[1]);
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
