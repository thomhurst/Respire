using System.Diagnostics;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    /// <summary>Captures scalar retained-generation observations without command-path counters or I/O.</summary>
    /// <remarks>Takes <c>_nodesGate</c> before any pool's <c>_gate</c>; completion continuations are
    /// asynchronous. Membership is stable during capture, but transport counters change independently
    /// and are not a completion barrier. Completed generations are not retained. The snapshot holds no
    /// generation, connection, pool, router, or exception. No retention-age threshold may abandon an
    /// owed fence or establish correction ordering, and capture leaves retry backoff, ownership, and
    /// disposal semantics unchanged.</remarks>
    internal RespireClusterRetirementSnapshot CaptureRetirementSnapshot()
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Wall-clock capture metadata is independent of monotonic retirement ages.
            var capturedAt = DateTimeOffset.UtcNow;
            var now = Stopwatch.GetTimestamp();
            var oldest = TimeSpan.Zero;
            var undrained = 0;
            var awaitingFence = 0;
            var failed = 0;
            long fences = 0, borrowed = 0, connecting = 0;
            foreach (var retirement in _retiringNodes.Values)
            {
                var age = Stopwatch.GetElapsedTime(retirement.StartedAt, now);
                if (age > oldest) oldest = age;
                var node = retirement.Node;
                var pendingFences = node.PendingCorrectionFenceCount;
                fences += pendingFences;
                if (!node.RetirementDrained) undrained++;
                else if (pendingFences != 0) awaitingFence++;
                var cleanupFailed = retirement.CleanupFailed;
                if (retirement.DedicatedPool is { } pool)
                {
                    var state = pool.CaptureRetirementState();
                    borrowed += state.Borrowed;
                    connecting += state.Connecting;
                    cleanupFailed |= state.CleanupFailed;
                }
                if (cleanupFailed) failed++;
            }
            return new RespireClusterRetirementSnapshot
            {
                CapturedAt = capturedAt,
                RetiringGenerationCount = _retiringNodes.Count,
                OldestRetirementAge = oldest,
                UndrainedGenerationCount = undrained,
                AwaitingFenceGenerationCount = awaitingFence,
                PendingCorrectionFenceCount = fences,
                CleanupFailedGenerationCount = failed,
                BorrowedDedicatedConnectionCount = borrowed,
                ConnectingDedicatedConnectionCount = connecting,
            };
        }
    }
}
