using Respire.Commands;

namespace Respire;

public partial interface IBatchListCommands
{
    /// <summary>Pops up to count values from the first nonempty list in input order; null when all are empty. Redis: LMPOP (7.0+).</summary>
    /// <remarks>Keys must share a Cluster slot. Count must be positive. Blocking pops have no deferred form.</remarks>
    RespirePending<RespireListPopManyResult?> PopMany(
        ReadOnlySpan<RespireKey> keys, long count = 1, ListSide side = ListSide.Left);
}

internal sealed partial class BatchListCommands
{
    public RespirePending<RespireListPopManyResult?> PopMany(
        ReadOnlySpan<RespireKey> keys, long count = 1, ListSide side = ListSide.Left)
    {
        var (operation, command) = ListCommands.PopManyCommand(sink.Client, keys, count, side, waitFor: null);
        return sink.Add<CmdN, RespireListPopManyResult?>(operation, command, keys,
            static (c, reply) => ListCommands.ParsePopMany(in reply, c.KeyPrefixBytes));
    }
}
