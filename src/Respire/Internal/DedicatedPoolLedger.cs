namespace Respire.Internal;

/// <summary>
/// Owns dedicated pools through graceful retirement and explicit disposal. The router still
/// serializes publication and prevents additions after shutdown. Sharing its publication gate
/// makes snapshots wait for in-progress publications. Pool operations run outside that gate.
/// </summary>
internal sealed class DedicatedPoolLedger(object gate)
{
    private readonly HashSet<DedicatedConnectionPool> _pools = [];

    public int Count
    {
        get { lock (gate) return _pools.Count; }
    }

    internal void Add(DedicatedConnectionPool pool)
    {
        lock (gate) _pools.Add(pool);
    }

    private DedicatedConnectionPool[] Snapshot()
    {
        lock (gate) return _pools.ToArray();
    }

    internal async Task RetireAsync(DedicatedConnectionPool pool)
    {
        await pool.RetireAsync().ConfigureAwait(false);
        // Failed retirement stays owned so explicit disposal can still abort it.
        lock (gate) _pools.Remove(pool);
    }

    internal async ValueTask ReleaseAsync(DedicatedConnectionPool pool)
    {
        await pool.DisposeAsync().ConfigureAwait(false);
        lock (gate) _pools.Remove(pool);
    }

    internal Task RetireAllAsync()
        => Task.WhenAll(Snapshot().Select(RetireAsync));

    internal Task DisposeAllAsync()
        // Start every abort before awaiting any completion, even when one pool fails.
        => Task.WhenAll(Snapshot().Select(pool => ReleaseAsync(pool).AsTask()));
}
