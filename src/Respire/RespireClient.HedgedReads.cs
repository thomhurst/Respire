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
        Task<RespValue>? original = null;
        Task<RespValue>? hedge = null;
        Task<RespValue>? returned = null;
        RespireConnection? alternative = null;
        try
        {
            if (!budget.HasCredit)
                return await SendHedgedReadLegAsync(operation, command, connection, flags, cancellationToken).ConfigureAwait(false);

            // Either leg may outlive its caller. Own the argument bytes before dispatching either
            // request, including when admission/backpressure delays serialization of the loser.
            var snapshot = SnapshotCommand.Create(in command);
            original = SendHedgedReadLegAsync(operation, snapshot, connection, flags, cancellationToken).AsTask();
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
                        try { alternative = await selection.ConfigureAwait(false); }
                        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                        { /* An optional candidate failure must not replace the original outcome. */ }
                    }
                    else
                    {
                        selectionStop.Cancel();
                        _ = ObserveHedgeSelectionAsync(selection);
                    }
                    if (alternative is not null && !original.IsCompleted
                        && !cancellationToken.IsCancellationRequested && budget.TrySpend())
                    {
                        sent = true;
                        RespireTelemetry.RecordHedgeSent(alternative);
                        hedge = SendHedgedReadLegAsync(operation, snapshot, alternative, flags, cancellationToken).AsTask();
                    }
                }
            }

            var winner = hedge is null ? original : await Task.WhenAny(original, hedge).ConfigureAwait(false);
            RespValue response;
            try { response = await winner.ConfigureAwait(false); }
            catch (Exception error) when (hedge is not null
                && error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
            {
                // Prefer a successful response over a failed optional leg. If both fail, preserve
                // the original request's exception type and identity instead of aggregating it.
                winner = ReferenceEquals(winner, original) ? hedge : original;
                try { response = await winner.ConfigureAwait(false); }
                catch (Exception otherError) when (otherError is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { return await original.ConfigureAwait(false); }
            }
            returned = winner;
            if (ReferenceEquals(winner, hedge)) RespireTelemetry.RecordHedgeWon(alternative!);
            return response;
        }
        finally
        {
            // Do not cancel a losing accepted read: it still owns a FIFO response slot. Observe
            // its completion and dispose its buffer without delaying the winning caller.
            if (original is not null && !ReferenceEquals(original, returned)) _ = DisposeLosingReadAsync(original);
            if (hedge is not null && !ReferenceEquals(hedge, returned)) _ = DisposeLosingReadAsync(hedge);
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

    private static async Task DisposeLosingReadAsync(Task<RespValue> response)
    {
        try { using var discarded = await response.ConfigureAwait(false); }
        catch (Exception) { /* The winning caller cannot observe the losing request's failure. */ }
    }

    private static async Task ObserveHedgeSelectionAsync(Task<RespireConnection?> selection)
    {
        try { await selection.ConfigureAwait(false); }
        catch (Exception) { /* The original response won before optional selection finished. */ }
    }
}
