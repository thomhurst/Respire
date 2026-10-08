using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private async ValueTask<RespValue> SendHedgedReadAsync<TCommand>(string operation, TCommand command,
        HedgedReadBudget budget, RespireCommandFlags flags, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default, bool observeErrors = true)
        where TCommand : struct, IRespCommand
    {
        var ownsObservation = observation.IsEmpty;
        if (observation.IsEmpty) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var callerAttempts = observation.Attempts;
        RespireConnection? connection = null;
        Exception? failure = null;
        var sent = false;
        var race = new HedgeRace();
        try
        {
            var cluster = _core.Cluster;
            var slot = command.TryGetClusterSlot(out var value) ? value : (int?)null;
            connection = cluster is null
                ? await _core.ReadRouter.GetConnectionAsync(_readFrom, cancellationToken).ConfigureAwait(false)
                : await cluster.GetReadConnectionAsync(slot, _readFrom, cancellationToken, observation: observation).ConfigureAwait(false);
            // Selection belongs to the caller, before either independently owned hedge leg starts.
            callerAttempts = observation.Attempts;
            budget.RecordRead();
            // Either leg can outlive the caller. Its lease stays with that leg's FIFO reply,
            // while the caller copies the completed result leg's attempts into its own lease.
            race.OriginalObservation = RespireTelemetry.ErrorObservation.Rent(force: true);
            // Advisory cached-topology check only: do not establish optional connections before
            // starting the original request. A newly discovered peer can serve a later read.
            if (!budget.HasCredit || !(cluster is null
                ? _core.ReadRouter.HasPotentialHedgePeer(_readFrom, connection)
                : cluster.HasPotentialHedgePeer(slot!.Value, _readFrom, connection)))
            {
                try
                {
                    return await SendHedgedReadLegAsync(operation, command, connection, flags, cancellationToken,
                        observation: race.OriginalObservation).ConfigureAwait(false);
                }
                finally { observation.SetAttempts(callerAttempts + race.OriginalObservation.Attempts); }
            }

            // Either leg may outlive its caller. Own the argument bytes before dispatching either
            // request, including when admission/backpressure delays serialization of the loser.
            var snapshot = SnapshotCommand.Create(in command);
            var originalRoute = cluster is null ? null : new HedgeOriginalRoute(connection);
            var pending = SendHedgedReadLegAsync(operation, snapshot, connection, flags, cancellationToken, originalRoute,
                observation: race.OriginalObservation);
            if (pending.IsCompletedSuccessfully)
            {
                observation.SetAttempts(callerAttempts + race.OriginalObservation.Attempts);
                return pending.Result;
            }
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
                        race.HedgeObservation = RespireTelemetry.ErrorObservation.Rent(force: true);
                        try
                        {
                            race.Hedge = SendHedgedReadLegAsync(operation, snapshot, alternative, flags, cancellationToken,
                                originalRoute, isHedge: true, observation: race.HedgeObservation).AsTask();
                        }
                        catch (Exception error) when (!IsFatalHedgeFailure(error))
                        {
                            // Retirement can reject admission synchronously after selection.
                            race.Hedge = Task.FromException<RespValue>(error);
                        }
                    }
                }
            }

            var result = await race.ResolveAsync(observation, callerAttempts).ConfigureAwait(false);
            race.Returned = result.Winner;
            return result.Response;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            race.DisposeLosers();
            if (connection is not null) RespireTelemetry.RecordHedgeExtraLoad(connection, sent);
            if (ownsObservation)
            {
                if (observeErrors && failure is not null) observation.Final(failure);
                observation.Dispose();
            }
        }
    }

    private ValueTask<RespValue> SendHedgedReadLegAsync<TCommand>(string operation, TCommand command,
        RespireConnection connection, RespireCommandFlags flags, CancellationToken cancellationToken,
        HedgeOriginalRoute? originalRoute = null, bool isHedge = false,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Cluster is { } cluster
            ? SendClusterAsync(operation, cluster, command, cancellationToken,
                noRedirect: HasFlag(flags, RespireCommandFlags.NoRedirect), initialConnection: connection, allowReadFrom: true,
                hedgeOriginalRoute: originalRoute, isHedge: isHedge, observation: observation)
            : SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation);

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
