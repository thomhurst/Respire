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
        CancellationToken cancellationToken)
    {
        if (affinity is not null)
        {
            if (affinity.IsPinned) return await GetPinnedConnectionAsync(router, affinity, cancellationToken).ConfigureAwait(false);
            var first = await router.SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
            affinity.Replica = first.Replica;
            affinity.Primary = first.Primary;
            return first.Connection;
        }

        if (_shared.TryGetValue(readFrom, out var shared))
        {
            if (await IsPinCurrentAsync(router, shared, cancellationToken).ConfigureAwait(false))
                return await GetSharedPinnedConnectionAsync(router, readFrom, shared, cancellationToken).ConfigureAwait(false);
            _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
        }

        // The server that issued this cursor is gone, so no other server can continue it.
        if (isContinuation) throw CursorLost();

        await _sharedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shared.TryGetValue(readFrom, out shared))
            {
                if (await IsPinCurrentAsync(router, shared, cancellationToken).ConfigureAwait(false))
                    return await GetSharedPinnedConnectionAsync(router, readFrom, shared, cancellationToken).ConfigureAwait(false);
                _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
            }

            var selection = await router.SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
            _shared[readFrom] = new ReadAffinity { Replica = selection.Replica, Primary = selection.Primary };
            return selection.Connection;
        }
        finally { _sharedGate.Release(); }
    }

    internal async ValueTask<RespireConnection> GetClusterConnectionAsync(
        ClusterRouter cluster, int slot, RespireReadFrom readFrom, ReadAffinity? affinity,
        bool isContinuation, CancellationToken cancellationToken)
    {
        if (affinity is not null)
        {
            if (affinity.ClusterNode is { } pinned)
                return await cluster.GetPinnedReadConnectionAsync(slot, pinned, cancellationToken).ConfigureAwait(false);
            var first = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken).ConfigureAwait(false);
            affinity.ClusterSlot = slot;
            affinity.ClusterNode = first.Multiplexer;
            return first;
        }

        var key = (readFrom, slot);
        if (_clusterShared.TryGetValue(key, out var shared) && shared.ClusterNode is { } sharedNode)
        {
            try { return await cluster.GetPinnedReadConnectionAsync(slot, sharedNode, cancellationToken, revalidate: !isContinuation).ConfigureAwait(false); }
            catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
            {
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
                try { return await cluster.GetPinnedReadConnectionAsync(slot, currentNode, cancellationToken, revalidate: true).ConfigureAwait(false); }
                catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
                {
                    _clusterShared.TryRemove(new KeyValuePair<(RespireReadFrom, int), ReadAffinity>(key, shared));
                }
            }
            var connection = await cluster.GetReadConnectionAsync(slot, readFrom, cancellationToken).ConfigureAwait(false);
            _clusterShared[key] = new ReadAffinity { ClusterNode = connection.Multiplexer, ClusterSlot = slot };
            return connection;
        }
        finally { _sharedGate.Release(); }
    }

    /// <summary>Publishes a shared pin directly. Tests use it to model a pin that went stale.</summary>
    internal void PinShared(RespireReadFrom readFrom, ReadAffinity affinity) => _shared[readFrom] = affinity;

    internal void PinClusterShared(RespireReadFrom readFrom, int slot, RespireConnectionMultiplexer node)
        => _clusterShared[(readFrom, slot)] = new ReadAffinity { ClusterSlot = slot, ClusterNode = node };

    internal bool TryGetShared(RespireReadFrom readFrom, out ReadAffinity? affinity)
        => _shared.TryGetValue(readFrom, out affinity);

    internal void Clear() { _shared.Clear(); _clusterShared.Clear(); }

    private static RespireConnectionException CursorLost()
        => new("The server that issued this cursor left the read topology or failed. Restart the scan with cursor 0.");

    private async ValueTask<RespireConnection> GetSharedPinnedConnectionAsync(
        ReadEndpointRouter router, RespireReadFrom readFrom, ReadAffinity shared, CancellationToken cancellationToken)
    {
        try { return await GetPinnedConnectionAsync(router, shared, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken))
        {
            // The issuing server failed, so its cursors are lost. A fresh cursor reselects.
            _shared.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
            throw;
        }
    }

    private static async ValueTask<bool> IsPinCurrentAsync(
        ReadEndpointRouter router, ReadAffinity affinity, CancellationToken cancellationToken)
    {
        if (affinity.Replica is { } replica) return router.IsCurrent(replica);
        // An unreachable or replaced primary invalidates the pin so the policy can reselect.
        var core = router.Core;
        try { await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (ReadEndpointRouter.IsUnavailable(error, cancellationToken)) { return false; }
        return ReferenceEquals(core.Multiplexer, affinity.Primary);
    }

    private static async ValueTask<RespireConnection> GetPinnedConnectionAsync(
        ReadEndpointRouter router, ReadAffinity affinity, CancellationToken cancellationToken)
    {
        if (affinity.Replica is { } replica)
        {
            // Never recreate an entry for a replica removed from the topology.
            if (!router.IsCurrent(replica))
                throw new RespireConnectionException(
                    $"Read replica {replica.Endpoint} that issued the cursor was removed from the topology.");
            return await replica.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var core = router.Core;
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var multiplexer = core.Multiplexer;
        if (!ReferenceEquals(multiplexer, affinity.Primary))
            throw new RespireConnectionException("The primary that issued the cursor was replaced.");
        return multiplexer.GetConnection();
    }
}
