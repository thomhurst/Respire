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
        CancellationToken cancellationToken = default);
}

internal sealed partial class KeyCommands
{
    public async ValueTask<RespireValkeyClusterScanPage> ScanValkeyClusterPageAsync(string cursor = "0",
        string? match = null, RespireKeyType? type = null, int countHint = 250, int? slot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countHint);
        if (slot is < 0 or >= ClusterHash.SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var typeToken = FormatKeyType(type);
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        cancellationToken.ThrowIfCancellationRequested();
        if (client.Core.Cluster is null) throw new InvalidOperationException("CLUSTERSCAN requires UseCluster.");
        var prefix = client.KeyPrefix;
        var effectiveMatch = prefix is null ? match : EscapeGlob(prefix) + (match ?? "*");
        List<RespireValue> arguments = [cursor];
        if (effectiveMatch is not null) arguments.AddRange(["MATCH", effectiveMatch]);
        arguments.AddRange(["COUNT", countHint]);
        if (typeToken is not null) arguments.AddRange(["TYPE", typeToken]);
        if (slot is { } selectedSlot) arguments.AddRange(["SLOT", selectedSlot]);
        // The server cursor carries a physical hash tag. Never resolve it through client.Key().
        var routingSlot = cursor == "0" ? slot ?? 0 : ClusterHash.GetSlot(cursor);
        using var reply = await client.SendAsync("CLUSTERSCAN",
            new ValkeyClusterScanCommand(arguments.ToArray(), routingSlot), cancellationToken).ConfigureAwait(false);
        return ValkeyClusterScanParser.Parse(in reply, prefix);
    }

    private readonly struct ValkeyClusterScanCommand(RespireValue[] arguments, int routingSlot) : IRespCommand
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
        var prefixBytes = prefix is null ? [] : Encoding.UTF8.GetBytes(prefix);
        List<RespireKey> keys = [];
        foreach (ref readonly var key in fields[1].AsArray())
        {
            if (key.Type != RespDataType.BulkString) throw new RespireProtocolException("CLUSTERSCAN keys must be bulk strings.");
            var bytes = key.AsSpan();
            if (bytes.StartsWith(prefixBytes)) keys.Add(new RespireKey(bytes[prefixBytes.Length..].ToArray()));
        }
        return new(cursor, keys.ToArray());
    }
}
