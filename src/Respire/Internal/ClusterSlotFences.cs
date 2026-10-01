using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>
/// Per-slot owner-change fences that decide whether a queued <c>SMIGRATED</c> migration may
/// still move a slot. Not thread-safe: the router uses it only under its nodes gate.
/// </summary>
/// <remarks>
/// <para>
/// Every owner change records a token from <see cref="ClusterSlotMutationClock"/>. A migration
/// carries the token its receive loop read when the push arrived. If no owner change since
/// then is newer than that token, the migration is current and is not fenced.
/// </para>
/// <para>
/// One exception lets out-of-order receipt across connections work. A dependent migration
/// (<c>B→C</c>) can be received before the migration that gives its source the slot
/// (<c>A→B</c>), and so carry an older token. It may cross the fence only when all of these hold:
/// </para>
/// <list type="bullet">
/// <item>Its source owns the slot now.</item>
/// <item>Every owner change since its receipt was an <c>SMIGRATED</c> move. A <c>MOVED</c>
/// redirect, a slot clear or a discovery change ends the chain and always wins.</item>
/// <item>The chain began before its receipt.</item>
/// <item>Its source has not moved the slot away in that chain under a newer token. If it
/// had, the slot left the source and came back (<c>A→B</c>, <c>B→A</c>) after this
/// migration was received, so the older <c>A→C</c> belongs to an earlier ownership. A source
/// is matched by transport or by advertised endpoint, because a source that lost its last slot
/// is retired and gets a new transport when the slot comes back.</item>
/// </list>
/// </remarks>
internal sealed class ClusterSlotFences
{
    // A chain only grows through migrations received before its newest move, which the bounded
    // queue and deferral list limit. Past this length the chain closes conservatively: later
    // dependent migrations are fenced and MOVED handling or discovery corrects the slot.
    internal const int MaxChainLength = 256;

    // Invariant: a slot's version never decreases.
    private readonly long[] _versions = new long[ClusterHash.SlotCount];
    // The slot's version immediately before its current SMIGRATED chain began. Meaningful only
    // while _chains[slot] is not null.
    private readonly long[] _chainStarts = new long[ClusterHash.SlotCount];
    // Null when the latest owner change was not an SMIGRATED move (or the chain was closed).
    // Otherwise the moves of the current chain, newest first. Links are immutable and shared
    // between slots that one migration moved together.
    private readonly ChainLink?[] _chains = new ChainLink?[ClusterHash.SlotCount];

    private sealed class ChainLink(
        RespireConnectionMultiplexer departed, RespireEndpoint departedEndpoint, long token, ChainLink? previous)
    {
        internal readonly RespireConnectionMultiplexer Departed = departed;
        internal readonly RespireEndpoint DepartedEndpoint = departedEndpoint;
        internal readonly long Token = token;
        internal readonly ChainLink? Previous = previous;
        internal readonly int Length = (previous?.Length ?? 0) + 1;
    }

    internal long Version(int slot) => _versions[slot];

    /// <summary>Records an owner change that is not an <c>SMIGRATED</c> move.</summary>
    internal void MarkOwnerChanged(int slot)
    {
        _versions[slot] = ClusterSlotMutationClock.Next();
        _chains[slot] = null;
    }

    /// <summary>
    /// True when an owner change made after <paramref name="token"/> was read forbids moving
    /// <paramref name="slot"/> from <paramref name="source"/>, the active transport for the
    /// migration's advertised <paramref name="sourceEndpoint"/> (null when there is none).
    /// </summary>
    internal bool IsFenced(int slot, RespireConnectionMultiplexer? owner, RespireConnectionMultiplexer? source,
        RespireEndpoint sourceEndpoint, long token)
    {
        if (_versions[slot] <= token) return false;
        if (owner is null || !ReferenceEquals(owner, source)) return true;
        var chain = _chains[slot];
        if (chain is null || _chainStarts[slot] > token) return true;
        for (var link = chain; link is not null; link = link.Previous)
        {
            if (link.Token > token && (ReferenceEquals(link.Departed, source)
                    || ClusterNodeIdentityIndex.EndpointsEqual(link.DepartedEndpoint, sourceEndpoint)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Records that a migration received at <paramref name="token"/> moved
    /// <paramref name="slots"/> away from <paramref name="source"/>. Every slot must have
    /// passed <see cref="IsFenced"/> for the same source and token.
    /// </summary>
    internal void RecordMigration(List<int> slots, RespireConnectionMultiplexer source, RespireEndpoint sourceEndpoint,
        long token)
    {
        // Slots moved together usually share their chain, so reuse the link built for the
        // previous slot when its predecessor matches.
        ChainLink? fresh = null;
        ChainLink? lastPrevious = null;
        ChainLink? lastExtended = null;
        foreach (var slot in slots)
        {
            var prior = _versions[slot];
            if (prior <= token)
            {
                // Newer than every change so far: start a new chain at this move.
                _versions[slot] = token;
                _chainStarts[slot] = prior;
                _chains[slot] = fresh ??= new ChainLink(source, sourceEndpoint, token, null);
                continue;
            }

            // A dependent move crossed the fence: extend the chain and keep its start.
            var previous = _chains[slot]!;
            if (previous.Length >= MaxChainLength)
            {
                _chains[slot] = null;
                continue;
            }
            if (!ReferenceEquals(previous, lastPrevious))
            {
                lastPrevious = previous;
                lastExtended = new ChainLink(source, sourceEndpoint, token, previous);
            }
            _chains[slot] = lastExtended;
        }
    }
}
