using Respire.Internal;
using System.Runtime.CompilerServices;

namespace Respire.Tests;

public sealed class EndpointCircuitBreakerTests
{
    [Test]
    public async Task FailureCountAndRateBothGateOpening()
    {
        var breaker = new EndpointCircuitBreaker(new("redis.test"), new()
        {
            MinimumFailureCount = 2, FailureRateThreshold = 0.5
        }, new Clock());
        Finish(breaker, CircuitOutcome.Success);
        Finish(breaker, CircuitOutcome.Success);
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.TryAcquire(out _, out var remaining)).IsFalse();
        await Assert.That(remaining > TimeSpan.Zero).IsTrue();
    }

    [Test]
    public async Task RateBelowThresholdDoesNotOpenDespiteEnoughFailures()
    {
        var breaker = New(new Clock(), new() { MinimumFailureCount = 2, FailureRateThreshold = 0.75 });
        Finish(breaker, CircuitOutcome.Success);
        Finish(breaker, CircuitOutcome.Success);
        Finish(breaker, CircuitOutcome.Failure);
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 4, 2, 0, 0));
    }

    [Test]
    public async Task CountLimitEvictsOldestOutcomesAndKeepsExactRate()
    {
        var breaker = New(new Clock(), new() { MaximumSampleCount = 4, MinimumFailureCount = 2 });
        for (var i = 0; i < 10000; i++) Finish(breaker, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().SampleCount).IsEqualTo(4);
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot().FailureCount).IsEqualTo(1);
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    public async Task EvictedFailureIsRemovedFromNumerator()
    {
        var breaker = New(new Clock(), new() { MaximumSampleCount = 3, MinimumFailureCount = 2, FailureRateThreshold = 1 });
        Finish(breaker, CircuitOutcome.Failure);
        for (var i = 0; i < 3; i++) Finish(breaker, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 3, 0, 0, 0));
    }

    [Test]
    public async Task OldFailuresExpireExactlyAtWindowBoundary()
    {
        var clock = new Clock();
        var breaker = New(clock, new() { MinimumFailureCount = 2, SamplingWindow = TimeSpan.FromSeconds(10) });
        Finish(breaker, CircuitOutcome.Failure);
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Finish(breaker, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().FailureCount).IsEqualTo(1);
        clock.Advance(TimeSpan.FromTicks(1));
        Finish(breaker, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 2, 1, 0, 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(breaker.Snapshot().SampleCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredSuccessesOpenCircuitBeforeAnotherAdmission(bool inspectBeforeAdmission)
    {
        var clock = new Clock();
        var breaker = New(clock, new()
        {
            MinimumFailureCount = 2, FailureRateThreshold = 0.75, SamplingWindow = TimeSpan.FromSeconds(10)
        });
        for (var i = 0; i < 3; i++) Finish(breaker, CircuitOutcome.Success);
        clock.Advance(TimeSpan.FromSeconds(5));
        Finish(breaker, CircuitOutcome.Failure);
        Finish(breaker, CircuitOutcome.Failure);
        var pending = Acquire(breaker);
        clock.Advance(TimeSpan.FromSeconds(5));
        if (inspectBeforeAdmission) await Assert.That(breaker.Snapshot().SampleCount).IsEqualTo(2);
        await Assert.That(breaker.TryAcquire(out _, out var remaining)).IsFalse();
        await Assert.That(remaining).IsEqualTo(TimeSpan.FromSeconds(5));
        breaker.Complete(ref pending, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    public async Task ExpiryDoesNotOpenBelowMinimumOrWithoutRemainingFailures()
    {
        var clock = new Clock();
        var breaker = New(clock, new()
        {
            MinimumFailureCount = 2, FailureRateThreshold = 0.75, SamplingWindow = TimeSpan.FromSeconds(10)
        });
        for (var i = 0; i < 3; i++) Finish(breaker, CircuitOutcome.Success);
        clock.Advance(TimeSpan.FromSeconds(5));
        Finish(breaker, CircuitOutcome.Failure);
        clock.Advance(TimeSpan.FromSeconds(5));
        var belowMinimum = Acquire(breaker);
        await Assert.That(breaker.Snapshot().FailureCount).IsEqualTo(1);
        breaker.Complete(ref belowMinimum, CircuitOutcome.Ignored);
        clock.Advance(TimeSpan.FromSeconds(5));
        var emptyHistory = Acquire(breaker);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 0, 0, 0, 0));
        breaker.Complete(ref emptyHistory, CircuitOutcome.Ignored);
    }

    [Test]
    public async Task OpenDelayUsesMonotonicTimeDespiteWallClockChanges()
    {
        var clock = new Clock();
        var breaker = Open(clock);
        clock.WallClock = DateTimeOffset.MaxValue;
        await Assert.That(breaker.TryAcquire(out _, out var remaining)).IsFalse();
        await Assert.That(remaining).IsEqualTo(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        clock.WallClock = DateTimeOffset.MinValue;
        await Assert.That(breaker.TryAcquire(out _, out remaining)).IsFalse();
        await Assert.That(remaining).IsEqualTo(TimeSpan.FromTicks(1));
        clock.Advance(TimeSpan.FromTicks(1));
        await Assert.That(breaker.TryAcquire(out var probe, out _)).IsTrue();
        breaker.Complete(ref probe, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task AllRecoveryProbesMustSucceedAndAdmissionIsBounded()
    {
        var clock = new Clock();
        var breaker = Open(clock, 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        var a = Acquire(breaker);
        var b = Acquire(breaker);
        var c = Acquire(breaker);
        await Assert.That(breaker.TryAcquire(out _, out var remaining)).IsFalse();
        await Assert.That(remaining).IsNull();
        breaker.Complete(ref a, CircuitOutcome.Success);
        breaker.Complete(ref b, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.HalfOpen, 0, 0, 1, 2));
        await Assert.That(breaker.TryAcquire(out _, out _)).IsFalse();
        breaker.Complete(ref c, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 0, 0, 0, 0));
    }

    [Test]
    public async Task FailedProbeReopensAndOldProbeCannotCloseNextGeneration()
    {
        var clock = new Clock();
        var breaker = Open(clock, 2);
        clock.Advance(TimeSpan.FromSeconds(5));
        var failed = Acquire(breaker);
        var stale = Acquire(breaker);
        breaker.Complete(ref failed, CircuitOutcome.Failure);
        await Assert.That(breaker.TryAcquire(out _, out var delay)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var current = Acquire(breaker);
        breaker.Complete(ref stale, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().SuccessfulProbes).IsEqualTo(0);
        breaker.Complete(ref current, CircuitOutcome.Success);
        var last = Acquire(breaker);
        breaker.Complete(ref last, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task IgnoredProbeReleasesCapacityWithoutCountingSuccess()
    {
        var clock = new Clock();
        var breaker = Open(clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        var canceled = Acquire(breaker);
        breaker.Complete(ref canceled, CircuitOutcome.Ignored);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.HalfOpen, 0, 0, 0, 0));
        var replacement = Acquire(breaker);
        breaker.Complete(ref replacement, CircuitOutcome.Success);
        Finish(breaker, CircuitOutcome.Ignored);
        await Assert.That(breaker.Snapshot().SampleCount).IsEqualTo(0);
    }

    [Test]
    public async Task LateClosedCompletionCannotReopenRecoveredGeneration()
    {
        var clock = new Clock();
        var breaker = New(clock, new() { MinimumFailureCount = 1 });
        var stale = Acquire(breaker);
        Finish(breaker, CircuitOutcome.Failure);
        clock.Advance(TimeSpan.FromSeconds(5));
        Finish(breaker, CircuitOutcome.Success);
        breaker.Complete(ref stale, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Closed, 0, 0, 0, 0));
    }

    [Test]
    public async Task ConsumedPermitAndCopiedProbeCannotCompleteTwice()
    {
        var clock = new Clock();
        var breaker = Open(clock, 2);
        clock.Advance(TimeSpan.FromSeconds(5));
        var probe = Acquire(breaker);
        var copy = probe;
        breaker.Complete(ref probe, CircuitOutcome.Success);
        breaker.Complete(ref probe, CircuitOutcome.Failure);
        breaker.Complete(ref copy, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot().SuccessfulProbes).IsEqualTo(1);
        var last = Acquire(breaker);
        breaker.Complete(ref last, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task CanceledProbeCopyCannotCompleteReusedSlot()
    {
        var clock = new Clock();
        var breaker = Open(clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        var canceled = Acquire(breaker);
        var staleCopy = canceled;
        breaker.Complete(ref canceled, CircuitOutcome.Ignored);
        var replacement = Acquire(breaker);
        breaker.Complete(ref staleCopy, CircuitOutcome.Failure);
        await Assert.That(breaker.Snapshot().ActiveProbes).IsEqualTo(1);
        breaker.Complete(ref replacement, CircuitOutcome.Success);
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task EndpointsHaveIndependentHistoryAndAdmissions()
    {
        var clock = new Clock();
        var failed = Open(clock);
        var healthy = new EndpointCircuitBreaker(new("other.test"), new(), clock);
        Finish(healthy, CircuitOutcome.Success);
        await Assert.That(failed.TryAcquire(out _, out _)).IsFalse();
        await Assert.That(healthy.TryAcquire(out var permit, out _)).IsTrue();
        await Assert.That(() => failed.Complete(ref permit, CircuitOutcome.Success)).ThrowsExactly<ArgumentException>();
        healthy.Complete(ref permit, CircuitOutcome.Success);
        await Assert.That(healthy.Snapshot().SampleCount).IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentProbeAdmissionsNeverExceedLimit()
    {
        var clock = new Clock();
        var breaker = Open(clock, 4);
        clock.Advance(TimeSpan.FromSeconds(5));
        var permits = new CircuitPermit[128];
        var admitted = 0;
        Parallel.For(0, permits.Length, i =>
        {
            if (breaker.TryAcquire(out permits[i], out _)) Interlocked.Increment(ref admitted);
        });
        await Assert.That(admitted).IsEqualTo(4);
        await Assert.That(breaker.Snapshot().ActiveProbes).IsEqualTo(4);
        Parallel.For(0, permits.Length, i => breaker.Complete(ref permits[i], CircuitOutcome.Success));
        await Assert.That(breaker.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task ConcurrentOldGenerationFailuresCannotPolluteOpenHistory()
    {
        var breaker = New(new Clock(), new() { MinimumFailureCount = 2, FailureRateThreshold = 1 });
        var permits = Enumerable.Range(0, 128).Select(_ => Acquire(breaker)).ToArray();
        Parallel.For(0, permits.Length, i => breaker.Complete(ref permits[i], CircuitOutcome.Failure));
        await Assert.That(breaker.Snapshot()).IsEqualTo(new CircuitSnapshot(EndpointCircuitState.Open, 2, 2, 0, 0));
    }

    [Test]
    public async Task MaximumDurationDoesNotOverflowRemainingDelay()
    {
        var clock = new Clock();
        var breaker = New(clock, new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.MaxValue });
        Finish(breaker, CircuitOutcome.Failure);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(breaker.TryAcquire(out _, out var delay)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.MaxValue - TimeSpan.FromSeconds(1));
    }

    [Test]
    [Arguments("rate-zero")]
    [Arguments("rate-high")]
    [Arguments("rate-nan")]
    [Arguments("rate-infinity")]
    [Arguments("minimum-zero")]
    [Arguments("minimum-over-capacity")]
    [Arguments("capacity-zero")]
    [Arguments("capacity-high")]
    [Arguments("window-zero")]
    [Arguments("window-negative")]
    [Arguments("duration-zero")]
    [Arguments("duration-negative")]
    [Arguments("probes-zero")]
    [Arguments("probes-high")]
    public async Task InvalidOptionsAreRejected(string invalid)
    {
        var options = invalid switch
        {
            "rate-zero" => new RespireCircuitBreakerOptions { FailureRateThreshold = 0 },
            "rate-high" => new() { FailureRateThreshold = 1.01 },
            "rate-nan" => new() { FailureRateThreshold = double.NaN },
            "rate-infinity" => new() { FailureRateThreshold = double.PositiveInfinity },
            "minimum-zero" => new() { MinimumFailureCount = 0 },
            "minimum-over-capacity" => new() { MinimumFailureCount = 2, MaximumSampleCount = 1 },
            "capacity-zero" => new() { MaximumSampleCount = 0 },
            "capacity-high" => new() { MaximumSampleCount = 65537 },
            "window-zero" => new() { SamplingWindow = TimeSpan.Zero },
            "window-negative" => new() { SamplingWindow = TimeSpan.FromTicks(-1) },
            "duration-zero" => new() { OpenDuration = TimeSpan.Zero },
            "duration-negative" => new() { OpenDuration = TimeSpan.FromTicks(-1) },
            "probes-zero" => new() { HalfOpenProbeCount = 0 },
            _ => new() { HalfOpenProbeCount = 65537 },
        };
        await Assert.That(() => New(new Clock(), options)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RejectionPreservesEndpointDelayAndUnsentEvidence()
    {
        var endpoint = new RespireEndpoint("redis.test", 6380);
        var error = new RespireCircuitOpenException(endpoint, TimeSpan.FromSeconds(3));
        await Assert.That(error.Endpoint).IsEqualTo(endpoint);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(error.Message).Contains("redis.test:6380");
        await Assert.That(error.IsCommandNotSubmitted).IsTrue();
        await Assert.That(new RespireCircuitOpenException(endpoint, null).RetryAfter).IsNull();
        await Assert.That(() => new RespireCircuitOpenException(endpoint, TimeSpan.FromTicks(-1)))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test, NotInParallel]
    public async Task WarmHealthyStateUsesNoPerOutcomeAllocationWithPositiveControl()
    {
        var breaker = New(new Clock());
        Measure(breaker, false);
        Measure(breaker, true);
        var healthy = AllocationMeasurement.WithoutConcurrentGc(() => Measure(breaker, false));
        var positive = AllocationMeasurement.WithoutConcurrentGc(() => Measure(breaker, true));
        await Assert.That(healthy).IsEqualTo(0);
        await Assert.That(positive > 0).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(EndpointCircuitBreaker breaker, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            Finish(breaker, CircuitOutcome.Success);
            if (allocate) GC.KeepAlive(new byte[128]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static EndpointCircuitBreaker New(Clock clock, RespireCircuitBreakerOptions? options = null)
        => new(new("redis.test"), options ?? new(), clock);

    private static EndpointCircuitBreaker Open(Clock clock, int probes = 1)
    {
        var breaker = New(clock, new() { MinimumFailureCount = 1, HalfOpenProbeCount = probes });
        Finish(breaker, CircuitOutcome.Failure);
        return breaker;
    }

    private static CircuitPermit Acquire(EndpointCircuitBreaker breaker)
        => breaker.TryAcquire(out var permit, out _) ? permit : throw new InvalidOperationException("Expected admission.");

    private static void Finish(EndpointCircuitBreaker breaker, CircuitOutcome outcome)
    {
        if (!breaker.TryAcquire(out var permit, out _)) throw new InvalidOperationException("Expected admission.");
        breaker.Complete(ref permit, outcome);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset WallClock { get; set; } = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override DateTimeOffset GetUtcNow() => WallClock;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
