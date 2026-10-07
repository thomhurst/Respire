using System.Diagnostics;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private sealed partial class ReceiveProgress
    {
        private const long TimeoutClaimed = -1;

        internal void MarkReplyReceived()
        {
            // A timeout claims this same counter before closing. Replies racing
            // that claim either advance first (invalidating the sample) or see
            // the terminal sentinel. Other pending replies need no gate entry.
            long received;
            while (true)
            {
                var previous = Volatile.Read(ref ReceivedReplyCount);
                if (previous == TimeoutClaimed) return;
                received = previous + 1;
                if (Interlocked.CompareExchange(ref ReceivedReplyCount, received, previous) == previous)
                    break;
            }
            if (received < Volatile.Read(ref SentReplyCount)) return;

            lock (DeadlineGate)
            {
                // A writer can start the next pending interval before this idle
                // cleanup takes the gate; never clear that newly armed deadline.
                if (Volatile.Read(ref ReceivedReplyCount) >= SentReplyCount)
                    DeadlineTimestamp = 0;
            }
        }

        internal void MarkRepliesSent(int count)
        {
            lock (DeadlineGate)
            {
                var received = Volatile.Read(ref ReceivedReplyCount);
                if (received == TimeoutClaimed) return;
                var wasIdle = SentReplyCount <= received;
                var sent = SentReplyCount + count;
                Volatile.Write(ref SentReplyCount, sent);
                // Final-reply accounting is visible before its gated cleanup.
                // Rearm a new interval even if the prior timestamp remains set.
                if (sent > received && (wasIdle || DeadlineTimestamp == 0))
                    DeadlineTimestamp = Stopwatch.GetTimestamp();
            }
        }

        internal void RestartDeadline()
        {
            lock (DeadlineGate)
            {
                var received = Volatile.Read(ref ReceivedReplyCount);
                if (received != TimeoutClaimed && SentReplyCount > received)
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
