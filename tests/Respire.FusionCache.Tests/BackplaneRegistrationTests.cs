using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Respire.Caching;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BackplaneRegistrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BuilderSharesEachNodesClientWithL2AndProviderDisposalPreservesThatClient(int protocol)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        };
        await using var firstClient = await RespireClient.ConnectAsync(options);
        await using var secondClient = await RespireClient.ConnectAsync(options);
        var prefix = "fusion:" + Guid.NewGuid() + ":";
        await using var firstProvider = BuildProvider(firstClient, prefix);
        await using var secondProvider = BuildProvider(secondClient, prefix);
        var first = firstProvider.GetRequiredService<IFusionCache>();
        var second = secondProvider.GetRequiredService<IFusionCache>();
        await Assert.That(first.Backplane).IsTypeOf<RespireFusionCacheBackplane>();
        await Assert.That(first.DistributedCache).IsSameReferenceAs(firstProvider.GetRequiredService<IDistributedCache>());
        first.DefaultEntryOptions.AllowBackgroundDistributedCacheOperations = false;
        first.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
        second.DefaultEntryOptions.AllowBackgroundDistributedCacheOperations = false;
        second.DefaultEntryOptions.AllowBackgroundBackplaneOperations = false;
        await first.SetAsync("key", 42);
        await Assert.That(await second.GetOrDefaultAsync("key", -1)).IsEqualTo(42);
        await first.SetAsync("key", 43);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await second.GetOrDefaultAsync("key", -1, token: deadline.Token) != 43)
            await Task.Delay(10, deadline.Token);
        await firstProvider.DisposeAsync();
        await secondProvider.DisposeAsync();
        await firstClient.SetAsync(prefix + "still-alive", "yes");
        await Assert.That(await secondClient.GetStringAsync(prefix + "still-alive")).IsEqualTo("yes");
    }

    [Test]
    public async Task ServiceRegistrationProvidesIndependentTransientBackplanes()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        services.AddFusionCacheRespireBackplane();
        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IFusionCacheBackplane>();
        var second = provider.GetRequiredService<IFusionCacheBackplane>();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(first).IsTypeOf<RespireFusionCacheBackplane>();
        await provider.DisposeAsync();
        await client.SetAsync(Guid.NewGuid().ToString(), "still-alive");
    }

    private static ServiceProvider BuildProvider(IRespireClient client, string prefix)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddRespireDistributedCache(options => options.InstanceName = prefix);
        services.AddFusionCache()
            .WithOptions(options =>
            {
                options.BackplaneChannelPrefix = prefix;
                options.WaitForInitialBackplaneSubscribe = true;
            })
            .WithSerializer(new FusionCacheSystemTextJsonSerializer())
            .WithRegisteredDistributedCache()
            .WithRespireBackplane();
        return services.BuildServiceProvider();
    }
}
