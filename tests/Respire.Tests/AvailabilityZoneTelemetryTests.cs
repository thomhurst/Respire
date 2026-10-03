using System.Diagnostics.Metrics;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class AvailabilityZoneTelemetryTests
{
    [Test]
    public async Task OversizedNamesUseOverflowWithoutConsumingTheZoneBudget()
    {
        var counters = new AvailabilityZoneTelemetry.Registry();
        var oversized = counters.ForZone(new string('x', 129));
        for (var index = 0; index < 100; index++)
        {
            var counter = counters.ForZone(new string('x', 129) + index);
            await Assert.That(counter).IsSameReferenceAs(oversized);
            counter.Increment();
        }

        var boundary = new string('x', 128);
        counters.ForZone(boundary).Increment();
        for (var index = 1; index < 64; index++) counters.ForZone($"zone-{index}").Increment();
        await Assert.That(counters.ForZone("one-more-zone")).IsSameReferenceAs(oversized);
        var observed = counters.Observe().ToArray();
        await Assert.That(observed.Length).IsEqualTo(66);
        await Assert.That(observed.Count(value => Tag(value, "respire.availability_zone.status") == "known")).IsEqualTo(64);
        await Assert.That(observed.Single(value => Tag(value, "server.availability_zone") == boundary).Value).IsEqualTo(1L);
        var overflow = observed.Single(value => Tag(value, "respire.availability_zone.status") == "overflow");
        await Assert.That(overflow.Value).IsEqualTo(100L);
        await Assert.That(Tag(overflow, "server.availability_zone")).IsNull();
    }

    [Test]
    public async Task BudgetPreservesExistingCountersAndDistinguishesMissingMetadata()
    {
        var counters = new AvailabilityZoneTelemetry.Registry();
        var first = counters.ForZone("unknown");
        first.Increment();
        for (var index = 1; index < 64; index++) counters.ForZone($"zone-{index}");
        var overflow = counters.ForZone("overflow-a");
        await Assert.That(counters.ForZone("overflow-b")).IsSameReferenceAs(overflow);
        await Assert.That(counters.ForZone("unknown")).IsSameReferenceAs(first);
        first.Increment();
        overflow.Increment();
        counters.ForZone(null).Increment();
        var observed = counters.Observe().ToArray();
        await Assert.That(observed.Single(value => Tag(value, "server.availability_zone") == "unknown").Value).IsEqualTo(2L);
        var missing = observed.Single(value => Tag(value, "respire.availability_zone.status") == "unknown");
        await Assert.That(missing.Value).IsEqualTo(1L);
        await Assert.That(Tag(missing, "server.availability_zone")).IsNull();
        await Assert.That(observed.Single(value => Tag(value, "respire.availability_zone.status") == "overflow").Value).IsEqualTo(1L);
    }

    private static string? Tag(Measurement<long> measurement, string name)
    {
        foreach (var tag in measurement.Tags)
            if (tag.Key == name) return tag.Value as string;
        return null;
    }
}
