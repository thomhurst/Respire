namespace Respire.Internal;

/// <summary>
/// Process-wide monotonic clock for Cluster slot owner-mutation fences. Receive loops read it
/// as soon as a <c>SMIGRATED</c> push is parsed, and routers read it when MOVED, slot clears or
/// discovery change an owner. Sharing one clock lets the connection capture the token before
/// any later maintenance processing, without knowing which router will consume it. Values are
/// only compared within one router, so a global sequence is sufficient.
/// </summary>
internal static class ClusterSlotMutationClock
{
    private static long s_value;

    internal static long Next() => Interlocked.Increment(ref s_value);
}
