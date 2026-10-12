using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Respire.Internal;

namespace Respire.Tests;

[NotInParallel]
public sealed class CircuitTelemetryTests
{
    internal const string Transitions = "respire.circuit.transitions";
    internal const string Rejections = "respire.circuit.rejections";

    [Test]
    [Arguments(6379)]
    [Arguments(6380)]
    public async Task ReportsEveryTransitionAndRejectedAdmissionOnce(int port)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("circuit.test", port);
        var registry = new StandaloneCircuitRegistry(Options());
        var clock = new Clock();
        var first = registry.Acquire(endpoint, default);
        CircuitClock(registry.GetForTests(endpoint)) = clock;
        first.Failed(new RespireConnectionException("secret payload"), default);
        first.Dispose();
        Reject(registry, endpoint);
        Reject(registry, endpoint);
        clock.Advance();
        var probe = registry.Acquire(endpoint, default);
        Reject(registry, endpoint);
        probe.Success();
        probe.Dispose();
        probe.Dispose();
        var failed = registry.Acquire(endpoint, default);
        failed.Failed(new RespireConnectionException("secret key"), default);
        failed.Dispose();
        clock.Advance();
        probe = registry.Acquire(endpoint, default);
        probe.Failed(new RespireConnectionException("secret credentials"), default);
        probe.Dispose();
        Reject(registry, endpoint);

        var transitions = measurements.Events.Where(e => e.Name == Transitions).ToArray();
        await Assert.That(transitions.Select(e => (e.Tags["respire.circuit.state.from"], e.Tags["respire.circuit.state.to"])))
            .IsEquivalentTo(new (object?, object?)[]
            {
                ("closed", "open"), ("open", "half_open"), ("half_open", "closed"),
                ("closed", "open"), ("open", "half_open"), ("half_open", "open"),
            }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(measurements.Events.Count(e => e.Name == Rejections)).IsEqualTo(4);
        foreach (var measurement in measurements.Events)
        {
            await Assert.That(measurement.Value).IsEqualTo(1L);
            await Assert.That(measurement.Tags["server.address"]).IsEqualTo(endpoint.Host);
            await Assert.That(measurement.Tags.ContainsKey("server.port")).IsEqualTo(port != 6379);
            if (port != 6379) await Assert.That(measurement.Tags["server.port"]).IsEqualTo(port);
            await Assert.That(measurement.Tags["db.system.name"]).IsEqualTo("redis");
            var expected = measurement.Name == Transitions ? 5 : 3;
            await Assert.That(measurement.Tags.Count).IsEqualTo(expected + (port != 6379 ? 1 : 0));
            await Assert.That(measurement.Tags.Values.Any(v => v?.ToString()?.Contains("secret", StringComparison.Ordinal) == true)).IsFalse();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredSuccessHistoryReportsOpeningDuringAdmission(bool suspended)
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("expiry.test");
        var registry = new StandaloneCircuitRegistry(Options() with
        {
            MinimumFailureCount = 2, FailureRateThreshold = 0.75, SamplingWindow = TimeSpan.FromSeconds(10),
        });
        var clock = new Clock();
        var admission = registry.Acquire(endpoint, default);
        CircuitClock(registry.GetForTests(endpoint)) = clock;
        admission.Success();
        admission.Dispose();
        Finish(registry, endpoint, false);
        clock.Advance();
        Finish(registry, endpoint, true);
        Finish(registry, endpoint, true);
        clock.Advance();
        // Simulate suspension after the opening timestamp is captured. Admission may
        // open and enter recovery in one call; neither transition can be lost.
        clock.AdvanceAfterNextTimestamp = suspended;
        if (suspended)
        {
            var probe = registry.Acquire(endpoint, default);
            await Assert.That(measurements.Events.Count(e => e.Name == Transitions)).IsEqualTo(2);
            probe.Success();
            probe.Dispose();
        }
        else Reject(registry, endpoint);
        await Assert.That(measurements.Events.Count(e => e.Name == Transitions)).IsEqualTo(suspended ? 3 : 1);
        await Assert.That(measurements.Events.Count(e => e.Name == Rejections)).IsEqualTo(suspended ? 0 : 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsSelectionAndInstrumentSubscriptionBothGateDelivery(bool subscribed)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = subscribed ? RespireMetricGroups.Command : RespireMetricGroups.Resiliency,
        });
        using var measurements = new Measurements(subscribed);
        var registry = new StandaloneCircuitRegistry(Options());
        var endpoint = new RespireEndpoint("disabled.test");
        Finish(registry, endpoint, true);
        Reject(registry, endpoint);
        await Assert.That(measurements.Events.Count).IsEqualTo(0);
        await Assert.That(registry.GetForTests(endpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    public async Task FailoverProbeCoreKeepsItsExistingMetricsOnly()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var clock = new Clock();
        var breaker = new EndpointCircuitBreaker(new("probe.test"), Options(), clock);
        breaker.TryAcquire(out var permit, out _);
        breaker.Complete(ref permit, CircuitOutcome.Failure);
        breaker.TryAcquire(out _, out _);
        clock.Advance();
        breaker.TryAcquire(out permit, out _);
        breaker.Complete(ref permit, CircuitOutcome.Success);
        await Assert.That(measurements.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingListenersCannotChangeAdmissionOrCompletion()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements(throwOnMeasurement: true);
        var registry = new StandaloneCircuitRegistry(Options());
        var endpoint = new RespireEndpoint("throwing.test");
        Finish(registry, endpoint, true);
        await Assert.That(() => registry.Acquire(endpoint, default)).ThrowsExactly<RespireCircuitOpenException>();
        await Assert.That(registry.GetForTests(endpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    public async Task EvictedHistoryStartsFreshWithoutRetainedInstrumentState()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var registry = new StandaloneCircuitRegistry(Options());
        var endpoint = new RespireEndpoint("evicted.test");
        Finish(registry, endpoint, true);
        var old = registry.GetForTests(endpoint);
        for (var i = 0; i < 32; i++) Finish(registry, new("history.test", 9000 + i), false);
        await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
        Finish(registry, endpoint, false);
        await Assert.That(registry.GetForTests(endpoint)).IsNotSameReferenceAs(old);
        await Assert.That(registry.GetForTests(endpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(measurements.Events.Count).IsEqualTo(1);
        Finish(registry, endpoint, true);
        await Assert.That(measurements.Events.Count(e => e.Name == Transitions)).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task HealthyAdmissionAllocatesNothingWithPositiveControl(bool selected, bool subscribed)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = selected ? RespireMetricGroups.Resiliency : RespireMetricGroups.None,
        });
        using var measurements = new Measurements(subscribed);
        var endpoint = new RespireEndpoint("healthy.test", 6380);
        var registry = new StandaloneCircuitRegistry(Options());
        MeasureHealthy(registry, endpoint, false);
        MeasureHealthy(registry, endpoint, true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureHealthy(registry, endpoint, false), Control: MeasureHealthy(registry, endpoint, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control >= 37_000L).IsTrue();
        await Assert.That(measurements.Events.Count).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureHealthy(StandaloneCircuitRegistry registry, RespireEndpoint endpoint, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var admission = registry.Acquire(endpoint, default);
            admission.Success();
            admission.Dispose();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ConcurrentHalfOpenAdmissionsCountOnlyRejectedAttempts()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("concurrent.test");
        var registry = new StandaloneCircuitRegistry(Options() with { HalfOpenProbeCount = 4 });
        var clock = new Clock();
        var first = registry.Acquire(endpoint, default);
        CircuitClock(registry.GetForTests(endpoint)) = clock;
        first.Failed(new RespireConnectionException("failed"), default);
        first.Dispose();
        clock.Advance();
        var admissions = new CircuitAdmission[128];
        Parallel.For(0, admissions.Length, i =>
        {
            try { admissions[i] = registry.Acquire(endpoint, default); }
            catch (RespireCircuitOpenException) { }
        });
        await Assert.That(measurements.Events.Count(e => e.Name == Rejections)).IsEqualTo(124);
        Parallel.For(0, admissions.Length, i =>
        {
            admissions[i].Success();
            admissions[i].Dispose();
        });
        await Assert.That(measurements.Events.Count(e => e.Name == Transitions)).IsEqualTo(3);
        await Assert.That(registry.GetForTests(endpoint).Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
    }

    [Test]
    public async Task CancellationBeforeAdmissionDoesNotReportRejection()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("cancelled.test");
        var registry = new StandaloneCircuitRegistry(Options());
        Finish(registry, endpoint, true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => registry.Acquire(endpoint, cancellation.Token)).ThrowsExactly<OperationCanceledException>();
        await Assert.That(measurements.Events.Count(e => e.Name == Rejections)).IsEqualTo(0);
    }

    [Test]
    public async Task ReentrantListenerCannotEvictAnAdmissionBeforeItsPermitReturns()
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("retained.test");
        var registry = new StandaloneCircuitRegistry(Options());
        var clock = new Clock();
        var first = registry.Acquire(endpoint, default);
        var original = registry.GetForTests(endpoint);
        CircuitClock(original) = clock;
        first.Failed(new RespireConnectionException("failed"), default);
        first.Dispose();
        clock.Advance();
        measurements.OnMeasurement = () =>
        {
            for (var i = 0; i < 32; i++) Finish(registry, new("listener.test", 9000 + i), false);
        };
        var probe = registry.Acquire(endpoint, default);
        try
        {
            await Assert.That(registry.GetForTests(endpoint)).IsSameReferenceAs(original);
            await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
            await Assert.That(original.Snapshot().ActiveProbes).IsEqualTo(1);
        }
        finally { probe.Dispose(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AdmissionListenersCanWaitForRegistryWorkOnAnotherThread(bool rejected)
    {
        using var configuration = new MetricConfigurationScope();
        using var measurements = new Measurements();
        var endpoint = new RespireEndpoint("listener-worker.test");
        var registry = new StandaloneCircuitRegistry(Options());
        var clock = new Clock();
        var first = registry.Acquire(endpoint, default);
        var original = registry.GetForTests(endpoint);
        CircuitClock(original) = clock;
        first.Failed(new RespireConnectionException("failed"), default);
        first.Dispose();
        if (!rejected) clock.Advance();

        Task? worker = null;
        var completedDuringCallback = false;
        measurements.OnMeasurement = () =>
        {
            measurements.OnMeasurement = null;
            worker = Task.Factory.StartNew(() =>
            {
                Reject(registry, endpoint);
                for (var i = 0; i < 32; i++) Finish(registry, new("worker.test", 9000 + i), false);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            completedDuringCallback = worker.Wait(TimeSpan.FromSeconds(5));
        };

        var probe = default(CircuitAdmission);
        try
        {
            if (rejected)
                await Assert.That(() => registry.Acquire(endpoint, default)).ThrowsExactly<RespireCircuitOpenException>();
            else probe = registry.Acquire(endpoint, default);
            await worker!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await Assert.That(completedDuringCallback).IsTrue();
            await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
            if (!rejected)
            {
                await Assert.That(registry.GetForTests(endpoint)).IsSameReferenceAs(original);
                await Assert.That(original.Snapshot().ActiveProbes).IsEqualTo(1);
            }
        }
        finally { probe.Dispose(); }
    }

    private static RespireCircuitBreakerOptions Options() => new()
    {
        MinimumFailureCount = 1, OpenDuration = TimeSpan.FromSeconds(5), HalfOpenProbeCount = 1,
    };

    private static void Finish(StandaloneCircuitRegistry registry, RespireEndpoint endpoint, bool fail)
    {
        var admission = registry.Acquire(endpoint, default);
        if (fail) admission.Failed(new RespireConnectionException("failed"), default);
        else admission.Success();
        admission.Dispose();
    }

    private static void Reject(StandaloneCircuitRegistry registry, RespireEndpoint endpoint)
    {
        try { registry.Acquire(endpoint, default).Dispose(); }
        catch (RespireCircuitOpenException) { return; }
        throw new InvalidOperationException("Expected circuit rejection.");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        internal bool AdvanceAfterNextTimestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var timestamp = _ticks;
            if (AdvanceAfterNextTimestamp)
            {
                Advance();
                AdvanceAfterNextTimestamp = false;
            }
            return timestamp;
        }
        internal void Advance() => _ticks += TimeSpan.FromSeconds(5).Ticks;
    }

    internal sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal List<(string Name, long Value, Dictionary<string, object?> Tags)> Events { get; } = [];
        internal Action? OnMeasurement { get; set; }

        internal Measurements(bool subscribed = true, bool throwOnMeasurement = false)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (subscribed && instrument.Meter.Name == "Respire" && instrument.Name is Transitions or Rejections)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (throwOnMeasurement) throw new InvalidOperationException("Listener failure.");
                lock (Events) Events.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
                OnMeasurement?.Invoke();
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
