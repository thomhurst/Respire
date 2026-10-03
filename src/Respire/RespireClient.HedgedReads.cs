using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private async ValueTask<RespValue> SendHedgedReadAsync<TCommand>(string operation, TCommand command,
        HedgedReadBudget budget, RespireCommandFlags flags, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var cluster = _core.Cluster;
        var slot = command.TryGetClusterSlot(out var value) ? value : (int?)null;
        var connection = cluster is null
            ? await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false)
            : await cluster.GetReadConnectionAsync(slot, _readFrom, cancellationToken).ConfigureAwait(false);
        budget.RecordRead();
        var sent = false;
        var race = new HedgeRace();
        try
        {
            // Advisory cached-topology check only: do not establish optional connections before
            // starting the original request. A newly discovered peer can serve a later read.
            if (!budget.HasCredit || !(cluster is null
                ? _core.ReadRouter.HasPotentialHedgePeer(_readFrom, connection)
                : cluster.HasPotentialHedgePeer(slot!.Value, _readFrom, connection)))
                return await SendHedgedReadLegAsync(operation, command, connection, flags, cancellationToken).ConfigureAwait(false);

            // Either leg may outlive its caller. Own the argument bytes before dispatching either
            // request, including when admission/backpressure delays serialization of the loser.
            var snapshot = SnapshotCommand.Create(in command);
            var pending = SendHedgedReadLegAsync(operation, snapshot, connection, flags, cancellationToken);
            if (pending.IsCompletedSuccessfully) return pending.Result;
            var original = pending.AsTask();
            race.Original = original;
            if (!original.IsCompleted)
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    await Task.WhenAny(original, Task.Delay(_core.Options.HedgedReads!.Delay, timer.Token)).ConfigureAwait(false);
                }
                finally { timer.Cancel(); }

                if (!original.IsCompleted && !cancellationToken.IsCancellationRequested && budget.HasCredit)
                {
                    using var selectionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var selection = cluster is null
                        ? _core.ReadRouter.GetHedgeConnectionAsync(_readFrom, connection, selectionStop.Token).AsTask()
                        : cluster.GetHedgeConnectionAsync(slot!.Value, _readFrom, connection, selectionStop.Token).AsTask();
                    if (await Task.WhenAny(original, selection).ConfigureAwait(false) == selection)
                    {
                        try { race.Alternative = await selection.ConfigureAwait(false); }
                        catch (Exception error) when (!IsFatalHedgeFailure(error))
                        { /* An optional candidate failure must not replace the original outcome. */ }
                    }
                    else
                    {
                        selectionStop.Cancel();
                        _ = ObserveHedgeSelectionAsync(selection);
                    }
                    if (race.Alternative is { } alternative && !original.IsCompleted
                        && !cancellationToken.IsCancellationRequested && budget.TrySpend())
                    {
                        sent = true;
                        RespireTelemetry.RecordHedgeSent(alternative);
                        try
                        {
                            race.Hedge = SendHedgedReadLegAsync(operation, snapshot, alternative, flags, cancellationToken).AsTask();
                        }
                        catch (Exception error) when (!IsFatalHedgeFailure(error))
                        {
                            // Retirement can reject admission synchronously after selection.
                            race.Hedge = Task.FromException<RespValue>(error);
                        }
                    }
                }
            }

            var result = await race.ResolveAsync().ConfigureAwait(false);
            race.Returned = result.Winner;
            return result.Response;
        }
        finally
        {
            race.DisposeLosers();
            RespireTelemetry.RecordHedgeExtraLoad(connection, sent);
        }
    }

    private ValueTask<RespValue> SendHedgedReadLegAsync<TCommand>(string operation, TCommand command,
        RespireConnection connection, RespireCommandFlags flags, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => _core.Cluster is { } cluster
            ? SendClusterAsync(operation, cluster, command, cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect), initialConnection: connection, allowReadFrom: true)
            : SendOnConnectionAsync(operation, connection, command, cancellationToken);

    private static bool IsFatalHedgeFailure(Exception error)
        => error is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static async Task ObserveHedgeSelectionAsync(Task<RespireConnection?> selection)
    {
        try { await selection.ConfigureAwait(false); }
        catch (Exception) { /* The original response won before optional selection finished. */ }
    }
}
