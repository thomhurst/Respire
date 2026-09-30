using System.Diagnostics;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
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
