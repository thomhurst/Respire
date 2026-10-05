using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An owned HSCAN NOVALUES page. Cursor zero marks completion; an empty page need not be complete.</summary>
public readonly record struct RespireHashScanPage(ulong Cursor, string[] Fields)
{
    /// <summary>Whether this page completes the scan.</summary>
    public bool IsComplete => Cursor == 0;
}

public partial interface IHashCommands
{
    /// <summary>Enumerates field names without values using HSCAN NOVALUES. Requires Redis 7.4 or later.</summary>
    /// <remarks>COUNT is a hint. Scans are not snapshots and may return duplicates during mutation.</remarks>
    IAsyncEnumerable<string> ScanFieldsAsync(
        RespireKey key, string? match = null, int countHint = 250, CancellationToken cancellationToken = default);

    /// <summary>Reads one HSCAN NOVALUES page. Start with cursor zero; pass the returned cursor to continue.</summary>
    /// <remarks>Requires Redis 7.4. Keep the same key, server, and read policy across pages; restart after topology changes.</remarks>
    ValueTask<RespireHashScanPage> ScanFieldsPageAsync(
        RespireKey key, ulong cursor = 0, string? match = null, int? countHint = null, CancellationToken cancellationToken = default);
}

internal sealed partial class HashCommands
{
    public IAsyncEnumerable<string> ScanFieldsAsync(
        RespireKey key, string? match = null, int countHint = 250, CancellationToken cancellationToken = default)
        => CollectionScan.EnumerateAsync(client, "HSCAN", RespireCommands.Hash.HSCAN.Verb, key, match, countHint,
            static (in RespValue page) => ResponseReader.StringArray(in page), cancellationToken, noValues: true);

    public ValueTask<RespireHashScanPage> ScanFieldsPageAsync(
        RespireKey key, ulong cursor = 0, string? match = null, int? countHint = null, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("HSCAN", ScanFieldsCommand(client, key, cursor, match, countHint), cancellationToken,
            client, static (RespireClient _, in RespValue reply) => ParseFieldsPage(in reply));

    internal static CmdN ScanFieldsCommand(RespireClient client, RespireKey key, ulong cursor, string? match, int? countHint)
        => new(RespireCommands.Hash.HSCAN.Verb,
            CollectionScan.Arguments(client.Key(in key), cursor, match, countHint, noValues: true));

    internal static RespireHashScanPage ParseFieldsPage(in RespValue reply)
    {
        var parts = reply.AsArray();
        if (parts.Length != 2 || !ulong.TryParse(parts[0].AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
            throw new RespireProtocolException("HSCAN must return a cursor and field array.");
        return new(cursor, ResponseReader.StringArray(in parts[1]));
    }
}
