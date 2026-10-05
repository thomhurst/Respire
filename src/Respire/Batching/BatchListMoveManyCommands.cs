using Respire.Commands;

namespace Respire;

public partial interface IBatchListCommands
{
    /// <summary>Queues a multi-element list move and returns an owned array in destination order; null if unsatisfied. Redis: LMOVEM (8.10+).</summary>
    /// <remarks>Both keys must share a Cluster slot. Queued moves never wait; BLMOVEM has no deferred form.</remarks>
    RespirePending<string[]?> MoveMany(
        RespireKey source, RespireKey destination, long count = 1,
        ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        ListMoveCountMode countMode = ListMoveCountMode.UpTo, ListMoveOrder order = ListMoveOrder.OneByOne);
}

internal sealed partial class BatchListCommands
{
    public RespirePending<string[]?> MoveMany(
        RespireKey source, RespireKey destination, long count = 1,
        ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        ListMoveCountMode countMode = ListMoveCountMode.UpTo, ListMoveOrder order = ListMoveOrder.OneByOne)
    {
        var (operation, command) = ListCommands.MoveManyCommand(sink.Client, source, destination, count, from, to, countMode, order, null);
        return sink.Add<CmdN, string[]?>(operation, command, source, destination,
            static (_, reply) => ListCommands.ParseMovedValues(in reply));
    }
}
