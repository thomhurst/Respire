namespace Respire.Internal;

/// <summary>
/// Process-wide monotonic clock for Cluster slot owner-mutation fences. Receive loops read it
/// as soon as they identify a <c>SMIGRATED</c> push, and routers read it when MOVED, slot clears
/// or discovery change an owner. Values are only compared within one router, so a global
/// sequence is sufficient.
/// </summary>
/// <remarks>
/// The clock is global rather than per router for two reasons. First, the receive loop reads it
/// before it knows which router, if any, will consume the push: a connection does not hold its
/// router. Second, it is cheap. It is read only for <c>SMIGRATED</c> pushes and for slot owner
/// changes. Both are rare topology events, and no command or ordinary push touches the clock, so
/// one shared counter adds no contention on the request path.
/// </remarks>
internal static class ClusterSlotMutationClock
{
    private static long s_value;

    internal static long Next() => Interlocked.Increment(ref s_value);
}
