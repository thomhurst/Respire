using System.Numerics;
using System.Runtime.CompilerServices;

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
    private string?[]? _discardedOperations;
    private readonly int _mask;
    private long _completedWriteEnd;
    private long _head;
    private long _tail;

    public InflightRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        _slots = new Slot[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _slots.Length;

    internal long CompletedWriteEnd => Volatile.Read(ref _completedWriteEnd);

    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Producer only (must be called under the connection's write gate).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(PendingResponse source, long writeEnd = 0)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _slots.Length)
        {
            return false;
        }

        ref var slot = ref _slots[tail & _mask];
        slot.Source = source;
        slot.WriteEnd = writeEnd;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    // Generation-owned connections retain discarded-reply metadata in a lazily allocated,
    // bounded array. Ordinary rings allocate no metadata array; the slot layout is unchanged.
    internal bool TryEnqueueDiscard(string operation, long writeEnd)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _slots.Length) return false;
        (_discardedOperations ??= new string?[_slots.Length])[tail & _mask] = operation;
        return TryEnqueue(DiscardSentinel, writeEnd);
    }

    internal bool TryDequeue(out PendingResponse source, out string? discardedOperation)
    {
        var head = _head;
        if (Volatile.Read(ref _tail) == head)
        {
            source = null!;
            discardedOperation = null;
            return false;
        }
        // Read and clear before releasing the slot to the producer in TryDequeue.
        // The receive loop is the only consumer, so the head cannot change here.
        discardedOperation = null;
        if (_discardedOperations is { } operations)
        {
            discardedOperation = operations[head & _mask];
            operations[head & _mask] = null;
        }
        return TryDequeue(out source);
    }

    /// <summary>Consumer only. Returns the head source without consuming it, so the receive
    /// loop can choose a specialized completion path before dequeuing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out PendingResponse source)
    {
        var head = _head;
        if (Volatile.Read(ref _tail) == head)
        {
            source = null!;
            return false;
        }

        source = _slots[head & _mask].Source!;
        return true;
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
        long maintenanceStarted = long.MinValue)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
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

            var deadline = source.Deadline;
            if (deadline == 0)
            {
                continue;
            }

            // A late notification cannot revive a deadline that elapsed before maintenance.
            var extension = deadline > maintenanceStarted ? deadlineExtension : 0;
            var remaining = deadline + extension - nowMilliseconds;
            if (remaining > 0)
            {
                if (next < 0 || remaining < next)
                {
                    next = remaining;
                }

                continue;
            }

            source.TrySetTimedOut(state, timeout + TimeSpan.FromMilliseconds(extension), ref diagnostics, connection);
        }

        return next;
    }

    /// <summary>Consumer only (receive loop, or the fail-all drain after the loop exits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out PendingResponse source)
    {
        var head = _head;
        if (Volatile.Read(ref _tail) == head)
        {
            source = null!;
            return false;
        }

        ref var slot = ref _slots[head & _mask];
        source = slot.Source!;
        // Intermediate replies carry the frame start; only the final reply advances past
        // the complete frame. This offset never retreats across FIFO-ordered frames.
        Volatile.Write(ref _completedWriteEnd, slot.WriteEnd);
        slot.Source = null;
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    // Keep a response and its byte position together instead of indexing two arrays.
    private struct Slot
    {
        internal PendingResponse? Source;
        internal long WriteEnd;
    }
}
