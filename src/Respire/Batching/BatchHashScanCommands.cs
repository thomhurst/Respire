using Respire.Commands;

namespace Respire;

public partial interface IBatchHashCommands
{
    /// <summary>Queues one HSCAN NOVALUES page. Requires Redis 7.4; results own their field names.</summary>
    /// <remarks>Continue only after reading the previous cursor. Use a stable server and read policy across pages.
    /// A null countHint omits COUNT and uses the server's default.</remarks>
    RespirePending<RespireHashScanPage> ScanFieldsPage(
        RespireKey key, ulong cursor = 0, string? match = null, int? countHint = null);
}

internal sealed partial class BatchHashCommands
{
    public RespirePending<RespireHashScanPage> ScanFieldsPage(
        RespireKey key, ulong cursor = 0, string? match = null, int? countHint = null)
        => sink.Add<CmdN, RespireHashScanPage>("HSCAN", HashCommands.ScanFieldsCommand(sink.Client, key, cursor, match, countHint),
            static (_, reply) => HashCommands.ParseFieldsPage(in reply));
}
