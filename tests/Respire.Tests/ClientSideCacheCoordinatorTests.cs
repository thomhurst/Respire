using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ClientSideCacheCoordinatorTests
{
    [Test]
    public async Task Capacity_IsBoundedByEntryCount()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            MaxEntries = 2,
            MaxSizeBytes = 1_000_000,
            TimeToLive = null,
        });

        Insert(cache, "a", "1");
        Insert(cache, "b", "2");
        Insert(cache, "c", "3");

        await Assert.That(cache.Count).IsLessThanOrEqualTo(2);
        await Assert.That(cache.GetStatistics().Evictions).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task OversizedValue_IsReturnedButNotStored()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            MaxEntries = 10,
            MaxSizeBytes = 80,
            TimeToLive = null,
        });

        Insert(cache, "key", new string('x', 100));

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExpiredEntry_IsEvictedOnAccess()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions
        {
            TimeToLive = TimeSpan.FromMilliseconds(10),
        });
        Insert(cache, "key", "value");

        await Task.Delay(30);
        var key = new RespireKey("key");

        await Assert.That(cache.TryGet(in key, out _)).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BinaryKeys_DoNotCollide()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var first = new RespireKey(new byte[] { 0xFF, 0x00 });
        var second = new RespireKey(new byte[] { 0xFF, 0x01 });
        Insert(cache, first, "first");
        Insert(cache, second, "second");

        await Assert.That(Read(cache, first)).IsEqualTo("first");
        await Assert.That(Read(cache, second)).IsEqualTo("second");
    }

    [Test]
    public async Task ContinuityFlush_RejectsOlderInflightRead()
    {
        var cache = new ClientSideCacheCoordinator(new RespireClientSideCacheOptions());
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        cache.FlushForContinuityLoss();
        var response = RespValue.BulkString("stale"u8.ToArray());

        cache.CompleteRead(in token, in response, allowInsert: true);

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.GetStatistics().ContinuityFlushes).IsEqualTo(1);
    }

    private static void Insert(ClientSideCacheCoordinator cache, string key, string value)
        => Insert(cache, new RespireKey(key), value);

    private static void Insert(ClientSideCacheCoordinator cache, RespireKey key, string value)
    {
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(System.Text.Encoding.UTF8.GetBytes(value));
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    private static string Read(ClientSideCacheCoordinator cache, RespireKey key)
    {
        if (!cache.TryGet(in key, out var response))
        {
            throw new InvalidOperationException("Expected cached value.");
        }

        return response.AsString();
    }
}
