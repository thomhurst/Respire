using System.Reflection;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReceiveDeadlineTests
{
    [Test, NotInParallel]
    public async Task NonFinalReplyDoesNotWaitForTheWatchdogGate()
    {
        var state = new DeadlineState();
        state.Send(2);
        using var started = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var receiver = new Thread(() =>
        {
            started.Set();
            try { state.Receive(); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        bool completed;
        lock (state.Gate)
        {
            receiver.Start();
            if (!started.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Receiver did not start.");
            completed = finished.Wait(TimeSpan.FromSeconds(5));
        }
        if (!receiver.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Receiver did not finish.");
        await Assert.That(failure).IsNull();
        await Assert.That(completed).IsTrue();
        await Assert.That(state.Received).IsEqualTo(1L);
        await Assert.That(state.Timestamp).IsGreaterThan(0L);
    }

    [Test, NotInParallel]
    public async Task NewWriteRearmsBeforeFinalReplyCleanupAcquiresTheGate()
    {
        var state = new DeadlineState();
        state.Send(1);
        var oldTimestamp = state.Timestamp;
        using var started = new ManualResetEventSlim();
        Exception? failure = null;
        var receiver = new Thread(() =>
        {
            started.Set();
            try { state.Receive(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        bool counted;
        long newTimestamp;
        lock (state.Gate)
        {
            receiver.Start();
            if (!started.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Receiver did not start.");
            counted = SpinWait.SpinUntil(() => state.Received == 1, TimeSpan.FromSeconds(5));
            state.Send(1); // Reentrant gate: final-reply cleanup is still blocked.
            newTimestamp = state.Timestamp;
        }
        if (!receiver.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Receiver did not finish.");
        await Assert.That(failure).IsNull();
        await Assert.That(counted).IsTrue();
        await Assert.That(newTimestamp).IsGreaterThan(oldTimestamp);
        await Assert.That(state.Timestamp).IsEqualTo(newTimestamp);
        await Assert.That(state.Sent).IsEqualTo(2L);
        await Assert.That(state.Received).IsEqualTo(1L);
        state.Receive();
        await Assert.That(state.Timestamp).IsEqualTo(0L);
    }

    [Test]
    public async Task RepliesBeforeSendPublicationDoNotArmAnIdleConnection()
    {
        var state = new DeadlineState();
        state.Receive();
        state.Send(1);
        await Assert.That(state.Timestamp).IsEqualTo(0L);
        lock (state.Gate)
        {
            if (state.Claim(1)) throw new InvalidOperationException("An idle connection claimed a timeout.");
        }
        state.Send(1);
        await Assert.That(state.Timestamp).IsGreaterThan(0L);
        state.Receive();
        state.Restart();
        await Assert.That(state.Timestamp).IsEqualTo(0L);
    }

    [Test]
    public async Task StaleSampleCannotClaimAndCommittedClaimExcludesLaterProgress()
    {
        var state = new DeadlineState();
        state.Send(2);
        state.Receive();
        bool stale;
        bool current;
        lock (state.Gate)
        {
            stale = state.Claim(0);
            current = state.Claim(1);
        }
        await Assert.That(stale).IsFalse();
        await Assert.That(current).IsTrue();
        var timestamp = state.Timestamp;
        state.Receive();
        state.Send(1);
        state.Restart();
        await Assert.That(state.Received).IsEqualTo(-1L);
        await Assert.That(state.Sent).IsEqualTo(2L);
        await Assert.That(state.Timestamp).IsEqualTo(timestamp);
        lock (state.Gate)
        {
            if (state.Claim(-1)) throw new InvalidOperationException("A committed timeout was claimed twice.");
        }
    }

    [Test, NotInParallel]
    public async Task ConcurrentReplyAndWatchdogClaimHaveExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var state = new DeadlineState();
            state.Send(1);
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(2);
            var claimed = false;
            var receiver = new Thread(() => { ready.Signal(); start.Wait(); state.Receive(); }) { IsBackground = true };
            var watcher = new Thread(() =>
            {
                ready.Signal();
                start.Wait();
                lock (state.Gate) claimed = state.Claim(0);
            }) { IsBackground = true };
            receiver.Start();
            watcher.Start();
            try
            {
                if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Participants did not start.");
            }
            finally { start.Set(); }
            if (!receiver.Join(TimeSpan.FromSeconds(5)) || !watcher.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Participants did not finish.");
            await Assert.That(state.Received).IsEqualTo(claimed ? -1L : 1L);
            if (!claimed) await Assert.That(state.Timestamp).IsEqualTo(0L);
        }
    }

    // Reflect only the private protocol helper, not an operational owner's state.
    // Production invokes these same methods; no inspection member or clock hook is added.
    private sealed class DeadlineState
    {
        private static readonly Type StateType = typeof(RespireConnection).GetNestedType("ReceiveProgress", BindingFlags.NonPublic)!;
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object _state = Activator.CreateInstance(StateType, nonPublic: true)!;
        private readonly FieldInfo _sent = StateType.GetField("SentReplyCount", Members)!;
        private readonly FieldInfo _received = StateType.GetField("ReceivedReplyCount", Members)!;
        private readonly FieldInfo _timestamp = StateType.GetField("DeadlineTimestamp", Members)!;
        public Lock Gate { get; }
        public Action<int> Send { get; }
        public Action Receive { get; }
        public Action Restart { get; }
        public Func<long, bool> Claim { get; }
        public long Sent => (long)_sent.GetValue(_state)!;
        public long Received => (long)_received.GetValue(_state)!;
        public long Timestamp => (long)_timestamp.GetValue(_state)!;

        public DeadlineState()
        {
            Gate = (Lock)StateType.GetField("DeadlineGate", Members)!.GetValue(_state)!;
            Send = StateType.GetMethod("MarkRepliesSent", Members)!.CreateDelegate<Action<int>>(_state);
            Receive = StateType.GetMethod("MarkReplyReceived", Members)!.CreateDelegate<Action>(_state);
            Restart = StateType.GetMethod("RestartDeadline", Members)!.CreateDelegate<Action>(_state);
            Claim = StateType.GetMethod("TryClaimTimeout", Members)!.CreateDelegate<Func<long, bool>>(_state);
        }
    }
}
