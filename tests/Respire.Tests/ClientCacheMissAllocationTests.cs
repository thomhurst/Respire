using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheMissAllocationTests
{
    [Test]
    [NotInParallel]
    public async Task RacingStandaloneMissProducerAllocatesNothingWhenAnotherReadAlreadyFilledTheCache()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        // Bind once outside the measurement. This is the actual miss producer's
        // second lookup, after another read wins publication; no wire send starts.
        var method = typeof(RespireClient).GetMethod("FetchGetAndCacheAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(RespValue));
        var producer = method.CreateDelegate<MissProducer>(client);
        for (var index = 0; index < 32; index++)
        {
            Measure(producer, cache, key, false);
            Measure(producer, cache, key, true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: Measure(producer, cache, key, false), Control: Measure(producer, cache, key, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
    }

    private delegate ValueTask<RespValue> MissProducer(RespireKey key, ClientSideCacheCoordinator cache,
        CancellationToken cancellationToken, ResponseConverter<RespireClient, RespValue> converter, bool transferResponse);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(MissProducer producer, ClientSideCacheCoordinator cache, RespireKey key, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var operation = producer(key, cache, default,
                static (RespireClient _, in RespValue value) => value, true);
            if (!operation.IsCompletedSuccessfully || !operation.Result.AsSpan().SequenceEqual("value"u8))
                throw new InvalidOperationException("Expected the racing producer to use the published cache entry.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }
}
