using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Respire.Networking;

/// <summary>
/// Bounded single-producer/single-consumer FIFO of in-flight commands awaiting responses.
/// RESP has no correlation ids — responses arrive in exactly the order commands were written,
/// so the connection pairs each parsed response with the head of this ring.
/// </summary>
/// <remarks>
/// Producer side is the command writer (already serialized by the connection's write gate, so
/// effectively single-producer); consumer side is the receive loop. Slots are published with
/// release semantics via the tail counter. A fire-and-forget command enqueues
/// <see cref="DiscardSentinel"/> so its response is still consumed from the wire but not
/// delivered anywhere.
/// </remarks>
internal sealed class InflightRing
{
    /// <summary>Marks a slot whose response should be read and thrown away.</summary>
    public static readonly PendingResponseSource DiscardSentinel = new();

    private readonly Slot[] _slots;
    private DiscardedReply[]? _discardedReplies;
    private readonly int _mask;
    private Positions _positions;

    // The regions need not start on a cache-line boundary: 128 bytes between the
    // producer and consumer counters prevents sharing on 64/128-byte cache lines.
    [StructLayout(LayoutKind.Explicit, Size = 272)]
    private struct Positions
    {
        [FieldOffset(0)] internal long Tail;
        [FieldOffset(8)] internal long CachedHead;
        [FieldOffset(144)] internal long Head;
        [FieldOffset(152)] internal long CompletedWriteEnd;
    }

    public InflightRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        _slots = new Slot[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _slots.Length;

    internal long CompletedWriteEnd => Volatile.Read(ref _positions.CompletedWriteEnd);

    public int Count => (int)(Volatile.Read(ref _positions.Tail) - Volatile.Read(ref _positions.Head));

    /// <summary>Monotonic head position; only the consumer can advance it.</summary>
    internal long ConsumerPosition => Volatile.Read(ref _positions.Head);

    /// <summary>
    /// Counts queued replies and an active streamed reply using the same head snapshot.
    /// A negative position means no active stream. The caller must validate that the active
    /// position did not change during observation; this method never reads consumer-owned slots.
    /// </summary>
    internal int CountIncludingActiveReply(long activeReplyPosition)
    {
        var tail = Volatile.Read(ref _positions.Tail);
        var head = Volatile.Read(ref _positions.Head);
        var count = Math.Max(0, (int)(tail - head));
        return activeReplyPosition >= 0 && activeReplyPosition < head ? count + 1 : count;
    }

    /// <summary>Producer only (must be called under the connection's write gate).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(PendingResponse source, long writeEnd = 0)
    {
        var tail = _positions.Tail;
        if (!HasCapacity(1))
        {
            return false;
        }

        PublishSlot(tail, source, writeEnd);
        return true;
    }

    // Generation bookkeeping, publication metrics, and retried submissions retain metadata
    // in a lazily allocated, bounded array. Other rings allocate none; the slot layout is unchanged.
    internal bool TryEnqueueDiscard(string? operation, long writeEnd, int retryAttempts = 0)
    {
        var tail = _positions.Tail;
        if (!HasCapacity(1)) return false;
        if (operation is not null || retryAttempts != 0)
            (_discardedReplies ??= new DiscardedReply[_slots.Length])[tail & _mask] = new(operation, retryAttempts);
        PublishSlot(tail, DiscardSentinel, writeEnd);
        return true;
    }

    // Both callers admit capacity under the write gate before publishing. The consumer
    // can only free slots in between; no other producer can consume the admitted slot.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishSlot(long tail, PendingResponse source, long writeEnd)
    {
        ref var slot = ref _slots[tail & _mask];
        slot.Source = source;
        slot.WriteEnd = writeEnd;
        Volatile.Write(ref _positions.Tail, tail + 1);
    }

    /// <summary>Producer only, under the write gate. Refreshes the cached head only when capacity looks insufficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasCapacity(int count)
    {
        var tail = _positions.Tail;
        if (_slots.Length - (tail - _positions.CachedHead) >= count) return true;
        _positions.CachedHead = Volatile.Read(ref _positions.Head);
        return _slots.Length - (tail - _positions.CachedHead) >= count;
    }

    internal bool TryDequeue(out PendingResponse source, out string? discardedOperation)
        => TryDequeue(out source, out discardedOperation, out _);

    /// <summary>Consumer only. Returns the head source without consuming it, so the receive
    /// loop can choose a specialized completion path before dequeuing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out PendingResponse source)
    {
        var head = _positions.Head;
        if (Volatile.Read(ref _positions.Tail) == head)
        {
            source = null!;
            return false;
        }

        source = _slots[head & _mask].Source!;
        return true;
    }

    internal bool HasOtherIncompleteCommand(string commandName)
    {
        var head = Volatile.Read(ref _positions.Head);
        var tail = Volatile.Read(ref _positions.Tail);
        for (var position = head; position < tail; position++)
        {
            var source = Volatile.Read(ref _slots[position & _mask].Source);
            if (source is null || ReferenceEquals(source, DiscardSentinel) || source.CommandName == commandName)
                continue;
            if (!PendingResponse.IsCompleted(source.State)) return true;
        }

        return false;
    }

    /// <summary>
    /// Deadline-sweep only. Scans every published slot, timing out sources whose armed
    /// deadline has passed, and returns milliseconds until the earliest remaining armed
    /// deadline (or -1 when none is armed). The whole occupied span is examined — deadlines
    /// are near-monotonic in enqueue order, but a producer that waited out a full ring is
    /// re-stamped with its original deadline and can land behind a later-expiring entry, so
    /// an early exit could strand it. Lock-free against both sides: slots between the head
    /// and tail snapshots are published; a slot observed mid-dequeue reads null and is
    /// skipped; a source recycled after its state was captured is rejected by the epoch CAS
    /// inside <see cref="PendingResponse.TrySetTimedOut"/>.
    /// </summary>
    public long SweepExpired(long nowMilliseconds, TimeSpan timeout, RespireConnection? connection, long deadlineExtension = 0,
        long maintenanceStarted = long.MinValue, TimeSpan? alreadyRelaxedTimeout = null)
    {
        var head = Volatile.Read(ref _positions.Head);
        var tail = Volatile.Read(ref _positions.Tail);
        long next = -1;
        RespireTimeoutDiagnostics? diagnostics = null;
        for (var position = head; position < tail; position++)
        {
            var source = Volatile.Read(ref _slots[position & _mask].Source);
            if (source is null || ReferenceEquals(source, DiscardSentinel))
            {
                continue;
            }

            // State must be read before Deadline: the pool-return path clears the deadline
            // before its release-store of the new epoch, so a state read that sees the old
            // epoch pairs only with that incarnation's deadline, and one that sees the new
            // epoch can no longer see the previous command's expired deadline.
            var state = source.State;
            if (PendingResponse.IsCompleted(state))
            {
                continue;
            }

            var commandDeadline = source.Deadline;
            var alreadyRelaxed = commandDeadline.IsRelaxed;
            var deadline = commandDeadline.Ticks;
            if (deadline == 0)
            {
                continue;
            }

            // A late notification cannot revive a deadline that elapsed before maintenance.
            var extension = !alreadyRelaxed && MaintenanceTimeoutState.Relaxes(deadline, maintenanceStarted) ? deadlineExtension : 0;
            var remaining = deadline + extension - nowMilliseconds;
            if (remaining > 0)
            {
                if (next < 0 || remaining < next)
                {
                    next = remaining;
                }

                continue;
            }

            var reportedTimeout = timeout + TimeSpan.FromMilliseconds(extension);
            if (alreadyRelaxed && alreadyRelaxedTimeout is { } relaxedTimeout && relaxedTimeout > reportedTimeout)
                reportedTimeout = relaxedTimeout;
            source.TrySetTimedOut(state, reportedTimeout, ref diagnostics, connection);
        }

        return next;
    }

    /// <summary>Consumer only (receive loop, or the fail-all drain after the loop exits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out PendingResponse source)
    {
        var head = _positions.Head;
        if (Volatile.Read(ref _positions.Tail) == head)
        {
            source = null!;
            return false;
        }

        ref var slot = ref _slots[head & _mask];
        source = slot.Source!;
        // Callers that ignore metadata still release its references before slot reuse.
        // Avoid reading the record and constructing unused outputs on this common path.
        if (_discardedReplies is { } replies) replies[head & _mask] = default;
        Volatile.Write(ref _positions.CompletedWriteEnd, slot.WriteEnd);
        slot.Source = null;
        Volatile.Write(ref _positions.Head, head + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryDequeue(out PendingResponse source, out string? discardedOperation, out int retryAttempts)
    {
        var head = _positions.Head;
        discardedOperation = null;
        retryAttempts = 0;
        if (Volatile.Read(ref _positions.Tail) == head)
        {
            source = null!;
            return false;
        }

        ref var slot = ref _slots[head & _mask];
        source = slot.Source!;
        // Capture immutable counts and clear references before releasing this slot for reuse.
        if (_discardedReplies is { } replies)
        {
            var reply = replies[head & _mask];
            discardedOperation = reply.Operation;
            retryAttempts = reply.RetryAttempts;
            replies[head & _mask] = default;
        }
        // Intermediate replies carry the frame start; only the final reply advances past
        // the complete frame. This offset never retreats across FIFO-ordered frames.
        Volatile.Write(ref _positions.CompletedWriteEnd, slot.WriteEnd);
        slot.Source = null;
        Volatile.Write(ref _positions.Head, head + 1);
        return true;
    }

    // Keep a response and its byte position together instead of indexing two arrays.
    private struct Slot
    {
        internal PendingResponse? Source;
        internal long WriteEnd;
    }

    private readonly record struct DiscardedReply(string? Operation, int RetryAttempts);
}
