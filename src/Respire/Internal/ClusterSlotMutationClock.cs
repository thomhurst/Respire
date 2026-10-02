using Respire.Infrastructure;

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
    private static readonly object s_gate = new();
    private static readonly SortedSet<long> s_activeCaptures = [];
    private static long s_value;

    internal static long Next()
    {
        lock (s_gate) return ++s_value;
    }

    internal static CaptureScope BeginCapture(RespireConnectionMultiplexer? multiplexer)
    {
        lock (s_gate)
        {
            var token = ++s_value;
            s_activeCaptures.Add(token);
            return new CaptureScope(token, multiplexer);
        }
    }

    internal static long EarliestActiveCapture
    {
        get
        {
            lock (s_gate) return s_activeCaptures.Count == 0 ? long.MaxValue : s_activeCaptures.Min;
        }
    }

    private static bool EndCapture(long token)
    {
        lock (s_gate) return s_activeCaptures.Remove(token);
    }

    internal readonly struct CaptureScope(long token, RespireConnectionMultiplexer? multiplexer) : IDisposable
    {
        internal long Token => token;

        public void Dispose()
        {
            if (EndCapture(token)) multiplexer?.PruneMaintenanceHandlerEpochs();
        }
    }
}
