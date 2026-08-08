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
