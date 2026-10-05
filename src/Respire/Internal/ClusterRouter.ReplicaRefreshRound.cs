using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    // Empty replica snapshots remain useful evidence (for example, a promotion), but must not
    // publish or fence out another candidate that can still supply replicas for this slot.
    // Invariants: nonempty coverage can win immediately; only the newest empty evidence is
    // retained; empty evidence publishes after every probe fails. One batch plus the router's
    // topology/version fences prevents this round from overwriting a newer MOVED or discovery.
    private sealed class ReplicaRefreshRound(int slot, RespireConnectionMultiplexer? originalOwner)
    {
        internal readonly object SnapshotBatch = new();
        private readonly Lock _gate = new();
        private (List<ClusterTopologyRange> Ranges, long Version, long Generation)? _empty;
        private Exception? _failure;

        internal Exception? Failure => Volatile.Read(ref _failure);
        internal void RecordFailure(Exception error) => Interlocked.CompareExchange(ref _failure, error, null);

        internal bool Accept(List<ClusterTopologyRange> ranges, long version, long generation)
        {
            var covered = false;
            foreach (var range in ranges)
            {
                if (range.Start > slot || slot > range.End) continue;
                if (range.Replicas.Count != 0) return true;
                covered = true;
            }
            if (covered)
                lock (_gate)
                    if (_empty is not { } previous || generation > previous.Generation)
                        _empty = (ranges, version, generation);
            return false;
        }

        internal bool PublishEmpty(ClusterRouter router)
        {
            (List<ClusterTopologyRange> Ranges, long Version, long Generation)? candidate;
            lock (_gate) candidate = _empty;
            if (candidate is not { } snapshot) return false;
            // Empty evidence applies only to the requested slot. A promotion also removes the
            // promoted node from sibling replica sets, preserving their other known replicas.
            List<ClusterTopologyRange> ranges = [];
            lock (router._nodesGate)
            {
                // This is writer-side reconciliation under the node gate: staging includes
                // every accepted mutation, including any not yet published after a failure.
                // Read-only routing decisions instead capture the immutable snapshot.
                foreach (var range in snapshot.Ranges)
                {
                    if (range.Start > slot || range.End < slot) continue;
                    var retained = new Dictionary<ClusterReplicaSet, List<ClusterTopologyReplica>?>();
                    for (var current = range.Start; current <= range.End;)
                    {
                        if (current == slot)
                        {
                            ranges.Add(range with { Start = slot, End = slot });
                            current++;
                            continue;
                        }
                        var start = current;
                        var owner = router._slots[current];
                        var routes = router._replicasBySlot[current];
                        while (++current <= range.End && current != slot
                            && ReferenceEquals(router._slots[current], owner)
                            && ReferenceEquals(router._replicasBySlot[current], routes)) { }
                        if (originalOwner is null || !ReferenceEquals(owner, originalOwner) || routes is null) continue;
                        if (!retained.TryGetValue(routes, out var remaining))
                        {
                            var replicas = routes.Nodes.Select(node =>
                            {
                                router._identities.Replicas.TryGetId(node, out var id);
                                return new ClusterTopologyReplica(Endpoint(node), id, []);
                            }).ToList();
                            remaining = replicas.Where(replica =>
                                !MatchesPrimary(replica, range.Preferred, range.NodeId, range.Aliases)).ToList();
                            if (remaining.Count == replicas.Count) remaining = null;
                            retained.Add(routes, remaining);
                        }
                        if (remaining is not null)
                            ranges.Add(range with { Start = start, End = current - 1, Replicas = remaining });
                    }
                }
            }
            router.ApplyTopologyCore(ranges, snapshot.Version, snapshot.Generation,
                keepUncoveredOwners: true, snapshotBatch: SnapshotBatch);
            return true;
        }
    }
}
