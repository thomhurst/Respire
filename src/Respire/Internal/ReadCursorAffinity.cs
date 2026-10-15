using System.Collections.Concurrent;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Remembers which server issued a cursor, so later pages of the same enumeration reach it.
/// Each typed scan enumeration owns one; raw cursor commands share one per read policy.
/// </summary>
internal sealed class ReadAffinity
{
    internal ReadEndpointRouter.Entry? Replica;
    internal RespireConnectionMultiplexer? Primary;
    internal RespireConnectionMultiplexer? ClusterNode;
    internal int? ClusterSlot;
    internal RespireReadFrom ReadFrom;

    internal bool IsPinned => Replica is not null || Primary is not null || ClusterNode is not null;
}

/// <summary>
/// Keeps cursor reads on the server that issued their cursor. Typed scans own a
/// <see cref="ReadAffinity"/> per enumeration. Raw standalone cursor commands share one pin per
/// read policy; Cluster cursor commands share one per policy and hash slot. A fresh cursor
/// (<c>0</c>) may reselect when its pin is gone, but a continuation cursor fails instead of
/// reaching a server that never issued it.
/// </summary>
internal sealed class ReadCursorAffinity
{
    // Taken only to publish a new shared pin; reads through an existing pin never wait on it.
    private readonly SemaphoreSlim _sharedGate = new(1, 1);
    private readonly ConcurrentDictionary<RespireReadFrom, ReadAffinity> _shared = new();
    private readonly ConcurrentDictionary<(RespireReadFrom ReadFrom, int Slot), ReadAffinity> _clusterShared = new();

    internal async ValueTask<RespireConnection> GetConnectionAsync(
        ReadEndpointRouter router, RespireReadFrom readFrom, ReadAffinity? affinity, bool isContinuation,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool readyOnly = false,
        bool reselectSharedCursor = false)
    {
        if (affinity is not null)
        {
            if (affinity.IsPinned) return await GetPinnedConnectionAsync(router, affinity, cancellationToken, observation, readyOnly).ConfigureAwait(false);
            var first = await router.SelectAsync(readFrom, cancellationToken, observation, readyOnly: readyOnly).ConfigureAwait(false);
            affinity.Replica = first.Replica;
            affinity.Primary = first.Primary;
            affinity.ReadFrom = readFrom;
            return first.Connection;
        }

        // Queue admission alone does not invalidate a raw scan's issuing server.
        // Only a fresh queued cursor may bypass a cooling pin to recover elsewhere.
        var bypassCoolingPin = reselectSharedCursor && !isContinuation;
        if (_shared.TryGetValue(readFrom, out var shared)
            && (!bypassCoolingPin || shared.Replica is not { IsCoolingDown: true }))
        {
            if (await IsPinCurrentAsync(router, shared, cancellationToken, observation, readyOnly).ConfigureAwait(false))
                return await GetSharedPinnedConnectionAsync(router, readFrom, shared, cancellationToken, observation, readyOnly).ConfigureAwait(false);
            _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
        }

        // The server that issued this cursor is gone, so no other server can continue it.
        if (isContinuation) throw CursorLost();
        // A fresh cursor reselects; the stale pin's node must not name a later selection failure.
        observation.UseEndpoint(null);

        if (readyOnly)
        {
            if (!_sharedGate.Wait(0)) throw new ReadEndpointRouter.BacklogSelectionRequiredException();
        }
        else await _sharedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another publisher may have replaced the rejected pin while we waited.
            // Recheck the current pin under the gate before selecting and publishing.
            if (_shared.TryGetValue(readFrom, out shared)
                && (!bypassCoolingPin || shared.Replica is not { IsCoolingDown: true }))
            {
                if (await IsPinCurrentAsync(router, shared, cancellationToken, observation, readyOnly).ConfigureAwait(false))
                    return await GetSharedPinnedConnectionAsync(router, readFrom, shared, cancellationToken, observation, readyOnly).ConfigureAwait(false);
                _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
                observation.UseEndpoint(null);
            }

            var selection = await router.SelectAsync(readFrom, cancellationToken, observation, readyOnly: readyOnly).ConfigureAwait(false);
            _shared[readFrom] = new ReadAffinity { Replica = selection.Replica, Primary = selection.Primary, ReadFrom = readFrom };
            return selection.Connection;
        }
        finally { _sharedGate.Release(); }
    }

    internal async ValueTask<RespireConnection> GetClusterConnectionAsync(
        ClusterRouter cluster, int slot, RespireReadFrom readFrom, ReadAffinity? affinity,
        bool isContinuation, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        if (affinity is not null)
        {
            if (affinity.ClusterNode is { } pinned)
            {
                observation.UseEndpoint(pinned.ActiveConnectionEndpoint, cancellationToken);
                return await cluster.GetPinnedReadConnectionAsync(slot, pinned, cancellationToken, readFrom: affinity.ReadFrom).ConfigureAwait(false);
            }
            var first = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation).ConfigureAwait(false);
            affinity.ClusterSlot = slot;
            affinity.ClusterNode = first.Multiplexer;
            affinity.ReadFrom = readFrom;
            return first;
        }

        var key = (readFrom, slot);
        if (_clusterShared.TryGetValue(key, out var shared) && shared.ClusterNode is { } sharedNode)
        {
            observation.UseEndpoint(sharedNode.ActiveConnectionEndpoint, cancellationToken);
            try { return await cluster.GetPinnedReadConnectionAsync(slot, sharedNode, cancellationToken, revalidate: !isContinuation, readFrom: shared.ReadFrom).ConfigureAwait(false); }
            catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
            {
                if (!isContinuation) ReadEndpointRouter.RecordCandidateFailure(observation, error, sharedNode.ActiveConnectionEndpoint);
                _clusterShared.TryRemove(new KeyValuePair<(RespireReadFrom, int), ReadAffinity>(key, shared));
            }
        }
        else if (isContinuation)
        {
            throw CursorLost();
        }

        if (isContinuation) throw CursorLost();
        await _sharedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clusterShared.TryGetValue(key, out shared) && shared.ClusterNode is { } currentNode)
            {
                observation.UseEndpoint(currentNode.ActiveConnectionEndpoint, cancellationToken);
                try { return await cluster.GetPinnedReadConnectionAsync(slot, currentNode, cancellationToken, revalidate: true, readFrom: shared.ReadFrom).ConfigureAwait(false); }
                catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
                {
                    ReadEndpointRouter.RecordCandidateFailure(observation, error, currentNode.ActiveConnectionEndpoint);
                    _clusterShared.TryRemove(new KeyValuePair<(RespireReadFrom, int), ReadAffinity>(key, shared));
                }
            }
            var connection = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken, observation).ConfigureAwait(false);
            _clusterShared[key] = new ReadAffinity { ClusterNode = connection.Multiplexer, ClusterSlot = slot, ReadFrom = readFrom };
            return connection;
        }
        finally { _sharedGate.Release(); }
    }

    /// <summary>Publishes a shared pin directly. Tests use it to model a pin that went stale.</summary>
    internal void PinShared(RespireReadFrom readFrom, ReadAffinity affinity)
    {
        affinity.ReadFrom = readFrom;
        _shared[readFrom] = affinity;
    }

    internal void PinClusterShared(RespireReadFrom readFrom, int slot, RespireConnectionMultiplexer node)
        => _clusterShared[(readFrom, slot)] = new ReadAffinity { ClusterSlot = slot, ClusterNode = node, ReadFrom = readFrom };

    internal bool TryGetShared(RespireReadFrom readFrom, out ReadAffinity? affinity)
        => _shared.TryGetValue(readFrom, out affinity);

    internal void Clear() { _shared.Clear(); _clusterShared.Clear(); }

    private static RespireConnectionException CursorLost()
        => new("The server that issued this cursor left the read topology or failed. Restart the scan with cursor 0.");

    private async ValueTask<RespireConnection> GetSharedPinnedConnectionAsync(
        ReadEndpointRouter router, RespireReadFrom readFrom, ReadAffinity shared, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool readyOnly = false)
    {
        try { return await GetPinnedConnectionAsync(router, shared, cancellationToken, observation, readyOnly).ConfigureAwait(false); }
        catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
        {
            // The issuing server failed, so its cursors are lost. A fresh cursor reselects.
            _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
            throw;
        }
    }

    private static async ValueTask<bool> IsPinCurrentAsync(
        ReadEndpointRouter router, ReadAffinity affinity, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool readyOnly = false)
    {
        if (affinity.Replica is { } replica)
        {
            if (router.IsCurrent(replica)) return true;
            // A lost continuation belongs to the removed replica that issued the cursor.
            observation.UseEndpoint(replica.Endpoint, cancellationToken);
            return false;
        }
        // An unreachable or replaced primary invalidates the pin so the policy can reselect.
        var core = router.Core;
        if (readyOnly)
        {
            if (!core.TryGetReadyPrimaryMultiplexer(out var ready)) throw new ReadEndpointRouter.BacklogSelectionRequiredException();
            return ReferenceEquals(ready, affinity.Primary);
        }
        // Without Sentinel the pinned primary is the active endpoint. Sentinel records each
        // resolved primary before dialling it and clears the endpoint while querying Sentinels.
        var pinned = core.Sentinel is null ? core.Multiplexer.ActiveConnectionEndpoint : (RespireEndpoint?)null;
        try { await core.EnsureConnectedAsync(cancellationToken, observation).ConfigureAwait(false); }
        catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
        {
            ReadEndpointRouter.RecordCandidateFailure(observation, error, core.Sentinel is null ? pinned : observation.Endpoint);
            return false;
        }
        if (ReferenceEquals(core.Multiplexer, affinity.Primary)) return true;
        // Connecting recorded the replacement generation. A lost continuation belongs to the
        // pin's retired owner; a fresh selection records its own node afterwards.
        observation.UseEndpoint(affinity.Primary?.ActiveConnectionEndpoint, cancellationToken);
        return false;
    }

    // The pinned node is known before any connection work, so its failure names it.
    private static async ValueTask<RespireConnection> GetPinnedConnectionAsync(
        ReadEndpointRouter router, ReadAffinity affinity, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool readyOnly = false)
    {
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(affinity.ReadFrom)
            ? router.Core.Options.ClientAvailabilityZone : null;
        if (affinity.Replica is { } replica)
        {
            // The issuing replica owns this failure even when it has left the topology.
            observation.UseEndpoint(replica.Endpoint, cancellationToken);
            // Never recreate an entry for a replica removed from the topology.
            if (!router.IsCurrent(replica))
                throw new RespireConnectionException(
                    $"Read replica {replica.Endpoint} that issued the cursor was removed from the topology.");
            return await replica.GetConnectionAsync(cancellationToken, preferredZone, readyOnly).ConfigureAwait(false);
        }

        var core = router.Core;
        if (affinity.Primary is { } pinnedPrimary) observation.UseEndpoint(pinnedPrimary.ActiveConnectionEndpoint, cancellationToken);
        RespireConnectionMultiplexer multiplexer;
        if (readyOnly)
        {
            if (!core.TryGetReadyPrimaryMultiplexer(out multiplexer)) throw new ReadEndpointRouter.BacklogSelectionRequiredException();
        }
        else
        {
            await core.EnsureConnectedAsync(cancellationToken, observation).ConfigureAwait(false);
            multiplexer = core.Multiplexer;
        }
        if (!ReferenceEquals(multiplexer, affinity.Primary))
        {
            // Connecting recorded the replacement generation; the failure belongs to the cursor's owner.
            observation.UseEndpoint(affinity.Primary?.ActiveConnectionEndpoint);
            throw new RespireConnectionException("The primary that issued the cursor was replaced.");
        }
        return preferredZone is null ? multiplexer.GetConnection() : multiplexer.GetConnectionForZone(preferredZone);
    }
}
