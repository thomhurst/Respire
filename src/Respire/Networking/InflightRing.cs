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

    private readonly PendingResponse?[] _slots;
    private readonly int _mask;
    private long _head;
    private long _tail;

    public InflightRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        capacity = (int)BitOperations.RoundUpToPowerOf2((uint)capacity);
        _slots = new PendingResponse?[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _slots.Length;

    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Producer only (must be called under the connection's write gate).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(PendingResponse source)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _slots.Length)
        {
            return false;
        }

        _slots[tail & _mask] = source;
        Volatile.Write(ref _tail, tail + 1);
        return true;
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

        source = _slots[head & _mask]!;
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
    public long SweepExpired(long nowMilliseconds, TimeSpan timeout)
    {
        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);
        long next = -1;
        for (var position = head; position < tail; position++)
        {
            var source = Volatile.Read(ref _slots[position & _mask]);
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

            var remaining = deadline - nowMilliseconds;
            if (remaining > 0)
            {
                if (next < 0 || remaining < next)
                {
                    next = remaining;
                }

                continue;
            }

            source.TrySetTimedOut(state, timeout);
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

        var index = head & _mask;
        source = _slots[index]!;
        _slots[index] = null;
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
