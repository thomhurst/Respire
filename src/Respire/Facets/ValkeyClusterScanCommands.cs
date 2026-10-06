using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An owned Valkey CLUSTERSCAN page with an opaque server cursor and binary-safe keys.</summary>
/// <remarks>Keys from a prefixed view have that literal prefix removed and can be passed back to the same view.
/// Empty pages are permitted before completion. COUNT is a work hint, not a result-size limit.</remarks>
public sealed record RespireValkeyClusterScanPage(string Cursor, IReadOnlyList<RespireKey> Keys)
{
    /// <summary>Whether the server returned the terminal cursor, "0".</summary>
    public bool IsComplete => Cursor == "0";
}

public partial interface IKeyCommands
{
    /// <summary>Reads one owned page using Valkey 9.1+ CLUSTERSCAN. Requires UseCluster.</summary>
    /// <remarks>Start with "0" and pass each returned cursor unchanged. Keep the database, match, type,
    /// slot and key prefix unchanged while resuming. Uses primaries regardless of ReadFrom; normal MOVED/ASK
    /// handling applies. SLOT is a physical slot and is not prefixed. Duplicate keys and empty pages are valid.
    /// Older servers return their unsupported-command error; no legacy SCAN fallback is performed here.</remarks>
    ValueTask<RespireValkeyClusterScanPage> ScanValkeyClusterPageAsync(string cursor = "0",
        string? match = null, RespireKeyType? type = null, int countHint = 250, int? slot = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This implementation does not support CLUSTERSCAN.");
}

internal sealed partial class KeyCommands
{
    public async ValueTask<RespireValkeyClusterScanPage> ScanValkeyClusterPageAsync(string cursor = "0",
        string? match = null, RespireKeyType? type = null, int countHint = 250, int? slot = null,
        CancellationToken cancellationToken = default)
    {
        var command = CreateValkeyClusterScanCommand(client, cursor, match, type, countHint, slot, cancellationToken);
        using var reply = await client.SendAsync("CLUSTERSCAN", command, cancellationToken).ConfigureAwait(false);
        return ValkeyClusterScanParser.ParseWithPrefix(in reply, client.EncodedKeyPrefix, match);
    }

    internal static ValkeyClusterScanCommand CreateValkeyClusterScanCommand(RespireClient client,
        string cursor, string? match, RespireKeyType? type, int countHint, int? slot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
        if (slot is < 0 or >= ClusterHash.SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var typeToken = FormatKeyType(type);
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        cancellationToken.ThrowIfCancellationRequested();
        if (client.Core.Cluster is null) throw new InvalidOperationException("CLUSTERSCAN requires UseCluster.");
        var prefix = client.EncodedKeyPrefix;
        // A trailing high surrogate has both paired-text and replacement-byte encodings.
        // No single literal UTF-8 prefix covers both. Filter that namespace after scanning.
        var effectiveMatch = match;
        if (prefix is not null)
            effectiveMatch = prefix.HasSurrogateBoundary ? null : EscapeGlob(prefix.Text) + (match ?? "*");
        List<RespireValue> arguments = [cursor];
        if (effectiveMatch is not null) arguments.AddRange(["MATCH", effectiveMatch]);
        arguments.AddRange(["COUNT", countHint]);
        if (typeToken is not null) arguments.AddRange(["TYPE", typeToken]);
        if (slot is { } selectedSlot) arguments.AddRange(["SLOT", selectedSlot]);
        // The server cursor carries a physical hash tag. Never resolve it through client.Key().
        var routingSlot = cursor == "0" ? slot ?? 0 : ClusterHash.GetSlot(cursor);
        return new ValkeyClusterScanCommand(arguments.ToArray(), routingSlot);
    }

    internal readonly struct ValkeyClusterScanCommand(RespireValue[] arguments, int routingSlot) : IRespCommand
    {
        private static readonly Verb ScanVerb = new("CLUSTERSCAN", allowReadRouting: false);
        // Replica rotation could repeatedly restart the server's fingerprinted cursor.
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public bool TryGetClusterSlot(out int slot) { slot = routingSlot; return true; }
        public void Write(ref RespWriter writer) => new CmdN(ScanVerb, arguments).Write(ref writer);
    }
}

internal static class ValkeyClusterScanParser
{
    private static readonly UTF8Encoding CursorEncoding = new(false, true);

    internal static RespireValkeyClusterScanPage Parse(in RespValue reply, string? prefix)
        => ParseWithPrefix(in reply, prefix is null ? null : new KeyPrefix(prefix));

    internal static RespireValkeyClusterScanPage ParseWithPrefix(in RespValue reply, KeyPrefix? prefix, string? match = null)
    {
        if (reply.Type != RespDataType.Array || reply.AsArray().Length != 2)
            throw new RespireProtocolException("CLUSTERSCAN must return a cursor and a key array.");
        var fields = reply.AsArray();
        if (fields[0].Type != RespDataType.BulkString || fields[0].AsSpan().IsEmpty || fields[1].Type != RespDataType.Array)
            throw new RespireProtocolException("CLUSTERSCAN returned an invalid cursor or key array.");
        string cursor;
        try { cursor = CursorEncoding.GetString(fields[0].AsSpan()); }
        catch (DecoderFallbackException error)
        {
            throw new RespireProtocolException("CLUSTERSCAN cursor is not valid UTF-8.", error);
        }
        var values = fields[1].AsArray();
        var pattern = prefix is { HasSurrogateBoundary: true } && match is not null
            ? Encoding.UTF8.GetBytes(match) : null;
        var keys = new List<RespireKey>(values.Length);
        foreach (ref readonly var key in values)
        {
            if (key.Type != RespDataType.BulkString) throw new RespireProtocolException("CLUSTERSCAN keys must be bulk strings.");
            var bytes = key.AsSpan();
            RespireKey owned;
            if (prefix is null) owned = new RespireKey(bytes.ToArray());
            else if (!prefix.TryStripScanKey(bytes, out owned)) continue;
            if (pattern is null || ByteGlob.IsMatch(owned.ToBytes(), pattern)) keys.Add(owned);
        }
        return new(cursor, keys.ToArray());
    }
}
