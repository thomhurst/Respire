using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly Dictionary<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>> _correctionStateHandlers = [];

    private readonly record struct CorrectionPoolIdentity(
        RespireConnectionMultiplexer? Multiplexer, string PeerAddress, int PeerPort, string TlsHost);

    internal sealed class CorrectionPoolEntry(DedicatedConnectionPool pool)
    {
        internal readonly DedicatedConnectionPool Pool = pool;
        internal int Users;
        internal bool Detached;
    }

    // A reservation covers both asynchronous rent and command execution. Detachment cannot
    // retire its pool between selecting the pool and registering a borrowed connection.
    internal sealed class CorrectionLease(ClusterRouter owner, CorrectionPoolEntry entry) : IAsyncDisposable
    {
        private int _released;
        internal DedicatedConnectionPool Pool => entry.Pool;
        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _released, 1) == 0 ? owner.ReleaseCorrectionAsync(entry) : default;
    }

    internal CorrectionLease GetCorrectionLease(RespireConnection original)
    {
        var identity = new CorrectionPoolIdentity(original.Multiplexer,
            original.NetworkPeerAddress ?? original.Host, original.NetworkPeerPort ?? original.Port, original.Host);
        List<DedicatedConnectionPool> unused;
        CorrectionPoolEntry entry;
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_correctionPools.TryGetValue(identity, out entry!))
            {
                var options = (original.Multiplexer?.Options ?? _options.ToConnectionOptions()) with
                {
                    EnableClientTracking = false, PushHandler = null, SubscriptionConfirmationHandler = null,
                };
                if (options.UseTls)
                    options = options with { TlsOptions = RespireConnection.CreateTlsOptions(options.TlsOptions, original.Host) };
                var pool = new DedicatedConnectionPool(identity.PeerAddress, identity.PeerPort, options,
                    _options.CreateLogger($"Respire.Cluster.Correction.{original.Host}:{original.Port}"));
                entry = new(pool);
                _ownedPools.Add(pool);
                if (identity.Multiplexer is { } node && _identities.IsActive(node)
                    && !_retiringNodes.ContainsKey(node) && node.HasCurrentPeer(identity.PeerAddress, identity.PeerPort))
                {
                    _correctionPools.Add(identity, entry);
                    ObserveCorrectionPeersLocked(node);
                }
                else
                    entry.Detached = true;
            }
            entry.Users++;
            // Reconnect publication does not take this gate. Recheck after subscribing so
            // a peer change just before observer installation cannot leave a stale cache entry.
            // Reserve this lease first: pruning must not retire its pool before rent completes.
            unused = PruneCorrectionPeersLocked(identity.Multiplexer);
        }
        // RetirePoolAsync logs failures and leaves failed pools owned for explicit disposal.
        foreach (var pool in unused) _ = RetirePoolAsync(pool);
        return new(this, entry);
    }

    private ValueTask ReleaseCorrectionAsync(CorrectionPoolEntry entry)
    {
        lock (_nodesGate)
        {
            if (--entry.Users != 0 || !entry.Detached) return default;
        }
        return new(RetirePoolAsync(entry.Pool));
    }

    private List<DedicatedConnectionPool> DetachCorrectionPoolsLocked(RespireConnectionMultiplexer node)
    {
        List<DedicatedConnectionPool> unused = [];
        foreach (var (identity, entry) in _correctionPools.ToArray())
        {
            if (!ReferenceEquals(identity.Multiplexer, node)) continue;
            _correctionPools.Remove(identity);
            entry.Detached = true;
            if (entry.Users == 0) unused.Add(entry.Pool);
        }
        RemoveCorrectionObserverLocked(node);
        return unused;
    }

    private List<DedicatedConnectionPool> PruneCorrectionPeersLocked(RespireConnectionMultiplexer? node)
    {
        List<DedicatedConnectionPool> unused = [];
        foreach (var (identity, entry) in _correctionPools.ToArray())
        {
            if (!ReferenceEquals(identity.Multiplexer, node)
                || node?.HasCurrentPeer(identity.PeerAddress, identity.PeerPort) == true) continue;
            _correctionPools.Remove(identity);
            entry.Detached = true;
            if (entry.Users == 0) unused.Add(entry.Pool);
        }
        if (node is not null && !_correctionPools.Keys.Any(identity => ReferenceEquals(identity.Multiplexer, node)))
            RemoveCorrectionObserverLocked(node);
        return unused;
    }

    private void ObserveCorrectionPeersLocked(RespireConnectionMultiplexer node)
    {
        if (_correctionStateHandlers.ContainsKey(node)) return;
        Action<int, RespireConnectionStateChange> handler = (slot, change) =>
        {
            // Keep the pool through a reconnect to the same peer. Only publication of a
            // replacement reveals whether DNS actually changed its physical destination.
            if (change.State != RespireConnectionState.Connected) return;
            List<DedicatedConnectionPool> unused;
            lock (_nodesGate) unused = PruneCorrectionPeersLocked(node);
            // RetirePoolAsync logs failures and leaves failed pools owned for explicit disposal.
            foreach (var pool in unused) _ = RetirePoolAsync(pool);
        };
        _correctionStateHandlers.Add(node, handler);
        node.SlotStateChanged += handler;
    }

    private void RemoveCorrectionObserverLocked(RespireConnectionMultiplexer node)
    {
        if (_correctionStateHandlers.Remove(node, out var handler)) node.SlotStateChanged -= handler;
    }
}
