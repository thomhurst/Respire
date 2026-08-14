using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Respire.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ServiceCollectionTests(RedisTestContainer fixture)
{
    [Test]
    public async Task OptionsBuilderCompatibilityAliases_AreAbsent()
    {
        var properties = typeof(RespireOptionsBuilder).GetProperties().Select(static property => property.Name);

        await Assert.That(properties).DoesNotContain("Cluster");
        await Assert.That(properties).DoesNotContain("ServiceName");
        await Assert.That(properties).DoesNotContain("ResponseTimeout");
    }

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
    public async Task ClientOptions_TakesPrecedenceOverConnectionString()
    {
        var services = new ServiceCollection();
        services.AddRespireDistributedCache(options =>
        {
            options.ConnectionString = "redis://127.0.0.1:1";
            options.ClientOptions = provider =>
            {
                _ = provider.GetRequiredService<FactoryMarker>();
                return RespireOptions.Parse(fixture.ConnectionString);
            };
        });
        services.AddSingleton<FactoryMarker>();

        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        await cache.SetAsync("factory-key", [4, 2], new DistributedCacheEntryOptions());
        await Assert.That(await cache.GetAsync("factory-key")).IsEquivalentTo((byte[])[4, 2]);
    }

    [Test]
    public async Task AddRespire_ActionBuilder_RegistersConfiguredClient()
    {
        var endpoint = RespireOptions.Parse(fixture.ConnectionString).Endpoints[0];
        var services = new ServiceCollection();
        services.AddRespire(options =>
        {
            options.Endpoints.Add(endpoint);
            options.Database = fixture.Database;
            options.ClientName = "respire-di-test";
        });

        await using var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<RespireClient>();
        var abstraction = provider.GetRequiredService<IRespireClient>();

        await Assert.That(abstraction).IsSameReferenceAs(concrete);
        await concrete.SetAsync("builder-key", "configured");
        await Assert.That(await concrete.GetStringAsync("builder-key")).IsEqualTo("configured");
    }

    [Test]
    public async Task OptionsBuilder_UseClientSideCaching_ConfiguresSharedClient()
    {
        var endpoint = RespireOptions.Parse(fixture.ConnectionString).Endpoints[0];
        var services = new ServiceCollection();
        services.AddRespire(options =>
        {
            options.Endpoints.Add(endpoint);
            options.Database = fixture.Database;
            options.UseClientSideCaching(new RespireClientSideCacheOptions { MaxEntries = 123 });
        });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RespireClient>();

        await Assert.That(client.ClientSideCache).IsNotNull();
        await Assert.That(client.Core.Options.ClientSideCache!.MaxEntries).IsEqualTo(123);
    }

    [Test]
    public async Task DistributedCache_ClientSideCache_ConfiguresOwnedClient()
    {
        var services = new ServiceCollection();
        services.AddRespireDistributedCache(options =>
        {
            options.ConnectionString = fixture.ConnectionString;
            options.ClientSideCache = new RespireClientSideCacheOptions { MaxEntries = 123 };
        });

        await using var provider = services.BuildServiceProvider();
        var cache = (RespireDistributedCache)provider.GetRequiredService<IDistributedCache>();

        await Assert.That(cache.OwnedClientOptions).IsNotNull();
        await Assert.That(cache.OwnedClientOptions!.ClientSideCache!.MaxEntries).IsEqualTo(123);
    }

    [Test]
    public async Task AddKeyedRespire_RegistersSeparateClients()
    {
        var services = new ServiceCollection();
        services.AddKeyedRespire("sessions", fixture.ConnectionString);
        services.AddKeyedRespire("jobs", options =>
            options.Endpoints.Add(RespireOptions.Parse(fixture.ConnectionString).Endpoints[0]));

        await using var provider = services.BuildServiceProvider();
        var sessions = provider.GetRequiredKeyedService<IRespireClient>("sessions");
        var jobs = provider.GetRequiredKeyedService<IRespireClient>("jobs");

        await Assert.That(sessions).IsNotSameReferenceAs(jobs);
    }

    [Test]
    public async Task DuplicateRespireRegistration_ThrowsImmediately()
    {
        var services = new ServiceCollection();
        services.AddRespire(fixture.ConnectionString);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRespire(fixture.ConnectionString));

        await Assert.That(exception.Message).Contains("already registered");
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

    private sealed class FactoryMarker;
}
