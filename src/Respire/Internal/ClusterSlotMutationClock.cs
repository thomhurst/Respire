namespace Respire.Internal;

/// <summary>
/// Process-wide monotonic clock for Cluster slot owner-mutation fences and maintenance-handler
/// subscription epochs. Receive loops read it as soon as they identify a <c>SMIGRATED</c> push;
/// routers read it when MOVED, slot clears or discovery change an owner.
/// </summary>
/// <remarks>
/// The clock is global rather than per router because the receive loop reads it before it knows
/// which router will consume the push. The shared sequence also orders that receipt against
/// handler attachment and detachment. Reads occur only for <c>SMIGRATED</c> pushes, handler
/// subscription changes and slot owner changes; no command or ordinary push touches the clock.
/// </remarks>
internal static class ClusterSlotMutationClock
{
    private static long s_value;

    internal static long Next() => Interlocked.Increment(ref s_value);
}
