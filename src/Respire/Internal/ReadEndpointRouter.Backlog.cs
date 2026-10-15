using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    private const int BacklogRetryDelayMilliseconds = 25;
    private RespireConnectionMultiplexer.ReconnectBacklog? _backlog;
    // Eligibility survives membership replacement, but a primary fallback does not initialize the strict replica route.
    private const int SelectedConnection = 1;
    private const int SelectedReplica = 2;
    private int _selectionHistory;

    private void RecordSelection(bool replica)
    {
        var required = replica ? SelectedConnection | SelectedReplica : SelectedConnection;
        // A primary selection racing the first replica selection must never clear its bit.
        if ((Volatile.Read(ref _selectionHistory) & required) != required)
            Interlocked.Or(ref _selectionHistory, required);
    }

    internal sealed class BacklogSelectionRequiredException : Exception { }

    internal bool IsBacklogDraining => Volatile.Read(ref _backlog)?.IsRunning == true;
    private bool CanQueue(RespireReadFrom readFrom) => core.Options.BacklogPolicy == RespireBacklogPolicy.Queue
        && (Volatile.Read(ref _selectionHistory) & (readFrom == RespireReadFrom.Replica ? SelectedReplica : SelectedConnection)) != 0;

    internal async ValueTask<RespireConnection?> SelectForDispatchAsync(RespireReadFrom readFrom,
        CancellationToken token, RespireTelemetry.ErrorObservation observation,
        bool cursorRead = false, ReadAffinity? affinity = null)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        ThrowIfNoConfiguredReplica(readFrom);
        if (CanQueue(readFrom) && IsBacklogDraining) return null;
        try
        {
            var connection = await SelectBacklogHeadAsync(readFrom, token, observation, cursorRead, affinity,
                readyOnly: CanQueue(readFrom)).ConfigureAwait(false);
            return CanQueue(readFrom) && IsBacklogDraining ? null : connection;
        }
        catch (BacklogSelectionRequiredException) { return null; }
        catch (Exception error) when (CanQueue(readFrom) && IsUnavailable(error, token))
        {
            ThrowIfTerminal(readFrom, error);
            return null;
        }
    }

    internal ValueTask<T> EnqueueAsync<T>(RespireReadFrom readFrom,
        Func<RespireConnection, CancellationToken, ValueTask<T>> dispatch, CancellationToken token,
        RespireTelemetry.ErrorObservation observation, bool cursorRead = false, ReadAffinity? affinity = null)
    {
        ThrowIfNoConfiguredReplica(readFrom);
        // Dispatch only sends fresh cursors here. Selection may have pinned one before a
        // concurrent drain won admission; no server has issued that cursor yet.
        if (cursorRead && affinity is not null)
        {
            affinity.Replica = null;
            affinity.Primary = null;
        }
        RespireConnectionMultiplexer.ReconnectBacklog backlog;
        lock (_entriesGate)
        {
            ThrowIfDisposed();
            backlog = _backlog ??= new(core.Options.BacklogCapacity, _lifetime.Token,
                ThrowIfDisposed, static () => null,
                static _ => throw new InvalidOperationException("A read backlog head must supply its read policy."),
                failAllOnTerminal: false);
        }
        return backlog.EnqueueAsync(dispatch, token, async wait =>
        {
            while (true)
            {
                ThrowIfDisposed();
                wait.ThrowIfCancellationRequested();
                try { return await SelectBacklogHeadAsync(readFrom, wait, observation, cursorRead, affinity).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, wait))
                {
                    ThrowIfTerminal(readFrom, error);
                    // The selector owns discovery throttling, ROLE freshness, cooldown and
                    // fallback. Bound demand retries without pinning a retired generation.
                    await Task.Delay(BacklogRetryDelayMilliseconds, wait).ConfigureAwait(false);
                }
            }
        });
    }

    private async ValueTask<RespireConnection> SelectBacklogHeadAsync(RespireReadFrom readFrom,
        CancellationToken token, RespireTelemetry.ErrorObservation observation, bool cursorRead, ReadAffinity? affinity,
        bool readyOnly = false)
    {
        var connection = cursorRead
            ? await GetCursorConnectionAsync(readFrom, affinity, false, token, observation, readyOnly).ConfigureAwait(false)
            : await GetConnectionAsync(readFrom, token, observation, readyOnly).ConfigureAwait(false);
        if (cursorRead && (Volatile.Read(ref _selectionHistory) & SelectedReplica) == 0)
        {
            var replica = false;
            foreach (var entry in _entries.Values)
                if (ReferenceEquals(entry.RecoveryOwner, connection.Multiplexer))
                {
                    replica = true;
                    break;
                }
            RecordSelection(replica);
        }
        return connection;
    }

    private void ThrowIfTerminal(RespireReadFrom readFrom, Exception error)
    {
        ThrowIfNoConfiguredReplica(readFrom);
        // Cooldown and Nearest probe throttling may hide a terminal candidate error on
        // the next pass. Inspect current recovery owners without starting another attempt.
        // Sentinel can still replace a primary generation through discovery.
        if (readFrom != RespireReadFrom.Replica
            && (core.Sentinel is not null || !IsExhausted(core.Multiplexer))) return;
        var endpoints = Volatile.Read(ref _replicas);
        if (endpoints.Length == 0 && readFrom == RespireReadFrom.Replica) return;
        foreach (var endpoint in endpoints)
            if (!_entries.TryGetValue(endpoint, out var entry) || !IsExhausted(entry.RecoveryOwner)) return;
        // A concurrent topology publication makes this exhaustion snapshot inconclusive.
        // The head retries against that publication instead of failing on retired owners.
        if (!ReferenceEquals(endpoints, Volatile.Read(ref _replicas))) return;
        throw TerminalFailure(error);

        static bool IsExhausted(RespireConnectionMultiplexer? owner)
        {
            if (owner is null) return false;
            try { owner.ThrowIfRecoveryExhausted(); return false; }
            catch (RespireReconnectLimitException) { return true; }
        }
    }

    private void ThrowIfNoConfiguredReplica(RespireReadFrom readFrom)
    {
        if (readFrom == RespireReadFrom.Replica && core.Sentinel is null && Volatile.Read(ref _replicas).Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");
    }

    private static RespireReconnectLimitException TerminalFailure(Exception error)
        => error as RespireReconnectLimitException
            ?? new RespireReconnectLimitException("All eligible read connections exhausted recovery.", error)
            { HasAmbiguousEndpoint = true };
}
