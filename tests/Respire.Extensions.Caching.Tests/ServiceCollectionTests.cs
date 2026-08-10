using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ServiceCollectionTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ConnectionString_RegistersWorkingBufferCache()
    {
        var services = new ServiceCollection();
        services.AddRespireDistributedCache(fixture.ConnectionString, instanceName: "di:");

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await Assert.That(cache is IBufferDistributedCache).IsTrue();

        await cache.SetAsync("di-key", [1, 2, 3], new DistributedCacheEntryOptions());
        var fetched = await cache.GetAsync("di-key");
        await Assert.That(fetched).IsNotNull();
    }

    [Test]
    public async Task RegisteredRespireClient_IsReused()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(_ => RespireClient.Create(fixture.ConnectionString));
        services.AddRespireDistributedCache(options => options.InstanceName = "shared:");

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var client = provider.GetRequiredService<IRespireClient>();

        await cache.SetAsync("reuse-key", [7], new DistributedCacheEntryOptions());

        // Written through the shared client, under the configured instance prefix.
        await Assert.That(await client.ExistsAsync("shared:reuse-key")).IsTrue();
    }

    [Test]
    public async Task AddRespireDistributedCache_OverridesEarlierBackendRegistration()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddRespireDistributedCache(fixture.ConnectionString);

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await Assert.That(cache is RespireDistributedCache).IsTrue();
    }

    [Test]
    public async Task SynchronousProviderDispose_Works()
    {
        var services = new ServiceCollection();
        services.AddRespireDistributedCache(fixture.ConnectionString, instanceName: "sync-dispose:");

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        await cache.SetAsync("key", [1], new DistributedCacheEntryOptions());

        // The cache owns its client here; a synchronous container teardown must not throw.
        provider.Dispose();
    }

    [Test]
    public async Task NoClientAndNoConnectionString_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddRespireDistributedCache(_ => { });

        await using var provider = services.BuildServiceProvider();

        var threw = false;
        try
        {
            provider.GetRequiredService<IDistributedCache>();
        }
        catch (RespireConfigurationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }
}
