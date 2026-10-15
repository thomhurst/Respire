using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    private RespireConnectionMultiplexer.ReconnectBacklog? _backlog;
    // Eligibility survives membership replacement, but a primary fallback does not initialize the strict replica route.
    private int _hasSelectedConnection;
    private int _hasSelectedReplica;

    internal sealed class BacklogSelectionRequiredException : Exception { }

    internal bool IsBacklogDraining => Volatile.Read(ref _backlog)?.IsRunning == true;
    private bool CanQueue(RespireReadFrom readFrom) => core.Options.BacklogPolicy == RespireBacklogPolicy.Queue
        && (readFrom == RespireReadFrom.Replica
            ? Volatile.Read(ref _hasSelectedReplica) != 0 : Volatile.Read(ref _hasSelectedConnection) != 0);

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
                    await Task.Delay(25, wait).ConfigureAwait(false);
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
        if (Volatile.Read(ref _hasSelectedConnection) == 0) Volatile.Write(ref _hasSelectedConnection, 1);
        if (cursorRead && Volatile.Read(ref _hasSelectedReplica) == 0
            && _entries.Values.Any(entry => ReferenceEquals(entry.RecoveryOwner, connection.Multiplexer)))
            Volatile.Write(ref _hasSelectedReplica, 1);
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
