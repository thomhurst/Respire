using System.Runtime.CompilerServices;
using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>
/// Connection-scoped deduplication and bounded, out-of-order SMIGRATED dependencies.
/// Deferred entries and retries require the router's nodes gate; sequence claims have their
/// own gate because they precede parsing. Node identities are opaque to this state machine.
/// </summary>
internal sealed class ClusterMigrationState<TNode> where TNode : class
{
    internal const int DeferredMigrationLimit = 64;
    // A dependency normally arrives within milliseconds. Older entries must not apply if
    // their source regains the slots much later.
    internal const long DeferredLifetimeMilliseconds = 30_000;

    private readonly ConditionalWeakTable<object, SequenceWindow> _sequences = new();
    private readonly object _sequenceGate = new();
    private readonly List<DeferredMigration> _deferred = [];
    private int _deferredSlots;

    private readonly Func<long> _clock;

    internal ClusterMigrationState(Func<long>? clock = null)
        => _clock = clock ?? (static () => Environment.TickCount64);

    internal long Timestamp => _clock();
    internal int DeferredCount => _deferred.Count;
    internal int DeferredSlots => _deferredSlots;

    internal delegate AppliedMove? ApplyMigration(RespireEndpoint source, RespireEndpoint target,
        int[] slots, long token, out int[]? waiting);

    internal bool TryRecordSequence(object scope, long sequence)
    {
        lock (_sequenceGate) return _sequences.GetOrCreateValue(scope).TryAdd(sequence);
    }

    internal void ForgetSequence(object scope)
    {
        lock (_sequenceGate) _sequences.Remove(scope);
    }

    internal void ClearDeferred()
    {
        _deferred.Clear();
        _deferredSlots = 0;
    }

    private sealed class SequenceWindow
    {
        private long _highestSeen = -1;

        // Sequence IDs increase within one physical connection. A high-water mark rejects
        // delayed replays without retaining an unbounded set or forgetting old IDs.
        internal bool TryAdd(long sequence)
        {
            if (sequence <= _highestSeen) return false;
            _highestSeen = sequence;
            return true;
        }
    }

    // A migration entry whose slots were not yet owned by its advertised source when it ran.
    // Receive loops on different connections can enqueue dependent notifications out of order
    // (B->C before A->B). The entry is retried when a migration moves its slots to its source,
    // under its original fence token, so a MOVED, discovery change or newer migration still
    // rejects it. Entries expire after DeferredLifetimeMilliseconds. Slots are sorted.
    internal sealed class DeferredMigration(
        RespireEndpoint source, RespireEndpoint target, int[] slots, long token, long deferredAt, TNode sender)
    {
        internal readonly RespireEndpoint Source = source;
        internal readonly RespireEndpoint Target = target;
        internal readonly long Token = token;
        internal readonly long DeferredAt = deferredAt;
        internal readonly TNode Sender = sender;
        internal int[] Slots = slots;
    }

    // A migration that just moved Slots (sorted ascending) to Target.
    internal readonly record struct AppliedMove(TNode Target, int[] Slots);

    internal void Defer(DeferredMigration deferred,
        List<(string Reason, TNode Sender)> skippedMetrics)
    {
        _deferred.Add(deferred);
        _deferredSlots += deferred.Slots.Length;
        while (_deferred.Count > DeferredMigrationLimit
               || _deferredSlots > ClusterHash.SlotCount)
        {
            var evicted = _deferred[0];
            _deferredSlots -= evicted.Slots.Length;
            _deferred.RemoveAt(0);
            skippedMetrics.Add(("deferral_evicted", evicted.Sender));
        }
    }

    // Drops entries whose dependency has not arrived within DeferredLifetimeMilliseconds.
    // Expiry is lazy on the next notification; retained state stays bounded in the meantime.
    // The list is oldest first, so expiry stops at the first entry that is still young.
    internal void Expire(List<(string Reason, TNode Sender)> skippedMetrics)
    {
        if (_deferred.Count == 0) return;
        var now = Timestamp;
        var expired = 0;
        while (expired < _deferred.Count
               && now - _deferred[expired].DeferredAt >= DeferredLifetimeMilliseconds)
        {
            var deferred = _deferred[expired++];
            _deferredSlots -= deferred.Slots.Length;
            skippedMetrics.Add(("deferral_expired", deferred.Sender));
        }
        if (expired == 0) return;
        _deferred.RemoveRange(0, expired);
    }

    // Retries only the entries a move made runnable: those whose source is the node that just
    // received some of their slots. Only an SMIGRATED move can unblock an entry, because MOVED,
    // slot clears and discovery reset the slot's chain and fence it. Moves made here are queued
    // in turn, so chains resolve in one call. Each retry removes slots from the list, so the
    // work under the router gate is bounded by the list size rather than by repeated full passes.
    internal void RetryDependencies(Queue<AppliedMove> applied,
        Func<RespireEndpoint, TNode?> resolveSource, ApplyMigration apply)
    {
        while (_deferred.Count > 0 && applied.TryDequeue(out var move))
        {
            for (var i = 0; i < _deferred.Count; i++)
            {
                var deferred = _deferred[i];
                if (!ReferenceEquals(resolveSource(deferred.Source), move.Target))
                    continue;
                var ready = SplitSortedSlots(deferred.Slots, move.Slots, out var remaining);
                if (ready is null) continue;

                if (apply(deferred.Source, deferred.Target, ready, deferred.Token, out var waiting) is { } next)
                    applied.Enqueue(next);
                // Slots that another entry moved first keep waiting.
                if (waiting is not null) remaining = MergeSortedSlots(remaining, waiting);
                _deferredSlots -= deferred.Slots.Length - remaining.Length;
                if (remaining.Length == 0) _deferred.RemoveAt(i--);
                else deferred.Slots = remaining;
            }
        }
    }

    // Splits sorted slots into those also in sorted moved (returned, or null when none) and the rest.
    private static int[]? SplitSortedSlots(int[] slots, int[] moved, out int[] rest)
    {
        List<int>? common = null;
        var others = new List<int>(slots.Length);
        var j = 0;
        foreach (var slot in slots)
        {
            while (j < moved.Length && moved[j] < slot) j++;
            if (j < moved.Length && moved[j] == slot) (common ??= []).Add(slot);
            else others.Add(slot);
        }
        rest = common is null ? slots : [.. others];
        return common?.ToArray();
    }

    private static int[] MergeSortedSlots(int[] first, int[] second)
    {
        var merged = new int[first.Length + second.Length];
        var a = 0;
        var b = 0;
        for (var i = 0; i < merged.Length; i++)
            merged[i] = b == second.Length || (a < first.Length && first[a] <= second[b])
                ? first[a++] : second[b++];
        return merged;
    }
}
