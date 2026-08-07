using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.PerClass)]
[NotInParallel("redis-integration")]
public class RespireDistributedCacheTests(RedisTestFixture fixture)
{
    private RespireClient? _client;
    private RespireDistributedCache? _cache;

    private RespireClient Client => _client!;
    private RespireDistributedCache Cache => _cache!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        (await Client.ExecuteAsync("FLUSHDB")).Dispose();
        _cache = new RespireDistributedCache(_client);
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        if (_cache is not null)
        {
            await Cache.DisposeAsync();
        }

        if (_client is not null)
        {
            await Client.DisposeAsync();
        }
    }

    private async Task<long> PttlAsync(string key)
    {
        using var result = await Client.ExecuteAsync("PTTL", key);
        return result.AsInteger();
    }

    [Test]
    public async Task SetAndGet_RoundTripsBytes()
    {
        var value = Encoding.UTF8.GetBytes("hello distributed cache");

        await Cache.SetAsync("roundtrip", value, new DistributedCacheEntryOptions());
        var fetched = await Cache.GetAsync("roundtrip");

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.SequenceEqual(value)).IsTrue();
    }

    [Test]
    public async Task Get_MissingKey_ReturnsNull()
    {
        var fetched = await Cache.GetAsync("does-not-exist");

        await Assert.That(fetched).IsNull();
    }

    [Test]
    public async Task Remove_DeletesEntry()
    {
        await Cache.SetAsync("removable", [1, 2, 3], new DistributedCacheEntryOptions());

        await Cache.RemoveAsync("removable");

        await Assert.That(await Cache.GetAsync("removable")).IsNull();
    }

    [Test]
    public async Task Set_WithoutExpiration_HasNoTtl()
    {
        await Cache.SetAsync("no-ttl", [42], new DistributedCacheEntryOptions());

        await Assert.That(await PttlAsync("no-ttl")).IsEqualTo(-1);
    }

    [Test]
    public async Task Overwrite_WithoutExpiration_ClearsPreviousTtl()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
        await Cache.SetAsync("clear-ttl", [1], options);
        await Assert.That(await PttlAsync("clear-ttl")).IsGreaterThan(0);

        await Cache.SetAsync("clear-ttl", [2], new DistributedCacheEntryOptions());

        await Assert.That(await PttlAsync("clear-ttl")).IsEqualTo(-1);
    }

    [Test]
    public async Task AbsoluteExpirationRelativeToNow_ExpiresEntry()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1) };
        await Cache.SetAsync("short-lived", [1], options);

        await Assert.That(await Cache.GetAsync("short-lived")).IsNotNull();

        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(await Cache.GetAsync("short-lived")).IsNull();
    }

    [Test]
    public async Task AbsoluteExpiration_InThePast_Throws()
    {
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.AddMinutes(-1) };

        var threw = false;
        try
        {
            await Cache.SetAsync("past", [1], options);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task SlidingExpiration_GetExtendsLife()
    {
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(2) };
        await Cache.SetAsync("sliding", [7], options);

        // Total elapsed exceeds the sliding window, but each read re-arms it.
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(await Cache.GetAsync("sliding")).IsNotNull();
        }

        // Untouched past the window, the entry dies.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Assert.That(await Cache.GetAsync("sliding")).IsNull();
    }

    [Test]
    public async Task Refresh_ExtendsSlidingEntry()
    {
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(2) };
        await Cache.SetAsync("refreshable", [9], options);

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await Cache.RefreshAsync("refreshable");
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        await Assert.That(await Cache.GetAsync("refreshable")).IsNotNull();
    }

    [Test]
    public async Task SlidingExpiration_AbsoluteCapWins()
    {
        var options = new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(2),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3),
        };
        await Cache.SetAsync("capped", [5], options);

        // Keep touching well within the sliding window; the absolute cap must still kill it.
        for (var i = 0; i < 7; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            await Cache.GetAsync("capped");
        }

        await Assert.That(await Cache.GetAsync("capped")).IsNull();
    }

    [Test]
    public async Task InstanceName_IsolatesCachesAndPrefixesKeys()
    {
        var cacheA = new RespireDistributedCache(Client, new RespireCacheOptions { InstanceName = "a:" });
        var cacheB = new RespireDistributedCache(Client, new RespireCacheOptions { InstanceName = "b:" });

        await cacheA.SetAsync("shared-key", [1], new DistributedCacheEntryOptions());

        await Assert.That(await cacheA.GetAsync("shared-key")).IsNotNull();
        await Assert.That(await cacheB.GetAsync("shared-key")).IsNull();
        await Assert.That(await Client.ExistsAsync("a:shared-key")).IsTrue();
    }

    [Test]
    public async Task SyncApis_RoundTrip()
    {
        var value = new byte[] { 10, 20, 30 };
        var options = new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(1) };

        await Task.Run(() =>
        {
            Cache.Set("sync-key", value, options);
            var fetched = Cache.Get("sync-key");
            if (fetched is null || !fetched.SequenceEqual(value))
            {
                throw new InvalidOperationException("Sync Get returned a different value than Set stored.");
            }

            Cache.Refresh("sync-key");
            Cache.Remove("sync-key");
        });

        await Assert.That(await Cache.GetAsync("sync-key")).IsNull();
    }

    [Test]
    public async Task NullKey_Throws()
    {
        var threw = false;
        try
        {
            await Cache.GetAsync(null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
