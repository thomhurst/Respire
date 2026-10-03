using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>One publication of primary owners and replica membership for every Cluster slot.</summary>
/// <remarks>
/// Pages and membership arrays are never modified after publication. Transport lifetime and
/// replica selection/refresh coordination remain owned by the router and replica sets.
/// Point mutations copy one small page, avoiding a large-object allocation for each MOVED.
/// </remarks>
internal sealed class ClusterRoutingSnapshot
{
    internal const int PageSize = 256;
    private const int PageShift = 8;
    internal static ulong PageBit(int slot) => 1UL << (slot >> PageShift);
    private readonly SlotRoute[][] _pages;

    internal readonly record struct SlotRoute(RespireConnectionMultiplexer? Primary, ClusterReplicaSet? Replicas);

    private ClusterRoutingSnapshot(SlotRoute[][] pages, ImmutableArray<RespireConnectionMultiplexer> masters,
        ImmutableArray<RespireConnectionMultiplexer> replicaNodes, ImmutableArray<ClusterTopologyReplica> replicas,
        ImmutableArray<int> masterSlotCounts, bool complete)
    {
        _pages = pages;
        Masters = masters;
        ReplicaNodes = replicaNodes;
        Replicas = replicas;
        MasterSlotCounts = masterSlotCounts;
        IsComplete = complete;
    }

    internal static ClusterRoutingSnapshot Empty { get; } = CreateEmpty();
    internal ImmutableArray<RespireConnectionMultiplexer> Masters { get; }
    internal ImmutableArray<RespireConnectionMultiplexer> ReplicaNodes { get; }
    internal ImmutableArray<ClusterTopologyReplica> Replicas { get; }
    internal ImmutableArray<int> MasterSlotCounts { get; }
    internal bool IsComplete { get; }
    internal SlotRoute this[int slot] => _pages[slot >> PageShift][slot & (PageSize - 1)];

    private static ClusterRoutingSnapshot CreateEmpty()
    {
        var pages = new SlotRoute[ClusterHash.SlotCount / PageSize][];
        var empty = new SlotRoute[PageSize];
        Array.Fill(pages, empty);
        return new(pages, [], [], [], [], false);
    }

    // The router holds its writer gate. Dirty pages include primary and replica changes, so
    // no reader can observe a new primary paired with replicas from the previous publication.
    internal ClusterRoutingSnapshot Publish(RespireConnectionMultiplexer?[] owners,
        ClusterReplicaSet?[] routes, ulong dirtyPages, ImmutableArray<RespireConnectionMultiplexer> masters,
        ImmutableArray<RespireConnectionMultiplexer> replicaNodes, ImmutableArray<ClusterTopologyReplica> replicas,
        int[] masterSlotCounts, bool complete)
    {
        if (dirtyPages == 0 && complete == IsComplete
            && masters == Masters && replicaNodes == ReplicaNodes && replicas == Replicas
            && masterSlotCounts.AsSpan().SequenceEqual(MasterSlotCounts.AsSpan()))
            return this;
        var pages = dirtyPages == 0 ? _pages : (SlotRoute[][])_pages.Clone();
        while (dirtyPages != 0)
        {
            var pageIndex = System.Numerics.BitOperations.TrailingZeroCount(dirtyPages);
            dirtyPages &= dirtyPages - 1;
            var page = new SlotRoute[PageSize];
            var start = pageIndex * PageSize;
            for (var offset = 0; offset < page.Length; offset++)
                page[offset] = new(owners[start + offset], routes[start + offset]);
            pages[pageIndex] = page;
        }
        return new(pages, masters, replicaNodes, replicas,
            ImmutableCollectionsMarshal.AsImmutableArray((int[])masterSlotCounts.Clone()), complete);
    }
}
