using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterTopologyRefreshSchedulerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    [Test]
    [Arguments(null)]
    [Arguments(0L)]
    [Arguments(-10_000L)]
    public async Task DisabledIntervalArmsNoPeriodicDeadline(long? intervalTicks)
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(
            intervalTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null, clock);
        scheduler.Start();

        clock.Advance(TimeSpan.FromDays(365));
        var decision = scheduler.Next();

        await Assert.That(decision.Run).IsFalse();
        await Assert.That(decision.Wait).IsNull();
    }

    [Test]
    public async Task PeriodicDeadlineIsJitteredButNeverLaterThanInterval()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(Interval, clock);
        scheduler.Start();

        var wait = scheduler.Next().Wait!.Value;
        await Assert.That(wait).IsLessThanOrEqualTo(Interval);
        await Assert.That(wait).IsGreaterThanOrEqualTo(Interval - TimeSpan.FromSeconds(6));

        clock.Advance(Interval);
        var decision = scheduler.Next();
        await Assert.That(decision.Run).IsTrue();
        await Assert.That(decision.AllowRecentResult).IsFalse();
    }

    [Test]
    public async Task LongIntervalWaitsInTimerSafeSegments()
    {
        var scheduler = new ClusterTopologyRefreshScheduler(TimeSpan.FromDays(90), new SteppedClock());
        scheduler.Start();

        await Assert.That(scheduler.Next().Wait).IsEqualTo(ClusterTopologyRefreshScheduler.MaximumTimerSegment);
    }

    [Test]
    public async Task RepeatedMovedRequestsKeepTheFirstDebounceDeadline()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();

        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        clock.Advance(TimeSpan.FromSeconds(2));
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        clock.Advance(TimeSpan.FromSeconds(2));
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);

        await Assert.That(scheduler.Next().Wait).IsEqualTo(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        var decision = scheduler.Next();
        await Assert.That(decision.Run).IsTrue();
        await Assert.That(decision.AllowRecentResult).IsTrue();
    }

    [Test]
    public async Task PeriodicDeadlineInsideDebounceRunsFullRefresh()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(TimeSpan.FromMilliseconds(50), clock);
        scheduler.Start();
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);

        await Assert.That(scheduler.Next().Wait!.Value).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(50));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var decision = scheduler.Next();

        await Assert.That(decision.Run).IsTrue();
        await Assert.That(decision.AllowRecentResult).IsFalse();
        // The periodic refresh also satisfied the pending redirect.
        await Assert.That(scheduler.Next().Run).IsFalse();
        await Assert.That(scheduler.Next().Wait).IsNull();
    }

    [Test]
    public async Task ForcedRequestInterruptsDebounceAndDoesNotLeakIntoNextRequest()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        var wake = scheduler.Next().Wake;

        scheduler.Request(TimeSpan.Zero, force: true);
        await Assert.That(wake.IsCompleted).IsTrue();
        await Assert.That(scheduler.HasPendingForcedRequest).IsTrue();
        var forced = scheduler.Next();
        await Assert.That(forced.Run).IsTrue();
        await Assert.That(forced.AllowRecentResult).IsFalse();
        await Assert.That(scheduler.HasPendingForcedRequest).IsFalse();
        scheduler.Complete(forced, TopologyRefreshOutcome.Refreshed);

        // A wake left over from the forced request carries no state: the next redirect is debounced
        // and may reuse a recent success.
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        await Assert.That(scheduler.Next().Run).IsFalse();
        clock.Advance(ClusterTopologyRefreshScheduler.MovedDebounce);
        var next = scheduler.Next();
        await Assert.That(next.Run).IsTrue();
        await Assert.That(next.AllowRecentResult).IsTrue();
    }

    [Test]
    public async Task PrimaryDisconnectsAreSpacedAndTrailingRequestIsKept()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();

        scheduler.RequestPrimaryDisconnect();
        var first = scheduler.Next();
        await Assert.That(first.Run).IsTrue();
        scheduler.Complete(first, TopologyRefreshOutcome.Refreshed);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        scheduler.RequestPrimaryDisconnect();
        scheduler.RequestPrimaryDisconnect();
        var waiting = scheduler.Next();
        await Assert.That(waiting.Run).IsFalse();
        await Assert.That(waiting.Wait).IsEqualTo(TimeSpan.FromMilliseconds(800));

        clock.Advance(TimeSpan.FromMilliseconds(800));
        var trailing = scheduler.Next();
        await Assert.That(trailing.Run).IsTrue();
        await Assert.That(trailing.AllowRecentResult).IsFalse();
        await Assert.That(scheduler.Next().Wait).IsNull();
    }

    [Test]
    public async Task ForcedPrimaryDisconnectInterruptsMovedDebounceAfterSpacing()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.RequestPrimaryDisconnect();
        scheduler.Complete(scheduler.Next(), TopologyRefreshOutcome.Refreshed);

        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        scheduler.RequestPrimaryDisconnect();

        await Assert.That(scheduler.Next().Wait).IsEqualTo(ClusterTopologyRefreshScheduler.PrimaryDisconnectSpacing);
    }

    [Test]
    public async Task FailedRefreshRetriesWithCappedExponentialBackoff()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        var delays = new List<TimeSpan>();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var retry = scheduler.Complete(default, TopologyRefreshOutcome.Failed)!.Value;
            delays.Add(retry);
            await Assert.That(scheduler.Next().Wait).IsEqualTo(retry);
            clock.Advance(retry);
            var decision = scheduler.Next();
            await Assert.That(decision.Run).IsTrue();
            await Assert.That(decision.AllowRecentResult).IsFalse();
        }

        await Assert.That(delays.Select(static delay => (int)delay.TotalSeconds).ToArray())
            .IsEquivalentTo(new[] { 5, 10, 20, 40, 60, 60, 60, 60 });
        await Assert.That(scheduler.ConsecutiveFailures).IsEqualTo(8);

        await Assert.That(scheduler.Complete(default, TopologyRefreshOutcome.Refreshed)).IsNull();
        await Assert.That(scheduler.ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(scheduler.Next().Wait).IsNull();
    }

    [Test]
    public async Task FailureBackoffDelaysRedirectsButNotForcedRequests()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        for (var failure = 0; failure < 4; failure++) _ = scheduler.Complete(default, TopologyRefreshOutcome.Failed);

        scheduler.Request(TimeSpan.Zero);
        await Assert.That(scheduler.Next().Wait).IsEqualTo(TimeSpan.FromSeconds(40));

        scheduler.Request(TimeSpan.Zero, force: true);
        await Assert.That(scheduler.Next().Run).IsTrue();
    }

    [Test]
    public async Task WorkerSurvivesRefreshFailuresAndStopsOnCancellation()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = scheduler.RunAsync((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
            secondCall.TrySetResult();
            return Task.FromResult(TopologyRefreshOutcome.Refreshed);
        }, logger: null, stop.Token);

        scheduler.Request(TimeSpan.Zero, force: true);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (scheduler.ConsecutiveFailures == 0) await Task.Delay(5, timeout.Token);
        scheduler.Request(TimeSpan.Zero, force: true);
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await stop.CancelAsync();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(worker.IsCompletedSuccessfully).IsTrue();
        await Assert.That(scheduler.ConsecutiveFailures).IsEqualTo(0);
    }

    [Test]
    public async Task PeriodicDeadlineIsExactForAFixedJitterSample()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(Interval, clock, jitterSample: () => 0.5);
        scheduler.Start();

        var jitter = TimeSpan.FromTicks((long)(Interval.Ticks * ClusterTopologyRefreshScheduler.PeriodicJitterRatio * 0.5));
        await Assert.That(scheduler.Next().Wait).IsEqualTo(Interval - jitter);
    }

    [Test]
    public async Task IntervalNearMaxValueSaturatesInsteadOfOverflowing()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(TimeSpan.MaxValue, clock, jitterSample: () => 0);
        clock.Advance(TimeSpan.FromSeconds(1));

        scheduler.Start();
        await Assert.That(scheduler.Next().Wait).IsEqualTo(ClusterTopologyRefreshScheduler.MaximumTimerSegment);

        scheduler.Request(TimeSpan.Zero, force: true);
        var run = scheduler.Next();
        await Assert.That(run.Run).IsTrue();
        await Assert.That(scheduler.Complete(run, TopologyRefreshOutcome.Refreshed)).IsNull();
        await Assert.That(scheduler.Next().Wait).IsEqualTo(ClusterTopologyRefreshScheduler.MaximumTimerSegment);
        await Assert.That(ClusterTopologyRefreshScheduler.SaturatingAdd(TimeSpan.FromDays(1), TimeSpan.MaxValue))
            .IsEqualTo(TimeSpan.MaxValue);
    }

    [Test]
    public async Task FailureBackoffDelaysAShorterPeriodicInterval()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(TimeSpan.FromSeconds(1), clock, jitterSample: () => 0);
        scheduler.Start();
        clock.Advance(TimeSpan.FromSeconds(1));
        var periodic = scheduler.Next();
        await Assert.That(periodic.Run).IsTrue();

        var retry = scheduler.Complete(periodic, TopologyRefreshOutcome.Failed)!.Value;
        await Assert.That(retry).IsEqualTo(ClusterTopologyRefreshScheduler.InitialFailureRetryDelay);
        // The 1s periodic interval must not probe the failed cluster before the 5s retry.
        await Assert.That(scheduler.Next().Wait).IsEqualTo(retry);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(scheduler.Next().Run).IsFalse();

        clock.Advance(retry - TimeSpan.FromSeconds(1));
        var retried = scheduler.Next();
        await Assert.That(retried.Run).IsTrue();
        await Assert.That(scheduler.Complete(retried, TopologyRefreshOutcome.Refreshed)).IsNull();
        // Success re-arms the periodic timer.
        await Assert.That(scheduler.Next().Wait).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task RedirectQueuedDuringAFailedRefreshWaitsForTheRetry()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.Request(TimeSpan.Zero, force: true);
        var run = scheduler.Next();
        await Assert.That(run.Run).IsTrue();

        // A redirect arrives while the refresh is running, before any backoff exists, and its
        // debounce expires while a candidate stalls.
        scheduler.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
        clock.Advance(TimeSpan.FromSeconds(6));
        var retry = scheduler.Complete(run, TopologyRefreshOutcome.Failed)!.Value;

        var next = scheduler.Next();
        await Assert.That(next.Run).IsFalse();
        await Assert.That(next.Wait).IsEqualTo(retry);
    }

    [Test]
    public async Task EarlierPlainRequestDoesNotPullASpacedDisconnectForward()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.RequestPrimaryDisconnect();
        scheduler.Complete(scheduler.Next(), TopologyRefreshOutcome.Refreshed);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        scheduler.Request(TimeSpan.Zero);
        scheduler.RequestPrimaryDisconnect();

        // The plain request runs now, but it is not forced and may reuse the recent success.
        var plain = scheduler.Next();
        await Assert.That(plain.Run).IsTrue();
        await Assert.That(plain.AllowRecentResult).IsTrue();
        await Assert.That(scheduler.HasPendingForcedRequest).IsTrue();
        scheduler.Complete(plain, TopologyRefreshOutcome.ReusedRecent);

        // The disconnect still runs, forced, at the end of its spacing window.
        await Assert.That(scheduler.Next().Wait).IsEqualTo(TimeSpan.FromMilliseconds(900));
        clock.Advance(TimeSpan.FromMilliseconds(900));
        var forced = scheduler.Next();
        await Assert.That(forced.Run).IsTrue();
        await Assert.That(forced.AllowRecentResult).IsFalse();
        await Assert.That(scheduler.HasPendingForcedRequest).IsFalse();
    }

    [Test]
    public async Task RealQueryClearsAForcedRequestThatArrivedBeforeIt()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.RequestPrimaryDisconnect();
        scheduler.Complete(scheduler.Next(), TopologyRefreshOutcome.Refreshed);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        scheduler.RequestPrimaryDisconnect();
        scheduler.Request(TimeSpan.Zero);
        var plain = scheduler.Next();
        await Assert.That(plain.Run).IsTrue();
        scheduler.Complete(plain, TopologyRefreshOutcome.Refreshed);

        await Assert.That(scheduler.HasPendingForcedRequest).IsFalse();
        await Assert.That(scheduler.Next().Wait).IsNull();
    }

    [Test]
    public async Task ForcedRequestArrivingDuringARunIsNotClearedByIt()
    {
        var clock = new SteppedClock();
        var scheduler = new ClusterTopologyRefreshScheduler(null, clock);
        scheduler.Start();
        scheduler.Request(TimeSpan.Zero);
        var plain = scheduler.Next();
        await Assert.That(plain.Run).IsTrue();

        scheduler.Request(TimeSpan.Zero, force: true);
        scheduler.Complete(plain, TopologyRefreshOutcome.Refreshed);

        await Assert.That(scheduler.HasPendingForcedRequest).IsTrue();
        var forced = scheduler.Next();
        await Assert.That(forced.Run).IsTrue();
        await Assert.That(forced.AllowRecentResult).IsFalse();
    }

    [Test]
    [Arguments(60_000, 10, 10_000, 6_000, 6_000)]
    [Arguments(60_000, 100, 10_000, 600, 2_000)]
    [Arguments(60_000, 100, 500, 600, 500)]
    [Arguments(1_000, 3, 10_000, 333, 1_000)]
    [Arguments(0, 3, 10_000, 0, 0)]
    public async Task CandidateBudgetKeepsAUsableFloorWithinTheDeadline(
        int timeLeftMs, int candidatesLeft, int configuredMs, int evenShareMs, int timeoutMs)
    {
        var (evenShare, timeout) = ClusterRouter.GetTopologyCandidateBudget(
            TimeSpan.FromMilliseconds(timeLeftMs), candidatesLeft, TimeSpan.FromMilliseconds(configuredMs));

        await Assert.That((int)evenShare.TotalMilliseconds).IsEqualTo(evenShareMs);
        await Assert.That((int)timeout.TotalMilliseconds).IsEqualTo(timeoutMs);
    }

    private sealed class SteppedClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
