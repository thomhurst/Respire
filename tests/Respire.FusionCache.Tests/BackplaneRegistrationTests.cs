using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            Protocol = protocol == 2 ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
            Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
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
        {
            await Task.Delay(10, deadline.Token);
        }

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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProviderDisposalPreventsQueuedBackplaneSubscription(bool disposeAsync)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logger = new SubscriptionGateLogger(deadline.Token);
        string name = "fusion:" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        services.AddFusionCache(name)
            .AsKeyedServiceByCacheName()
            .WithOptions(options => options.EnableBestPracticesAdvisor = false)
            .WithLogger(logger)
            .WithRespireBackplane();
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredKeyedService<IFusionCache>(name);

        try
        {
            await logger.SubscribeEntered.Task.WaitAsync(deadline.Token);

            if (disposeAsync)
            {
                await provider.DisposeAsync();
            }
            else
            {
                provider.Dispose();
            }

            await logger.Unsubscribed.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            logger.ReleaseSubscribe.TrySetResult();
        }

        string channel = await logger.SubscribeFinished.Task.WaitAsync(deadline.Token);
        var counts = await client.Server.PubSubSubscriberCountsAsync([channel], cancellationToken: deadline.Token);
        await Assert.That(counts.Single().Subscribers).IsEqualTo(0L);
        await client.SetAsync(name, "still-alive", cancellationToken: deadline.Token);
        await Assert.That(await client.GetStringAsync(name, deadline.Token)).IsEqualTo("still-alive");
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

    private sealed class SubscriptionGateLogger(CancellationToken deadline) : ILogger<ZiggyCreatures.Caching.Fusion.FusionCache>
    {
        internal TaskCompletionSource SubscribeEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseSubscribe
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Unsubscribed
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<string> SubscribeFinished
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);

            if (message.Contains("before subscribing to backplane on channel", StringComparison.Ordinal))
            {
                SubscribeEntered.TrySetResult();
                ReleaseSubscribe.Task.WaitAsync(deadline).GetAwaiter().GetResult();
            }
            else if (message.Contains("after unsubscribing to backplane", StringComparison.Ordinal))
            {
                Unsubscribed.TrySetResult();
            }
            else if (state is IReadOnlyList<KeyValuePair<string, object?>> fields
                && (message.Contains("after subscribing to backplane on channel", StringComparison.Ordinal)
                    || (exception is ObjectDisposedException && fields.Any(field => field is { Key: "RetriesLeft", Value: 0 }))))
            {
                SubscribeFinished.TrySetResult((string)fields.Single(field => field.Key == "BackplaneChannel").Value!);
            }
        }
    }
}
