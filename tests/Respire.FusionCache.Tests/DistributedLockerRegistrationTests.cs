using Microsoft.Extensions.DependencyInjection;
using Respire.Caching;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Locking.Distributed;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DistributedLockerRegistrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    public async Task BackgroundFactoryRetainsLeaseAccordingToRegisteredPolicy(int protocol, bool releaseOnCancellation)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = (RespProtocol)protocol, Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        var builder = services.AddFusionCache(Guid.NewGuid().ToString()).WithRespireDistributedLocker(
            new() { ReleaseOnCallerCancellation = releaseOnCancellation, LeaseDuration = TimeSpan.FromSeconds(2) });
        await using var provider = services.BuildServiceProvider();
        var locker = (RespireFusionCacheDistributedLocker)builder.DistributedLockerFactory!(provider);
        using var cache = new ZiggyCreatures.Caching.Fusion.FusionCache(new FusionCacheOptions { CacheName = Guid.NewGuid().ToString() });
        cache.SetupDistributedLocker(locker);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Events.BackgroundFactorySuccess += (_, _) => backgroundFinished.TrySetResult();
        using var caller = new CancellationTokenSource();
        CancellationToken factoryToken = default;
        var foreground = cache.GetOrSetAsync<int>("product", async token =>
        {
            factoryToken = token;
            started.TrySetResult();
            await finish.Task;
            return 42;
        }, failSafeDefaultValue: -1, options: new FusionCacheEntryOptions
        {
            IsFailSafeEnabled = true, FactorySoftTimeout = TimeSpan.FromMilliseconds(50),
            FactoryHardTimeout = TimeSpan.FromMilliseconds(50), AllowTimedOutFactoryBackgroundCompletion = true,
            AllowBackgroundDistributedCacheOperations = false, ReThrowDistributedLockerExceptions = true,
        }, token: caller.Token).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await foreground.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(-1);
            var leases = new List<RespireKey>();
            await foreach (var key in client.Keys.ScanAsync("respire:fusioncache:lock:*:lease")) leases.Add(key);
            await Assert.That(leases.Count).IsEqualTo(1);
            await caller.CancelAsync();
            await Assert.That(factoryToken.IsCancellationRequested).IsTrue();
            await Assert.That(finish.Task.IsCompleted).IsFalse();
            if (releaseOnCancellation)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (await client.GetBytesAsync(leases[0], limit.Token) is not null) await Task.Delay(10, limit.Token);
            }
            else
            {
                await DistributedLockerTests.WaitPastLeaseExpiryAsync(TimeSpan.FromSeconds(2));
                await Assert.That(await client.GetBytesAsync(leases[0])).IsNotNull();
            }
            finish.TrySetResult();
            await backgroundFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await cache.GetOrDefaultAsync<int>("product")).IsEqualTo(42);
            // FusionCache announces factory success before its finally releases the handle.
            using var released = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await client.GetBytesAsync(leases[0], released.Token) is not null) await Task.Delay(10, released.Token);
        }
        finally
        {
            finish.TrySetResult();
            await foreground.WaitAsync(TimeSpan.FromSeconds(5));
            await backgroundFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RegisteredServiceDiscoveryPassesAcquiredLeasePolicy(bool releaseOnCancellation)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        services.AddFusionCacheRespireDistributedLocker(new() { ReleaseOnCallerCancellation = releaseOnCancellation });
        await using var provider = services.BuildServiceProvider();
        var locker = (RespireFusionCacheDistributedLocker)provider.GetRequiredService<IFusionCacheDistributedLocker>();
        using var caller = new CancellationTokenSource();
        var handle = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero, caller.Token))!;
        await caller.CancelAsync();
        await Assert.That(handle.OwnershipCancellationToken.IsCancellationRequested).IsEqualTo(releaseOnCancellation);
        await DistributedLockerTests.ReleaseAsync(locker, "cache", handle, caller.Token);
        await Assert.That(await client.GetBytesAsync(handle.LeaseKey)).IsNull();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task TwoNodesShareL2BackplaneAndLockerAndPreventConcurrentFactories(int protocol, bool synchronous)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = (RespProtocol)protocol, Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        };
        await using var firstClient = await RespireClient.ConnectAsync(options);
        await using var secondClient = await RespireClient.ConnectAsync(options);
        var prefix = "stampede:" + Guid.NewGuid() + ":";
        var (firstProvider, firstBuilder) = BuildProvider(firstClient, prefix);
        var (secondProvider, _) = BuildProvider(secondClient, prefix);
        await using var firstLifetime = firstProvider;
        await using var secondLifetime = secondProvider;
        var first = firstProvider.GetRequiredService<IFusionCache>();
        var second = secondProvider.GetRequiredService<IFusionCache>();
        await Assert.That(first.HasDistributedLocker && second.HasDistributedLocker).IsTrue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<int> Execute(IFusionCache cache, string key = "product") => synchronous
            ? Task.Run(() => cache.GetOrSet<int>(key, _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                release.Task.Wait(timeout.Token);
                return 42;
            }, token: timeout.Token))
            : cache.GetOrSetAsync<int>(key, async _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
                return 42;
            }, token: timeout.Token).AsTask();

        var producing = Execute(first);
        await entered.Task.WaitAsync(timeout.Token);
        var contending = Execute(second);
        try
        {
            await Task.Delay(150, timeout.Token);
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(contending.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        await Assert.That(await producing.WaitAsync(timeout.Token)).IsEqualTo(42);
        await Assert.That(await contending.WaitAsync(timeout.Token)).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);

        // A cancelled FusionCache contender propagates caller cancellation without running its factory.
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A fresh key avoids racing the previous phase's asynchronous backplane invalidation.
        producing = Execute(first, "cancelled-product");
        await entered.Task.WaitAsync(timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var cancelledWait = second.GetOrSetAsync<int>("cancelled-product", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(-1);
        }, token: cancelled.Token).AsTask();
        try
        {
            await Task.Delay(100, timeout.Token);
            await cancelled.CancelAsync();
            var error = await Assert.That(async () => await cancelledWait.WaitAsync(timeout.Token)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancelled.Token);
            await Assert.That(calls).IsEqualTo(2);
        }
        finally { release.TrySetResult(); }
        await producing.WaitAsync(timeout.Token);

        var locker = (RespireFusionCacheDistributedLocker)firstBuilder.DistributedLockerFactory!(firstProvider);
        var handle = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "unreleased", TimeSpan.Zero))!;
        await firstProvider.DisposeAsync();
        await secondProvider.DisposeAsync();
        await Assert.That(handle.OwnershipCancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(await firstClient.GetBytesAsync(handle.LeaseKey)).IsNull();
        await firstClient.SetAsync("still-alive", "yes");
        await Assert.That(await secondClient.GetStringAsync("still-alive")).IsEqualTo("yes");
    }

    [Test]
    public async Task RegisteredServiceDiscoveryUsesTransientProviderOwnedLockers()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        services.AddFusionCacheRespireDistributedLocker();
        services.AddFusionCache().WithRegisteredDistributedLocker();
        await using var provider = services.BuildServiceProvider();
        var first = (RespireFusionCacheDistributedLocker)provider.GetRequiredService<IFusionCacheDistributedLocker>();
        var second = provider.GetRequiredService<IFusionCacheDistributedLocker>();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(provider.GetRequiredService<IFusionCache>().HasDistributedLocker).IsTrue();
        var handle = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(first, "cache", TimeSpan.Zero))!;
        provider.Dispose();
        await Assert.That(await client.GetBytesAsync(handle.LeaseKey)).IsNull();
        await client.SetAsync("provider-disposed", "still-alive");
    }

    [Test]
    public async Task BuilderOptionsAndLifetimesRemainIndependentForNamedCaches()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        var alpha = services.AddFusionCache("alpha").WithRespireDistributedLocker(new() { LeaseDuration = TimeSpan.FromSeconds(2) });
        var beta = services.AddFusionCache("beta").WithRespireDistributedLocker(new() { LeaseDuration = TimeSpan.FromSeconds(30) });
        await using var provider = services.BuildServiceProvider();
        var first = (RespireFusionCacheDistributedLocker)alpha.DistributedLockerFactory!(provider);
        var second = (RespireFusionCacheDistributedLocker)beta.DistributedLockerFactory!(provider);
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        var shortLease = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(first, "alpha", TimeSpan.Zero))!;
        var longLease = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(second, "beta", TimeSpan.Zero))!;
        await Assert.That((await client.Keys.ExpiryAsync(shortLease.LeaseKey)).TimeToLive!.Value < TimeSpan.FromSeconds(3)).IsTrue();
        await Assert.That((await client.Keys.ExpiryAsync(longLease.LeaseKey)).TimeToLive!.Value > TimeSpan.FromSeconds(20)).IsTrue();
        await first.DisposeAsync();
        await Assert.That(await client.GetBytesAsync(longLease.LeaseKey)).IsNotNull();
    }

    private static (ServiceProvider Provider, IFusionCacheBuilder Builder) BuildProvider(IRespireClient client, string prefix)
    {
        var services = new ServiceCollection();
        services.AddSingleton(client);
        services.AddRespireDistributedCache(options => options.InstanceName = prefix);
        var builder = services.AddFusionCache().WithOptions(options =>
        {
            options.BackplaneChannelPrefix = prefix;
            options.WaitForInitialBackplaneSubscribe = true;
            options.DefaultEntryOptions = new()
            {
                Duration = TimeSpan.FromMinutes(1), DistributedLockTimeout = TimeSpan.FromSeconds(5),
                AllowBackgroundDistributedCacheOperations = false, AllowBackgroundBackplaneOperations = false,
                ReThrowDistributedLockerExceptions = true,
            };
        }).WithSerializer(new FusionCacheSystemTextJsonSerializer()).WithRegisteredDistributedCache()
          .WithRespireBackplane().WithRespireDistributedLocker();
        return (services.BuildServiceProvider(), builder);
    }
}
