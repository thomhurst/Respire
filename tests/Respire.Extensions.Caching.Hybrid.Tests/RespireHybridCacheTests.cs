using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Respire.Extensions.Caching;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Hybrid.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel("redis-integration")]
public class RespireHybridCacheTests(RedisTestContainer fixture)
{
    private const string InstanceName = "hybrid:";

    private RespireClient _client = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        (await _client.ExecuteAsync("FLUSHDB")).Dispose();
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddRespireHybridCache(fixture.ConnectionString, InstanceName);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task AddRespireHybridCache_WiresRespireAsTheDistributedBackend()
    {
        await using var provider = BuildProvider();

        var hybridCache = provider.GetRequiredService<HybridCache>();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();

        await Assert.That(hybridCache).IsNotNull();
        await Assert.That(distributedCache is RespireDistributedCache).IsTrue();
    }

    [Test]
    public async Task GetOrCreateAsync_InvokesFactoryOnceAndCaches()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var factoryCalls = 0;

        var first = await cache.GetOrCreateAsync("counted", _ =>
        {
            factoryCalls++;
            return ValueTask.FromResult("expensive result");
        });
        var second = await cache.GetOrCreateAsync("counted", _ =>
        {
            factoryCalls++;
            return ValueTask.FromResult("should not run");
        });

        await Assert.That(first).IsEqualTo("expensive result");
        await Assert.That(second).IsEqualTo("expensive result");
        await Assert.That(factoryCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Value_SurvivesIntoRedis_AndSeedsAFreshProcess()
    {
        await using (var providerA = BuildProvider())
        {
            var cacheA = providerA.GetRequiredService<HybridCache>();
            await cacheA.GetOrCreateAsync("l2-backed", _ => ValueTask.FromResult("from provider A"));

            // HybridCache can publish the factory result to its caller before its best-effort
            // L2 write finishes. Keep the provider alive until that write reaches Redis.
            await Assert.That(await WaitForRedisKeyAsync(InstanceName + "l2-backed")).IsTrue();
        }

        // The entry landed in Redis under the configured instance prefix.
        await Assert.That(await _client.ExistsAsync(InstanceName + "l2-backed")).IsTrue();

        // A fresh provider has an empty local cache, so this must come from Redis — the factory
        // must not run.
        await using var providerB = BuildProvider();
        var cacheB = providerB.GetRequiredService<HybridCache>();
        var value = await cacheB.GetOrCreateAsync<string>(
            "l2-backed",
            _ => throw new InvalidOperationException("Factory ran — the value was not served from L2."));

        await Assert.That(value).IsEqualTo("from provider A");
    }

    private async Task<bool> WaitForRedisKeyAsync(string key)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await _client.ExistsAsync(key))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    [Test]
    public async Task RemoveAsync_EvictsLocalAndRedis()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        await cache.SetAsync("removable", "value");
        await Assert.That(await _client.ExistsAsync(InstanceName + "removable")).IsTrue();

        await cache.RemoveAsync("removable");

        await Assert.That(await _client.ExistsAsync(InstanceName + "removable")).IsFalse();
        var factoryRan = false;
        await cache.GetOrCreateAsync("removable", _ =>
        {
            factoryRan = true;
            return ValueTask.FromResult("recreated");
        });
        await Assert.That(factoryRan).IsTrue();
    }

    [Test]
    public async Task Expiration_IsHonoredInRedis()
    {
        await using var provider = BuildProvider();
        var cache = provider.GetRequiredService<HybridCache>();

        // Local cache disabled so the read below exercises the Redis entry, not L1.
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(1),
            Flags = HybridCacheEntryFlags.DisableLocalCache,
        };
        await cache.SetAsync("short-lived", "value", options);
        await Assert.That(await _client.ExistsAsync(InstanceName + "short-lived")).IsTrue();

        await Task.Delay(TimeSpan.FromSeconds(2));

        await Assert.That(await _client.ExistsAsync(InstanceName + "short-lived")).IsFalse();
    }

    [Test]
    public async Task SharedRespireClient_IsUsedWhenNoConnectionStringGiven()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(_ => RespireClient.Create(fixture.ConnectionString));
        services.AddRespireHybridCache(configureCache: options => options.InstanceName = InstanceName);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();

        await cache.SetAsync("via-shared-client", "value");

        await Assert.That(await _client.ExistsAsync(InstanceName + "via-shared-client")).IsTrue();
    }
}
