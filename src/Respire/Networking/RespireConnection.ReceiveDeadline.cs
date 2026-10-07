using System.Diagnostics;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private sealed partial class ReceiveProgress
    {
        private const long TimeoutClaimed = -1;

        internal void MarkReplyReceived()
        {
            lock (DeadlineGate)
            {
                if (ReceivedReplyCount == TimeoutClaimed) return;
                ReceivedReplyCount++;
                if (ReceivedReplyCount >= SentReplyCount) DeadlineTimestamp = 0;
            }
        }

        internal void MarkRepliesSent(int count)
        {
            lock (DeadlineGate)
            {
                if (ReceivedReplyCount == TimeoutClaimed) return;
                SentReplyCount += count;
                if (SentReplyCount > ReceivedReplyCount && DeadlineTimestamp == 0)
                    DeadlineTimestamp = Stopwatch.GetTimestamp();
            }
        }

        internal void RestartDeadline()
        {
            lock (DeadlineGate)
            {
                if (ReceivedReplyCount != TimeoutClaimed && SentReplyCount > ReceivedReplyCount)
                    DeadlineTimestamp = Stopwatch.GetTimestamp();
            }
        }

        // The watchdog holds DeadlineGate across its time/maintenance/suppression
        // checks and this claim. A completed claim excludes subsequent replies.
        internal bool TryClaimTimeout(long expectedReceived)
        {
            if (expectedReceived == TimeoutClaimed || SentReplyCount <= expectedReceived)
                return false;
            return Interlocked.CompareExchange(ref ReceivedReplyCount, TimeoutClaimed, expectedReceived) == expectedReceived;
        }
    }
}
