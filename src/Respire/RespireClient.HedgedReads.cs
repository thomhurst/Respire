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
            var snapshot = SnapshotCommand.Create(in command, captureCacheMetadata: _core.ClientCache is not null);
            var originalRoute = cluster is null ? null : new HedgeOriginalRoute(connection);
            var pending = SendHedgedReadLegAsync(operation, snapshot, connection, flags, cancellationToken, originalRoute);
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
#pragma warning disable CA1849 // This private token only cancels Task.Delay; keep read completion free of a cancellation worker hop.
                finally { timer.Cancel(); }
#pragma warning restore CA1849

                if (!original.IsCompleted && !cancellationToken.IsCancellationRequested && budget.HasCredit
                    && (originalRoute is null ? connection : originalRoute.Connection) is { } currentOriginal)
                {
                    using var selectionStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var selection = cluster is null
                        ? _core.ReadRouter.GetHedgeConnectionAsync(_readFrom, connection, selectionStop.Token).AsTask()
                        : cluster.GetHedgeConnectionAsync(slot!.Value, _readFrom, currentOriginal, selectionStop.Token).AsTask();
                    if (await Task.WhenAny(original, selection).ConfigureAwait(false) == selection)
                    {
                        try { race.Alternative = await selection.ConfigureAwait(false); }
                        catch (Exception error) when (!IsFatalHedgeFailure(error))
                        { /* An optional candidate failure must not replace the original outcome. */ }
                    }
                    else
                    {
                        await selectionStop.CancelAsync().ConfigureAwait(false);
                        _ = ObserveHedgeSelectionAsync(selection);
                    }
                    if (race.Alternative is { } alternative && !original.IsCompleted
                        && (originalRoute is null || originalRoute.CanHedge(alternative))
                        && !cancellationToken.IsCancellationRequested && budget.TrySpend())
                    {
                        sent = true;
                        RespireTelemetry.RecordHedgeSent(alternative);
                        try
                        {
                            race.Hedge = SendHedgedReadLegAsync(operation, snapshot, alternative, flags, cancellationToken,
                                originalRoute, isHedge: true).AsTask();
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
        RespireConnection connection, RespireCommandFlags flags, CancellationToken cancellationToken,
        HedgeOriginalRoute? originalRoute = null, bool isHedge = false)
        where TCommand : struct, IRespCommand
        => _core.Cluster is { } cluster
            ? SendClusterAsync(operation, cluster, command, cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect), initialConnection: connection, allowReadFrom: true,
                hedgeOriginalRoute: originalRoute, isHedge: isHedge)
            : SendOnConnectionAsync(operation, connection, command, cancellationToken);

    private sealed class HedgeOriginalRoute(RespireConnection connection)
    {
        private RespireConnection? _connection = connection;
        private int _completed;

        // Null means the original is discovering its next route. Optional
        // work must not guess which peer that attempt will use while discovery is pending.
        internal RespireConnection? Connection
        {
            get => Volatile.Read(ref _connection);
            set => Volatile.Write(ref _connection, value);
        }

        internal bool CanHedge(RespireConnection candidate)
            => Volatile.Read(ref _completed) != 0
                || Connection is { } current && HedgedReadPolicy.IsDifferentPeer(current, candidate);

        // An already-issued hedge may still recover after the original fails. There is no
        // competing attempt to exclude once that original has finished.
        internal void Complete() => Volatile.Write(ref _completed, 1);
    }

    private static bool IsFatalHedgeFailure(Exception error)
        // This classifies exceptions delivered to a catch filter; it does not make
        // runtime-fatal failures catchable or promise recovery from them.
        => error is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static async Task ObserveHedgeSelectionAsync(Task<RespireConnection?> selection)
    {
        try { await selection.ConfigureAwait(false); }
        catch (Exception) { /* The original response won before optional selection finished. */ }
    }
}
