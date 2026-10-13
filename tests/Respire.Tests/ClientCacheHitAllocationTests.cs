using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientCacheHitAllocationTests
{
    [Test]
    public async Task DecodedStringsCountAgainstTheLimitAndInvalidationRemovesTheirSize()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new() { MaxSizeBytes = 200 },
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(new byte[100]);
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That((await client.GetStringAsync("key"))!.Length).IsEqualTo(100);
        await Assert.That(cache.SizeBytes).IsLessThanOrEqualTo(200);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);

        response = RespValue.BulkString("é😀"u8.ToArray());
        token = cache.BeginRead(in key);
        cache.CompleteRead(in token, in response, allowInsert: true);
        var binarySize = cache.SizeBytes;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("é😀");
        var decodedSize = cache.SizeBytes;
        await Assert.That(decodedSize).IsGreaterThan(binarySize);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("é😀");
        await Assert.That(cache.SizeBytes).IsEqualTo(decodedSize);
        cache.Invalidate(in key);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentHitsAndMissesKeepExactTotalsAndOneDecodedEntry()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var missing = new RespireKey("missing");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("é😀"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                if (!cache.TryGetString(in key, out var value) || value != "é😀")
                    throw new InvalidOperationException("Lost a cached value.");
                if (cache.TryGet(in missing, out _)) throw new InvalidOperationException("Unexpected cache hit.");
            }
        })));
        var statistics = cache.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(32_000);
        await Assert.That(statistics.Misses).IsEqualTo(32_000);
        await Assert.That(cache.Count).IsEqualTo(1);
        cache.Invalidate(in key);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StringMemoizationPreservesNullBinaryCopiesAndInvalidation(bool isNull)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = isNull ? RespValue.Null : RespValue.BulkString(new byte[] { 0xff, 0, 65 });
        cache.CompleteRead(in token, in response, allowInsert: true);
        var first = await client.GetStringAsync("key");
        var second = await client.GetStringAsync("key");
        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(first).IsEqualTo(isNull ? null : "�\0A");
        if (!isNull)
        {
            var bytes = (await client.GetBytesAsync("key"))!;
            bytes[0] = 1;
            await Assert.That((await client.GetBytesAsync("key"))![0]).IsEqualTo((byte)0xff);
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("�\0A");
        }
        cache.Invalidate(in key);
        token = cache.BeginRead(in key);
        response = RespValue.BulkString("new"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

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

    [Test]
    [NotInParallel]
    [Arguments("ascii-value")]
    [Arguments("é😀value")]
    public async Task WarmPublicHashStringHitsAllocateNothing(string text)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        PublishHashField(cache, RespValue.BulkString(Encoding.UTF8.GetBytes(text)));
        for (var index = 0; index < 32; index++)
        {
            MeasureHash(client, false);
            MeasureHash(client, true);
        }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: MeasureHash(client, false), Control: MeasureHash(client, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000);
        await Assert.That(await client.Hashes.GetStringAsync("hash", "field")).IsEqualTo(text);
        await Assert.That(cache.GetStatistics().Misses).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HashStringHitsCountOnceMemoizeTextAndReleaseItsSize(bool isNull)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        PublishHashField(cache, isNull ? RespValue.Null : RespValue.BulkString("é😀"u8.ToArray()));
        var binarySize = cache.SizeBytes;
        var first = await client.Hashes.GetStringAsync("hash", "field");
        var second = await client.Hashes.GetStringAsync("hash", "field");
        await Assert.That(first).IsEqualTo(isNull ? null : "é😀");
        await Assert.That(second).IsSameReferenceAs(first);
        // A decoded value counts against the limit once; a null reply has no text to retain.
        if (isNull) await Assert.That(cache.SizeBytes).IsEqualTo(binarySize);
        else await Assert.That(cache.SizeBytes).IsGreaterThan(binarySize);
        var statistics = cache.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(2);
        await Assert.That(statistics.Misses).IsEqualTo(0);
        cache.Invalidate(new RespireKey("hash"));
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    [Test]
    public async Task HitOnlyProbesLeaveMissesToTheCountedRoute()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("missing");
        var request = HashFieldRequest();
        await Assert.That(cache.TryGetHit(in key, out _)).IsFalse();
        await Assert.That(cache.TryGetStringHit(in key, out _)).IsFalse();
        await Assert.That(cache.TryGetHit(in request, out _)).IsFalse();
        await Assert.That(cache.TryGetStringHit(in request, out _)).IsFalse();
        var statistics = cache.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(0);
        await Assert.That(statistics.Misses).IsEqualTo(0);
    }

    [Test]
    public async Task WarmConvertedHitsCountOnceAndCompleteSynchronously()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        var key = new RespireKey("key");
        var request = new ClientSideCacheCoordinator.QueryRequest(new ClientCacheCommandKey("EXISTS", key.AsValue()), key);
        var token = cache.BeginRead("EXISTS", in request);
        var response = RespValue.Integer(1);
        cache.CompleteRead(in token, in response, allowInsert: true);
        var operation = client.ExistsAsync("key");
        await Assert.That(operation.IsCompletedSuccessfully).IsTrue();
        await Assert.That(await operation).IsTrue();
        var statistics = cache.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(1);
        await Assert.That(statistics.Misses).IsEqualTo(0);
    }

    [Test]
    public async Task CachedCommandQueryHitsStillRejectADisposedClient()
    {
        var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        PublishHashField(cache, RespValue.BulkString("value"u8.ToArray()));
        await Assert.That(await client.Hashes.GetStringAsync("hash", "field")).IsEqualTo("value");
        await client.DisposeAsync();
        await Assert.That(async () => await client.Hashes.GetStringAsync("hash", "field"))
            .Throws<ObjectDisposedException>();
    }

    private static ClientSideCacheCoordinator.QueryRequest HashFieldRequest()
    {
        var key = new RespireKey("hash");
        return new(new ClientCacheCommandKey("HGET", key.AsValue(), "field"), key);
    }

    private static void PublishHashField(ClientSideCacheCoordinator cache, RespValue response)
    {
        var request = HashFieldRequest();
        var token = cache.BeginRead("HGET", in request);
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureHash(RespireClient client, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var operation = client.Hashes.GetStringAsync("hash", "field");
            if (!operation.IsCompletedSuccessfully) throw new InvalidOperationException("Expected a synchronous cache hit.");
            GC.KeepAlive(operation.Result);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
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
