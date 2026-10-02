using System.Runtime.CompilerServices;
using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>
/// Process-wide monotonic clock for Cluster slot owner-mutation fences and maintenance-handler
/// subscription epochs. Receive loops read it as soon as they identify a <c>SMIGRATED</c> push;
/// routers read it when MOVED, slot clears or discovery change an owner. Capture scopes belong
/// to one multiplexer, so completing a push only prunes that multiplexer’s handler epochs.
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
    private static readonly SortedSet<long> s_globalCaptures = [];
    private static readonly ConditionalWeakTable<RespireConnectionMultiplexer, SortedSet<long>> s_multiplexerCaptures = new();
    private static readonly List<WeakReference<RespireConnectionMultiplexer>> s_multiplexers = [];
    private static long s_value;

    internal static long Next()
    {
        lock (s_gate) return ++s_value;
    }

    internal static void Track(RespireConnectionMultiplexer multiplexer)
    {
        lock (s_gate)
        {
            for (var i = s_multiplexers.Count - 1; i >= 0; i--)
            {
                if (!s_multiplexers[i].TryGetTarget(out var existing))
                    s_multiplexers.RemoveAt(i);
                else if (ReferenceEquals(existing, multiplexer))
                    return;
            }
            s_multiplexers.Add(new(multiplexer));
        }
    }

    internal static CaptureScope BeginCapture(RespireConnectionMultiplexer? multiplexer = null)
    {
        lock (s_gate)
        {
            var token = ++s_value;
            if (multiplexer is null) s_globalCaptures.Add(token);
            else s_multiplexerCaptures.GetOrCreateValue(multiplexer).Add(token);
            return new CaptureScope(token, multiplexer);
        }
    }

    internal static bool HasActiveCapture(RespireConnectionMultiplexer multiplexer, long start, long end)
    {
        lock (s_gate)
        {
            if (ContainsCaptureInRange(s_globalCaptures, start, end)) return true;
            return s_multiplexerCaptures.TryGetValue(multiplexer, out var captures)
                && ContainsCaptureInRange(captures, start, end);
        }
    }

    private static bool ContainsCaptureInRange(SortedSet<long> captures, long start, long end)
    {
        foreach (var capture in captures)
        {
            if (capture >= end) return false;
            if (capture >= start) return true;
        }
        return false;
    }

    private static RespireConnectionMultiplexer[] EndCapture(long token, RespireConnectionMultiplexer? multiplexer)
    {
        lock (s_gate)
        {
            if (multiplexer is null)
            {
                if (!s_globalCaptures.Remove(token)) return [];
            }
            else if (!s_multiplexerCaptures.TryGetValue(multiplexer, out var captures) || !captures.Remove(token))
                return [];
            if (multiplexer is not null) return [multiplexer];
            var active = new List<RespireConnectionMultiplexer>(s_multiplexers.Count);
            for (var i = s_multiplexers.Count - 1; i >= 0; i--)
            {
                if (s_multiplexers[i].TryGetTarget(out var trackedMultiplexer)) active.Add(trackedMultiplexer);
                else s_multiplexers.RemoveAt(i);
            }
            return [.. active];
        }
    }

    internal readonly struct CaptureScope(long token, RespireConnectionMultiplexer? multiplexer) : IDisposable
    {
        internal long Token => token;

        public void Dispose()
        {
            foreach (var current in EndCapture(token, multiplexer)) current.PruneMaintenanceHandlerEpochs();
        }
    }
}

