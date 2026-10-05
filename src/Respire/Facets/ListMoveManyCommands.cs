using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>How a multi-element list move handles a source shorter than the requested count.</summary>
public enum ListMoveCountMode
{
    /// <summary>Move up to the requested count. Redis: COUNT.</summary>
    UpTo,
    /// <summary>Move only when the entire requested count is available. Redis: EXACTLY.</summary>
    Exactly,
}

/// <summary>How a multi-element list move arranges the selected elements.</summary>
public enum ListMoveOrder
{
    /// <summary>Insert in pop order, reversing that order when pushing at the head. Redis: OBO.</summary>
    OneByOne,
    /// <summary>Preserve the selected elements' left-to-right source order. Redis: BULK.</summary>
    Bulk,
}

public partial interface IListCommands
{
    /// <summary>
    /// Moves up to or exactly count elements, returning an owned array in destination order.
    /// Returns null when the move cannot be satisfied. Redis: LMOVEM / BLMOVEM (8.10+).
    /// </summary>
    /// <remarks>
    /// Count must be positive and both keys must share a Cluster slot. Omit waitFor for an
    /// immediate move. A supplied wait rents a dedicated connection; Exactly waits for the
    /// entire count, while UpTo waits for at least one element. Timeout.InfiniteTimeSpan waits
    /// indefinitely. TimeSpan.Zero uses a one-millisecond wait. Cancellation discards the lease.
    /// Invalid options throw synchronously before I/O.
    /// </remarks>
    ValueTask<string[]?> MoveManyAsync(
        RespireKey source, RespireKey destination, long count = 1,
        ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        ListMoveCountMode countMode = ListMoveCountMode.UpTo, ListMoveOrder order = ListMoveOrder.OneByOne,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default);
}

internal sealed partial class ListCommands
{
    public ValueTask<string[]?> MoveManyAsync(
        RespireKey source, RespireKey destination, long count = 1,
        ListSide from = ListSide.Left, ListSide to = ListSide.Right,
        ListMoveCountMode countMode = ListMoveCountMode.UpTo, ListMoveOrder order = ListMoveOrder.OneByOne,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (operation, command) = MoveManyCommand(client, source, destination, count, from, to, countMode, order, waitFor);
        return waitFor.HasValue
            ? MoveManyBlockingAsync(operation, command, cancellationToken)
            : client.ConvertResponseAsync(operation, command, cancellationToken, this,
                static (ListCommands _, in RespValue reply) => ParseMovedValues(in reply));
    }

    private async ValueTask<string[]?> MoveManyBlockingAsync(string operation, CmdN command, CancellationToken cancellationToken)
    {
        using var reply = await client.SendBlockingAsync(operation, command, cancellationToken).ConfigureAwait(false);
        return ParseMovedValues(in reply);
    }

    internal static string[]? ParseMovedValues(in RespValue reply)
        => reply.IsNull ? null : ResponseReader.StringArray(in reply);

    internal static (string Operation, CmdN Command) MoveManyCommand(
        RespireClient client, RespireKey source, RespireKey destination, long count,
        ListSide from, ListSide to, ListMoveCountMode countMode, ListMoveOrder order, TimeSpan? waitFor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var fromToken = SideToken(from);
        var toToken = SideToken(to);
        var selector = countMode switch
        {
            ListMoveCountMode.UpTo => "COUNT",
            ListMoveCountMode.Exactly => "EXACTLY",
            _ => throw new ArgumentOutOfRangeException(nameof(countMode), countMode, null),
        };
        var ordering = order switch
        {
            ListMoveOrder.OneByOne => "OBO",
            ListMoveOrder.Bulk => "BULK",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, null),
        };
        if (waitFor is { } wait) MultiKeyPop.ValidateWait(wait);
        var operation = waitFor.HasValue ? "BLMOVEM" : "LMOVEM";
        var arguments = new RespireValue[waitFor.HasValue ? 8 : 7];
        MultiKeyPop.CopyPopKeys(client, [source, destination], arguments.AsSpan(0, 2), operation);
        arguments[2] = fromToken;
        arguments[3] = toToken;
        var index = 4;
        // TimeSpan.Zero maps to 1 ms: Redis interprets a wire timeout of zero as infinite.
        if (waitFor is { } timeout) arguments[index++] = MultiKeyPop.ToSeconds(timeout);
        arguments[index++] = selector;
        arguments[index++] = count;
        arguments[index] = ordering;
        return (operation, new CmdN(waitFor.HasValue ? RespireCommands.List.BLMOVEM.Verb : RespireCommands.List.LMOVEM.Verb, arguments));
    }
}
