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
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var read = cache.BeginRead(in key);
        using var response = RespValue.BulkString("42"u8.ToArray());
        cache.CompleteRead(in read, in response, allowInsert: true, decodedText: "42");
        using var capture = new Capture(throwOnMeasurement: true);
        _ = MeasureCachedCallers(client, allocate: false);
        _ = MeasureCachedCallers(client, allocate: true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Success: MeasureCachedCallers(client, allocate: false),
            Control: MeasureCachedCallers(client, allocate: true)));
        await Assert.That(measured.Success).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items).IsEmpty();
    }

    private static object? _cacheAllocationControl;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCachedCallers(RespireClient client, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            _ = client.GetAsync<int>("key").GetAwaiter().GetResult();
            _ = client.GetStringAsync("key").GetAwaiter().GetResult();
            _ = client.GetOrSetAsync<int>("key", static _ => throw new InvalidOperationException("Unexpected cache miss."),
                TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            if (allocate) Volatile.Write(ref _cacheAllocationControl, new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
