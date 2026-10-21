using System.Runtime.CompilerServices;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task SuccessfulCachedCallersAllocateNothingWithErrorsEnabled()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
#pragma warning disable RESP010 // Each telemetry scenario owns and disposes its client so connection and observation state cannot leak between tests.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
#pragma warning restore RESP010
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var read = cache.BeginRead(in key);
        using var response = RespValue.BulkString("42"u8.ToArray());
        cache.CompleteRead(in read, in response, allowInsert: true, decodedText: "42");
        using var capture = new Capture(throwOnMeasurement: true);
        _ = MeasureCachedCallers(client, allocate: false);
        _ = MeasureCachedCallers(client, allocate: true);
        var collectionsBefore = CollectionCounts();
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Success: MeasureCachedCallers(client, allocate: false),
            Control: MeasureCachedCallers(client, allocate: true)));
        var collectionsAfter = CollectionCounts();
        // A nonzero sample names its caller and first iteration. See docs/ALLOCATION_MEASUREMENT.md.
        var evidence = $"{measured.Success}; GC collections {collectionsBefore} -> {collectionsAfter}";
        await Assert.That(measured.Success.Total).IsEqualTo(0L).Because(evidence);
        await Assert.That(measured.Control.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    private static object? _cacheAllocationControl;

    private static (int Gen0, int Gen1, int Gen2) CollectionCounts()
        => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    // Reading the thread counter does not allocate, so the per-caller deltas sum to the whole interval.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CachedCallerBytes MeasureCachedCallers(RespireClient client, bool allocate)
    {
        long get = 0, getString = 0, getOrSetInt = 0, getOrSetString = 0, control = 0;
        var firstIteration = -1;
        var previous = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var start = previous;
#pragma warning disable RESP009 // This warmed synchronous allocation probe consumes an already-completed operation on the measuring thread.
            _ = client.GetAsync<int>("key").GetAwaiter().GetResult();
#pragma warning restore RESP009
            var current = GC.GetAllocatedBytesForCurrentThread();
            get += current - previous;
            previous = current;
#pragma warning disable RESP009 // This warmed synchronous allocation probe consumes an already-completed operation on the measuring thread.
            _ = client.GetStringAsync("key").GetAwaiter().GetResult();
#pragma warning restore RESP009
            current = GC.GetAllocatedBytesForCurrentThread();
            getString += current - previous;
            previous = current;
#pragma warning disable RESP009 // This warmed synchronous allocation probe consumes an already-completed operation on the measuring thread.
            _ = client.GetOrSetAsync<int>("key", static _ => throw new InvalidOperationException("Unexpected cache miss."),
                TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
#pragma warning restore RESP009
            current = GC.GetAllocatedBytesForCurrentThread();
            getOrSetInt += current - previous;
            previous = current;
#pragma warning disable RESP009 // This warmed synchronous allocation probe consumes an already-completed operation on the measuring thread.
            _ = client.GetOrSetAsync<string>("key", static _ => throw new InvalidOperationException("Unexpected cache miss."),
                TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
#pragma warning restore RESP009
            current = GC.GetAllocatedBytesForCurrentThread();
            getOrSetString += current - previous;
            previous = current;
            if (firstIteration < 0 && current != start) firstIteration = index;
            if (allocate) Volatile.Write(ref _cacheAllocationControl, new byte[37]);
            current = GC.GetAllocatedBytesForCurrentThread();
            control += current - previous;
            previous = current;
        }
        return new(get, getString, getOrSetInt, getOrSetString, control, firstIteration);
    }

    private readonly record struct CachedCallerBytes(
        long GetAsync, long GetStringAsync, long GetOrSetInt, long GetOrSetString, long Control, int FirstIteration)
    {
        public long Total => GetAsync + GetStringAsync + GetOrSetInt + GetOrSetString + Control;
    }
}
