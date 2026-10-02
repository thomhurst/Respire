using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>
/// Immutable replica routes for one Cluster slot range, plus that range's selection cursor and
/// topology-refresh throttle.
/// </summary>
/// <remarks>
/// Every slot in a range shares one instance. The cursor is therefore per range, not per slot:
/// load spreads evenly across a range's replicas even when traffic concentrates on a few slots,
/// and memory does not scale with the 16384 slots. A refresh that reports identical routes keeps
/// the existing instance, so the cursor and throttle survive it. Ranges throttle independently:
/// a shard without replicas does not delay discovery for a shard that has just gained one.
/// <para>
/// Successful replica reads produce no redirect, so nothing else would notice a failover that
/// promotes one of these replicas: its read-only connections would keep serving reads as a
/// primary. Each set therefore records when its routes were last confirmed, and a read that finds
/// them older than the revalidation interval starts a background refresh.
/// </para>
/// </remarks>
internal sealed class ClusterReplicaSet
{
    /// <summary>Minimum interval between topology refreshes started from one replica set.</summary>
    internal const long RefreshIntervalMilliseconds = 1_000;

    private readonly Func<long>? _clock;
    private long Now => _clock?.Invoke() ?? Environment.TickCount64;

    private int _cursor;
    private long _refreshNotBefore;
    private long _revalidateAt;
    private Task? _refresh;

    internal ClusterReplicaSet(RespireConnectionMultiplexer[] nodes, TimeSpan revalidationInterval, Func<long>? clock = null)
    {
        _clock = clock;
        Nodes = nodes;
        MarkValidated(revalidationInterval);
    }

    internal RespireConnectionMultiplexer[] Nodes { get; }

    /// <summary>True once the routes are older than the revalidation interval.</summary>
    internal bool IsDueForRevalidation => Now >= Volatile.Read(ref _revalidateAt);

    /// <summary>Records that a topology refresh has just confirmed these routes.</summary>
    internal void MarkValidated(TimeSpan revalidationInterval)
        => Volatile.Write(ref _revalidateAt, Now + (long)revalidationInterval.TotalMilliseconds);

    /// <summary>Returns the next round-robin starting index. Requires a nonempty set.</summary>
    internal int NextStart()
        => (int)((uint)Interlocked.Increment(ref _cursor) % (uint)Nodes.Length);

    /// <summary>
    /// Joins the refresh already in flight, starts one when the interval has elapsed, or returns
    /// null while the set is throttled. The returned task never faults.
    /// </summary>
    /// <remarks>
    /// Concurrent callers share one refresh instead of each sending CLUSTER SLOTS, and callers that
    /// arrive while it runs wait for its result rather than failing against the stale routes.
    /// </remarks>
    internal Task? JoinOrStartRefresh(Func<Task> refresh)
    {
        while (true)
        {
            var current = Volatile.Read(ref _refresh);
            if (current is { IsCompleted: false }) return current;
            var now = Now;
            if (now < Volatile.Read(ref _refreshNotBefore)) return null;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _refresh, completion.Task, current), current))
            {
                continue;
            }

            // Publish the throttle before running the refresh, so a fast completion cannot let a
            // racing caller start a second refresh in the same interval.
            Volatile.Write(ref _refreshNotBefore, now + RefreshIntervalMilliseconds);
            _ = RunRefreshAsync(refresh, completion);
            return completion.Task;
        }
    }

    private static async Task RunRefreshAsync(Func<Task> refresh, TaskCompletionSource completion)
    {
        try
        {
            await refresh().ConfigureAwait(false);
        }
        catch
        {
            // The router logs refresh failures. Waiters re-read the routes either way.
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    internal bool HasSameNodes(RespireConnectionMultiplexer[] nodes)
    {
        if (ReferenceEquals(Nodes, nodes)) return true;
        if (Nodes.Length != nodes.Length) return false;
        for (var index = 0; index < nodes.Length; index++)
        {
            if (!ReferenceEquals(Nodes[index], nodes[index])) return false;
        }

        return true;
    }
}
