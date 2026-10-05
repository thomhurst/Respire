using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An owned HSCAN NOVALUES page. Cursor zero marks completion; an empty page need not be complete.</summary>
/// <remarks>Record equality compares the Fields array by reference, not by its contents.
/// Page APIs omit COUNT by default and use the server's default; ScanFieldsAsync uses a count hint of 250.
/// COUNT is a hint, not a guaranteed page size.</remarks>
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
    /// <remarks>Requires Redis 7.4. Keep the same key, server, and read policy across pages; restart after topology changes.
    /// A null countHint omits COUNT and uses the server's default; ScanFieldsAsync defaults to a hint of 250.
    /// An already-canceled token returns a canceled ValueTask before validating arguments or sending a command.</remarks>
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
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<RespireHashScanPage>(cancellationToken);
        return client.ConvertResponseAsync("HSCAN", ScanFieldsCommand(client, key, cursor, match, countHint), cancellationToken,
            client, static (RespireClient _, in RespValue reply) => ParseFieldsPage(in reply));
    }

    internal static CmdN ScanFieldsCommand(RespireClient client, RespireKey key, ulong cursor, string? match, int? countHint)
        => new(RespireCommands.Hash.HSCAN.Verb,
            CollectionScan.Arguments(client.Key(in key), cursor, match, countHint, noValues: true));

    internal static RespireHashScanPage ParseFieldsPage(in RespValue reply)
    {
        var parts = CollectionScan.ParsePage(in reply, "HSCAN", out var cursor);
        return new(cursor, ResponseReader.StringArray(in parts[1]));
    }
}
