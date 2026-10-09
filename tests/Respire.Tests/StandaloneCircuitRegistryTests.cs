using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class StandaloneCircuitRegistryTests
{
    [Test]
    public async Task LiveMembershipSkipsRepeatedScansAndInvalidationTrimsAfterRelease()
    {
        var endpoints = Enumerable.Range(0, 32).Select(i => new RespireEndpoint("node", 9000 + i)).ToArray();
        var live = endpoints.ToHashSet();
        var checks = 0;
        var registry = new StandaloneCircuitRegistry(new(), isCurrentEndpoint: endpoint =>
        {
            checks++;
            return live.Contains(endpoint);
        });
        foreach (var endpoint in endpoints) registry.Acquire(endpoint, default).Dispose();
        checks = 0;
        for (var i = 0; i < 100; i++) registry.Acquire(endpoints[i % endpoints.Length], default).Dispose();
        await Assert.That(checks).IsEqualTo(0);
        var held = registry.Acquire(endpoints[0], default);
        live.Remove(endpoints[0]);
        registry.InvalidateMembership();
        registry.Acquire(endpoints[1], default).Dispose();
        await Assert.That(registry.CountForTests).IsEqualTo(32);
        held.Dispose();
        await Assert.That(registry.CountForTests).IsEqualTo(31);
        checks = 0;
        for (var i = 1; i < 32; i++) registry.Acquire(endpoints[i], default).Dispose();
        await Assert.That(checks).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task WarmHealthyAdmissionAllocatesNothingWithPositiveControl()
    {
        var endpoint = new RespireEndpoint("current");
        var registry = new StandaloneCircuitRegistry(new(), () => endpoint);
        Measure(registry, endpoint, false);
        Measure(registry, endpoint, true);
        var healthy = AllocationMeasurement.WithoutConcurrentGc(() => Measure(registry, endpoint, false));
        var positive = AllocationMeasurement.WithoutConcurrentGc(() => Measure(registry, endpoint, true));
        await Assert.That(healthy).IsEqualTo(0);
        await Assert.That(positive > 0).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(StandaloneCircuitRegistry registry, RespireEndpoint endpoint, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var admission = registry.Acquire(endpoint, default);
            admission.Success();
            admission.Dispose();
            if (allocate) GC.KeepAlive(new byte[128]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task HostnameCaseSharesOpenHistoryAndRetainsCurrentEndpoint()
    {
        var endpoint = new RespireEndpoint("CURRENT");
        var current = new RespireEndpoint("current");
        var registry = new StandaloneCircuitRegistry(new() { MinimumFailureCount = 1 }, () => current);
        var failed = registry.Acquire(endpoint, default);
        failed.Failed(new RespireConnectionException("failed"), default);
        failed.Dispose();
        var original = registry.GetForTests(endpoint);
        await Assert.That(() => registry.Acquire(current, default)).Throws<RespireCircuitOpenException>();
        for (var i = 0; i < 64; i++) registry.Acquire(new("history", 1000 + i), default).Dispose();
        await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
        await Assert.That(ReferenceEquals(registry.GetForTests(current), original)).IsTrue();
        await Assert.That(() => registry.Acquire(current, default)).Throws<RespireCircuitOpenException>();
    }

    [Test]
    public async Task UnixSocketPathCaseRetainsDistinctCircuitHistories()
    {
        var registry = new StandaloneCircuitRegistry(new() { MinimumFailureCount = 1 });
        var endpoint = RespireEndpoint.UnixSocket("/tmp/Redis.sock");
        var failed = registry.Acquire(endpoint, default);
        failed.Failed(new RespireConnectionException("failed"), default);
        failed.Dispose();
        registry.Acquire(RespireEndpoint.UnixSocket("/tmp/redis.sock"), default).Dispose();
        await Assert.That(registry.CountForTests).IsEqualTo(2);
        await Assert.That(() => registry.Acquire(endpoint, default)).Throws<RespireCircuitOpenException>();
    }

    [Test]
    public async Task IdleHistoryIsBoundedAndCurrentEndpointKeepsItsOpenState()
    {
        var current = new RespireEndpoint("current");
        var registry = new StandaloneCircuitRegistry(new() { MinimumFailureCount = 1 }, () => current);
        var failed = registry.Acquire(current, default);
        failed.Failed(new RespireConnectionException("failed"), default);
        failed.Dispose();
        var original = registry.GetForTests(current);
        for (var i = 0; i < 64; i++)
        {
            var admission = registry.Acquire(new("history", 1000 + i), default);
            admission.Success();
            admission.Dispose();
        }
        await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
        await Assert.That(ReferenceEquals(registry.GetForTests(current), original)).IsTrue();
        await Assert.That(() => registry.Acquire(current, default)).Throws<RespireCircuitOpenException>();
        var revisited = registry.Acquire(new("history", 1000), default);
        revisited.Dispose();
        await Assert.That(registry.GetForTests(new("history", 1000)).Snapshot().SampleCount).IsEqualTo(0);
    }

    [Test]
    public async Task OutstandingHalfOpenPermitPreservesCircuitThroughEvictionAndReturn()
    {
        var endpoint = new RespireEndpoint("old");
        var registry = new StandaloneCircuitRegistry(new() { MinimumFailureCount = 1, HalfOpenProbeCount = 2 });
        var failed = registry.Acquire(endpoint, default);
        failed.Failed(new RespireConnectionException("failed"), default);
        failed.Dispose();
        var original = registry.GetForTests(endpoint);
        CircuitClock(original) = new FutureClock();
        var probe = registry.Acquire(endpoint, default);
        for (var i = 0; i < 64; i++) registry.Acquire(new("history", 1000 + i), default).Dispose();
        var returned = registry.Acquire(endpoint, default);
        await Assert.That(ReferenceEquals(registry.GetForTests(endpoint), original)).IsTrue();
        await Assert.That(original.Snapshot().ActiveProbes).IsEqualTo(2);
        probe.Success();
        probe.Dispose();
        await Assert.That(original.Snapshot().SuccessfulProbes).IsEqualTo(1);
        returned.Success();
        returned.Dispose();
        await Assert.That(original.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(original.Snapshot().ActiveProbes).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentEndpointAdmissionAndCompletionCannotEvictOutstandingPermits()
    {
        var registry = new StandaloneCircuitRegistry(new());
        var permits = new CircuitAdmission[64];
        var circuits = new EndpointCircuitBreaker[64];
        Parallel.For(0, permits.Length, i =>
        {
            var endpoint = new RespireEndpoint("active", 1000 + i);
            permits[i] = registry.Acquire(endpoint, default);
            circuits[i] = registry.GetForTests(endpoint);
        });
        await Assert.That(registry.CountForTests).IsEqualTo(permits.Length);
        // All entries exceed the idle cap but remain owned until their completions.
        Parallel.For(0, permits.Length, i =>
        {
            if (!ReferenceEquals(registry.GetForTests(new("active", 1000 + i)), circuits[i]))
                throw new InvalidOperationException("An outstanding circuit was replaced.");
            permits[i].Success();
            permits[i].Dispose();
            permits[i].Dispose();
        });
        await Assert.That(registry.CountForTests).IsEqualTo(StandaloneCircuitRegistry.RetainedEndpointLimit);
        foreach (var circuit in circuits)
            await Assert.That(circuit.Snapshot().SampleCount).IsEqualTo(1);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);

    private sealed class FutureClock : TimeProvider
    {
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp()
            => TimeProvider.System.GetTimestamp() + TimestampFrequency * 60;
    }
}
