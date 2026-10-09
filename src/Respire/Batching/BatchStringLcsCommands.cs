using Respire.Commands;

namespace Respire;

public partial interface IBatchStringCommands
{
    /// <summary>Queues inclusive byte ranges and total subsequence length. Redis: LCS IDX (7.0+).</summary>
    /// <remarks>Both keys must share a Cluster slot after prefixing. Filtering does not change the total length.</remarks>
    RespirePending<RespireLcsIndexResult> LcsIndex(RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options = null);
}

internal sealed partial class BatchStringCommands
{
    public RespirePending<RespireLcsIndexResult> LcsIndex(RespireKey firstKey, RespireKey secondKey,
        RespireLcsOptions? options = null)
        => sink.Add<CmdN, RespireLcsIndexResult>("LCS",
            StringCommands.LcsIndexCommand(sink.Client, firstKey, secondKey, options), firstKey, secondKey,
            static (c, v) => StringCommands.ParseLcsIndex(in v));
}
