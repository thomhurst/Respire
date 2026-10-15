using System.Collections.Concurrent;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    // Slot identity survives owner replacement. Unkeyed work has its own queue.
    private readonly ConcurrentDictionary<int, RespireConnectionMultiplexer.ReconnectBacklog> _backlogs = new();
    private int _hasBacklogConnection;

    internal bool IsBacklogDraining(int? slot)
        => _backlogs.TryGetValue(slot ?? -1, out var queue) && queue.IsRunning;

    internal Action<bool>? BacklogAdmissionCheckedForTests { get; set; }

    internal bool ShouldBacklog(int? slot, RespireReadFrom readFrom = RespireReadFrom.Primary)
    {
        var shouldBacklog = ShouldBacklogCore(slot, readFrom);
        BacklogAdmissionCheckedForTests?.Invoke(shouldBacklog);
        return shouldBacklog;
    }

    private bool ShouldBacklogCore(int? slot, RespireReadFrom readFrom)
    {
        if (_options.BacklogPolicy != RespireBacklogPolicy.Queue
            || Volatile.Read(ref _hasBacklogConnection) == 0 && !_primary.IsInitialized) return false;
        if (IsBacklogDraining(slot)) return true;
        if (slot is { } readSlot && readFrom != RespireReadFrom.Primary)
        {
            var recoveringReplica = false;
            if (GetKnownReplicas(readSlot) is { } replicas)
                foreach (var replica in replicas.Nodes)
                {
                    if (replica is { IsConnected: true, IsRetired: false }) return false;
                    recoveringReplica |= replica.IsInitialized;
                }
            if (readFrom == RespireReadFrom.Replica) return recoveringReplica;
        }
        var owner = slot is { } value ? RoutingSnapshot[value].Primary : TryGetConnectedNode();
        owner ??= Volatile.Read(ref _seed) ?? _primary;
        return !owner.IsConnected || owner.IsRetired;
    }

    internal void ValidateBacklogAdmission(int? slot, RespireReadFrom readFrom, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfBacklogUnavailable();
        if (_circuits is null || readFrom != RespireReadFrom.Primary) return;
        var owner = (slot is { } value ? RoutingSnapshot[value].Primary : null) ?? Volatile.Read(ref _seed);
        if (owner is not null && !HasAlternateMaster(owner))
            _circuits.Acquire(owner.ActiveConnectionEndpoint, token).Dispose();
    }

    internal RespireConnectionMultiplexer.ReconnectBacklog GetReconnectBacklog(int? slot)
    {
        // Avoid registering losing factory instances against the router lifetime.
        lock (_nodesGate)
        {
            ThrowIfBacklogUnavailable();
            if (_backlogs.TryGetValue(slot ?? -1, out var queue)) return queue;
            queue = new(_options.BacklogCapacity, _stopDiscovery.Token, ThrowIfBacklogUnavailable,
                () => slot is { } value ? RoutingSnapshot[value].Primary?.ActiveConnectionEndpoint : null,
                token => WaitForBacklogConnectionAsync(slot, RespireReadFrom.Primary, token, default));
            _backlogs[slot ?? -1] = queue;
            return queue;
        }
    }

    private void ThrowIfBacklogUnavailable()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    internal async ValueTask<RespireConnection> WaitForBacklogConnectionAsync(int? slot,
        RespireReadFrom readFrom, CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        var attempts = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfBacklogUnavailable();
            try
            {
                // A lone route waits for its replacement socket. A connected alternative
                // keeps normal discovery/fallback available when the cached owner is lost.
                if (readFrom == RespireReadFrom.Primary && TryGetConnectedNode() is null)
                    await WaitForRouteReplacementAsync(slot, token).ConfigureAwait(false);
                var selectedOwner = slot is { } selectedSlot ? RoutingSnapshot[selectedSlot].Primary : null;
                var connection = await GetReadConnectionAsync(slot, readFrom, token, observation).ConfigureAwait(false);
                connection.ThrowIfRetired();
                // Reselect a concurrently replaced owner, but retain a legitimate seed
                // fallback returned by discovery for the same unavailable cached owner.
                if (readFrom == RespireReadFrom.Primary && slot is { } value
                    && RoutingSnapshot[value].Primary is { } owner
                    && !ReferenceEquals(owner, selectedOwner)
                    && !ReferenceEquals(owner, connection.Multiplexer)) continue;
                return connection;
            }
            catch (ObjectDisposedException error) when (Volatile.Read(ref _disposed) == 0)
            {
                // A detached generation can finish disposal during selection. The router
                // still owns this unsent entry, so select its replacement on the next pass.
                token.ThrowIfCancellationRequested();
                ReadEndpointRouter.RecordCandidateFailure(observation, error, observation.Endpoint);
                await Task.Delay(RouteReplacementPollInterval, token).ConfigureAwait(false);
            }
            catch (RespireException error) when (error is RespireConnectionRetiredException
                || error is RespireConnectionException and not RespireReconnectLimitException)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfBacklogUnavailable();
                if (_options.ReconnectPolicy?.IsExhausted(attempts) == true)
                    throw new RespireReconnectLimitException("Cluster backlog recovery exhausted its configured attempts.", error)
                    { Endpoint = error.Endpoint };
                if (attempts < int.MaxValue) attempts++;
                ReadEndpointRouter.RecordCandidateFailure(observation, error, observation.Endpoint ?? error.Endpoint);
                var minimumDelay = TimeSpan.FromMilliseconds(RouteReplacementPollInterval);
                var delay = _options.ReconnectPolicy?.GetDelay(attempts) ?? minimumDelay;
                if (delay < minimumDelay) delay = minimumDelay;
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }
}
