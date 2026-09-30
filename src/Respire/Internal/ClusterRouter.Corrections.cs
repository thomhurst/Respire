using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
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
        List<DedicatedConnectionPool> unused = [];
        CorrectionPoolEntry entry;
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            foreach (var (key, candidate) in _correctionPools.ToArray())
            {
                if (ReferenceEquals(key.Multiplexer, identity.Multiplexer)
                    && key.Multiplexer?.HasCurrentPeer(key.PeerAddress, key.PeerPort) != true)
                {
                    _correctionPools.Remove(key);
                    candidate.Detached = true;
                    if (candidate.Users == 0) unused.Add(candidate.Pool);
                }
            }
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
                    _correctionPools.Add(identity, entry);
                else
                    entry.Detached = true;
            }
            entry.Users++;
        }
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
        return unused;
    }
}
