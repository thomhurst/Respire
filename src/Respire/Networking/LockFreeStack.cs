using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Respire.Networking;

/// <summary>
/// Thread-safe bounded LIFO stack using pre-allocated striped node arrays with stamped CAS heads.
/// Zero allocation in steady state: TryPush/TryPop only perform Interlocked operations
/// on stripe heads and pre-allocated nodes — no linked list nodes or wrapper objects are allocated.
/// </summary>
/// <remarks>
/// ConcurrentStack/ConcurrentQueue allocate a ~32-byte node per push. At high throughput these
/// short-lived allocations promote to Gen2 and cause a GC feedback loop. This array-based CAS
/// stack allocates only fixed-size arrays at construction time.
/// Large pools are striped by processor so hot rent/return paths do not contend on one cache
/// line; a pop starts at the current processor's stripe and steals from others on miss.
/// Version-stamped heads prevent ABA when nodes are rapidly recycled between the free and
/// available lists.
/// </remarks>
internal sealed class LockFreeStack<T> where T : class
{
    private const int MinCapacityForStriping = 64;
    private const int MaxStripeCount = 32;
    private const int MinSlotsPerStripe = 16;
    private const int EmptyIndex = -1;

    private readonly Stripe[] _stripes;

    private struct Node
    {
        public T? Item;
        public int Next;
    }

    private sealed class Stripe
    {
        public readonly Node[] Nodes;
        public LockFreeStackHeads Heads;

        public Stripe(int capacity)
        {
            Nodes = new Node[capacity];
            for (var i = 0; i < Nodes.Length; i++)
            {
                Nodes[i].Next = i + 1 < Nodes.Length ? i + 1 : EmptyIndex;
            }

            Volatile.Write(ref Heads.AvailableHead, PackHead(0, EmptyIndex));
            Volatile.Write(ref Heads.FreeHead, PackHead(0, 0));
        }
    }

    public LockFreeStack(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        var stripeCount = ComputeStripeCount(capacity);
        _stripes = new Stripe[stripeCount];

        var baseCapacity = capacity / stripeCount;
        var extraSlots = capacity % stripeCount;
        for (var i = 0; i < stripeCount; i++)
        {
            _stripes[i] = new Stripe(baseCapacity + (i < extraSlots ? 1 : 0));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPush(T item)
    {
        var stripeIndex = GetStartStripe();
        for (var i = 0; i < _stripes.Length; i++)
        {
            if (TryPush(_stripes[stripeIndex], item))
            {
                return true;
            }

            if (++stripeIndex == _stripes.Length)
            {
                stripeIndex = 0;
            }
        }

        return false;
    }

    private static bool TryPush(Stripe stripe, T item)
    {
        if (!TryTakeNode(ref stripe.Heads.FreeHead, stripe.Nodes, out var nodeIndex))
        {
            return false;
        }

        Volatile.Write(ref stripe.Nodes[nodeIndex].Item, item);
        PublishNode(ref stripe.Heads.AvailableHead, stripe.Nodes, nodeIndex);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop([NotNullWhen(true)] out T? item)
    {
        var stripeIndex = GetStartStripe();
        for (var i = 0; i < _stripes.Length; i++)
        {
            if (TryPop(_stripes[stripeIndex], out item))
            {
                return true;
            }

            if (++stripeIndex == _stripes.Length)
            {
                stripeIndex = 0;
            }
        }

        item = null;
        return false;
    }

    private static bool TryPop(Stripe stripe, [NotNullWhen(true)] out T? item)
    {
        if (!TryTakeNode(ref stripe.Heads.AvailableHead, stripe.Nodes, out var nodeIndex))
        {
            item = null;
            return false;
        }

        // The winning CAS in TryTakeNode gave this thread exclusive node ownership,
        // so plain reads/writes of Item are safe here.
        item = stripe.Nodes[nodeIndex].Item;
        stripe.Nodes[nodeIndex].Item = null;
        PublishNode(ref stripe.Heads.FreeHead, stripe.Nodes, nodeIndex);
        return item is not null;
    }

    private static bool TryTakeNode(ref long head, Node[] nodes, out int nodeIndex)
    {
        while (true)
        {
            var observedHead = Volatile.Read(ref head);
            nodeIndex = GetIndex(observedHead);
            if (nodeIndex == EmptyIndex)
            {
                return false;
            }

            var nextIndex = Volatile.Read(ref nodes[nodeIndex].Next);
            var updatedHead = NextHead(observedHead, nextIndex);
            if (Interlocked.CompareExchange(ref head, updatedHead, observedHead) == observedHead)
            {
                return true;
            }
        }
    }

    private static void PublishNode(ref long head, Node[] nodes, int nodeIndex)
    {
        while (true)
        {
            var observedHead = Volatile.Read(ref head);
            Volatile.Write(ref nodes[nodeIndex].Next, GetIndex(observedHead));
            var updatedHead = NextHead(observedHead, nodeIndex);
            if (Interlocked.CompareExchange(ref head, updatedHead, observedHead) == observedHead)
            {
                return;
            }
        }
    }

    private static long PackHead(int version, int index)
        => ((long)version << 32) | (uint)index;

    private static long NextHead(long observedHead, int index)
        => PackHead(unchecked((int)(observedHead >> 32) + 1), index);

    private static int GetIndex(long head)
        => (int)head;

    private static int ComputeStripeCount(int capacity)
    {
        if (capacity < MinCapacityForStriping)
        {
            return 1;
        }

        var maxByCapacity = capacity / MinSlotsPerStripe;
        return Math.Min(Math.Min(Environment.ProcessorCount, MaxStripeCount), maxByCapacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetStartStripe()
        => _stripes.Length == 1
            ? 0
            : (Thread.GetCurrentProcessorId() & int.MaxValue) % _stripes.Length;
}

// Renters CAS AvailableHead then FreeHead; returners do the reverse, often on other
// threads. Stripe objects are allocated back to back with their node arrays, so 128
// bytes before, between and after the two heads keep them off any cache line (64 or
// 128 bytes) shared with each other or with a neighbouring stripe. Declared outside
// LockFreeStack<T> because the runtime rejects explicit layout on generic types.
[StructLayout(LayoutKind.Explicit, Size = 384)]
internal struct LockFreeStackHeads
{
    [FieldOffset(128)] public long AvailableHead;
    [FieldOffset(256)] public long FreeHead;
}
