using Respire.Commands;

namespace Respire;

public partial interface IRespireBatchListCommands
{
    /// <summary>Queues a multi-element list move and returns an owned array in destination order; null if unsatisfied. Redis: LMOVEM (8.10+).</summary>
    /// <remarks>Both keys must share a Cluster slot. Queued moves never wait; BLMOVEM has no deferred form.</remarks>
    RespirePending<string[]?> MoveMany(
        RespireKey source, RespireKey destination, long count = 1,
        RespireListSide from = RespireListSide.Left, RespireListSide to = RespireListSide.Right,
        RespireListMoveCountMode countMode = RespireListMoveCountMode.UpTo, RespireListMoveOrder order = RespireListMoveOrder.OneByOne);
}

internal sealed partial class BatchListCommands
{
    public RespirePending<string[]?> MoveMany(
        RespireKey source, RespireKey destination, long count = 1,
        RespireListSide from = RespireListSide.Left, RespireListSide to = RespireListSide.Right,
        RespireListMoveCountMode countMode = RespireListMoveCountMode.UpTo, RespireListMoveOrder order = RespireListMoveOrder.OneByOne)
    {
        var (operation, command) = ListCommands.MoveManyCommand(sink.Client, source, destination, count, from, to, countMode, order, null);
        return sink.Add<CmdN, string[]?>(operation, command, source, destination,
            static (_, reply) => ListCommands.ParseMovedValues(in reply));
    }
}
