namespace Respire.Internal;

/// <summary>
/// Owns dedicated pools through graceful retirement and explicit disposal. The router still
/// serializes publication and prevents additions after shutdown. Sharing its publication gate
/// makes snapshots wait for in-progress publications. Pool operations run outside that gate.
/// </summary>
/// <remarks>
/// One ledger each for <see cref="ClientCore"/>, <see cref="ClusterRouter"/>, and every Sentinel
/// generation; current route lookup stays with those owners. A replacement is added before it is
/// exposed; the previous pool stays until its graceful retirement succeeds, and a failed retirement
/// stays owned for explicit disposal. Completed pools are removed, not kept as history. Disposal
/// starts every pool's abort before awaiting any, so one failure cannot stop another borrowed lease
/// from being aborted; concurrent retirement and disposal share the pool's cleanup task. This
/// bookkeeping is off the healthy dispatch and lease-acquisition path; route-version validation, ASK
/// target selection, MOVING publication, deadlines, and drain rules stay with their owners.
/// </remarks>
internal sealed class DedicatedPoolLedger(Lock gate)
{
    private readonly HashSet<DedicatedConnectionPool> _pools = [];

    internal int Count
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

    internal async Task RetireAsync(DedicatedConnectionPool pool, bool moving = false)
    {
        await pool.RetireAsync(moving).ConfigureAwait(false);
        // Failed retirement stays owned so explicit disposal can still abort it.
        lock (gate) _pools.Remove(pool);
    }

    internal async Task ReleaseAsync(DedicatedConnectionPool pool)
    {
        await pool.DisposeAsync().ConfigureAwait(false);
        lock (gate) _pools.Remove(pool);
    }

    internal Task RetireAllAsync()
        => Task.WhenAll(Snapshot().Select(pool => RetireAsync(pool)));

    internal Task DisposeAllAsync()
        // Start every abort before awaiting any completion, even when one pool fails.
        => CleanupTasks.WhenAllAsync(Snapshot().Select(ReleaseAsync));
}
