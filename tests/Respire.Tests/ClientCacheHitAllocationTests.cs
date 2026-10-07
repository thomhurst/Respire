using System.Runtime.CompilerServices;
using System.Text;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheHitAllocationTests
{
    [Test]
    [NotInParallel]
    [Arguments("ascii-value")]
    [Arguments("é😀value")]
    public async Task WarmPublicStringHitsAllocateNothing(string text)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(Encoding.UTF8.GetBytes(text));
        cache.CompleteRead(in token, in response, allowInsert: true);
        for (var index = 0; index < 32; index++)
        {
            Measure(client, false);
            Measure(client, true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: Measure(client, false), Control: Measure(client, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
        await Assert.That(cache.GetStatistics().Misses).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireClient client, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var operation = client.GetStringAsync("key");
            if (!operation.IsCompletedSuccessfully) throw new InvalidOperationException("Expected a synchronous cache hit.");
            GC.KeepAlive(operation.Result);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
