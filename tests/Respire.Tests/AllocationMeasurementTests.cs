using System.Runtime.CompilerServices;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class AllocationMeasurementTests
{
    [Test]
    public async Task LostRegionReportsCollectionsAndProcessAllocations()
    {
        var failure = CaptureFailure(() => AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            return 0;
        }));

        await Assert.That(failure.Message).Contains("no-GC region was not retained");
        await Assert.That(failure.Message).Contains("GC counts=1/1/1");
        await Assert.That(failure.Message).Contains("process bytes=");
        await Assert.That(failure.Message).Contains("thread bytes=");
        await Assert.That(failure.Message).Contains("mode=NoGCRegion->");
        await Assert.That(failure.Message).Contains("elapsed ms=");
        await Assert.That(failure.InnerException is InvalidOperationException).IsTrue();
    }

    [Test]
    public async Task LostRegionPreservesMeasurementException()
    {
        var expected = new InvalidOperationException("Measurement failed.");
        var failure = CaptureFailure(() => AllocationMeasurement.WithoutConcurrentGc<int>(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            throw expected;
        }));

        await Assert.That(failure).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task DiagnosticSnapshotsPreserveExactMeasurementAndPositiveControl()
    {
        _ = Measure(false);
        _ = Measure(true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (Measure(false), Measure(true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThanOrEqualTo(37L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        if (allocate) GC.KeepAlive(new byte[37]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static InvalidOperationException CaptureFailure(Action action)
    {
        try { action(); }
        catch (InvalidOperationException failure) { return failure; }
        throw new InvalidOperationException("Expected the allocation measurement to fail.");
    }
}
