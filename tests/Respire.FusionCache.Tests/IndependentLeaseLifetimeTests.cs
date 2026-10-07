using Microsoft.Extensions.DependencyInjection;
using Respire.Coordination;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Locking.Distributed;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class IndependentLeaseLifetimeTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 0)]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(2, 2)]
    [Arguments(3, 2)]
    public async Task LifetimePropagatesThroughDirectBuilderAndRegisteredService(int protocol, int registration)
    {
        await using var client = await ConnectAsync(protocol);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        var options = new RespireFusionCacheDistributedLockerOptions
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(100),
        };
        var builder = registration == 1 ? services.AddFusionCache().WithRespireDistributedLocker(options) : null;
        if (registration == 2) services.AddFusionCacheRespireDistributedLocker(options);
        await using var provider = services.BuildServiceProvider();
        await using var locker = registration switch
        {
            1 => (RespireFusionCacheDistributedLocker)builder!.DistributedLockerFactory!(provider),
            2 => (RespireFusionCacheDistributedLocker)provider.GetRequiredService<IFusionCacheDistributedLocker>(),
            _ => new RespireFusionCacheDistributedLocker(client, options),
        };
        var name = Guid.NewGuid().ToString();
        using var caller = new CancellationTokenSource();
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, name, TimeSpan.Zero, caller.Token))!;
        await caller.CancelAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await IndependentLeaseLifetimeWireTests.WaitForCancellationAsync(owner, limit.Token);
        await Task.WhenAll(owner.DisposeAsync().AsTask(), locker.DisposeAsync().AsTask(),
            DistributedLockerTests.ReleaseAsync(locker, name, owner, caller.Token).AsTask()).WaitAsync(limit.Token);
        await Assert.That(owner.LifetimeLimitExpired).IsTrue();
        await Assert.That(owner.OwnershipLost).IsFalse();
        await Assert.That(owner.RenewalFailure).IsNull();
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNull();
        await Assert.That(await client.GetStringAsync(owner.FencingCounterKey)).IsEqualTo("1");
        await client.SetAsync("client-survives", "yes");
        await Assert.That(await client.GetStringAsync("client-survives")).IsEqualTo("yes");
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task BackgroundFactoryCanCompleteAfterLimitWithoutReleasingReplacement(int protocol, bool registeredService)
    {
        await using var client = await ConnectAsync(protocol);
        var services = new ServiceCollection();
        services.AddSingleton<IRespireClient>(client);
        var options = new RespireFusionCacheDistributedLockerOptions
        {
            ReleaseOnCallerCancellation = false, LeaseDuration = TimeSpan.FromSeconds(1),
            MaximumIndependentLeaseLifetime = TimeSpan.FromSeconds(3),
        };
        var builder = services.AddFusionCache(Guid.NewGuid().ToString());
        if (registeredService) services.AddFusionCacheRespireDistributedLocker(options);
        else builder.WithRespireDistributedLocker(options);
        await using var provider = services.BuildServiceProvider();
        var locker = registeredService
            ? (RespireFusionCacheDistributedLocker)provider.GetRequiredService<IFusionCacheDistributedLocker>()
            : (RespireFusionCacheDistributedLocker)builder.DistributedLockerFactory!(provider);
        using var cache = new ZiggyCreatures.Caching.Fusion.FusionCache(new FusionCacheOptions { CacheName = Guid.NewGuid().ToString() });
        cache.SetupDistributedLocker(locker);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Events.BackgroundFactorySuccess += (_, _) => completed.TrySetResult();
        using var caller = new CancellationTokenSource();
        var foreground = cache.GetOrSetAsync<int>("product", async _ =>
        {
            entered.TrySetResult();
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
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await foreground.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(-1);
            var leases = new List<RespireKey>();
            await foreach (var key in client.Keys.ScanAsync("respire:fusioncache:lock:*:lease")) leases.Add(key);
            await Assert.That(leases.Count).IsEqualTo(1);
            await caller.CancelAsync();
            await DistributedLockerTests.WaitPastLeaseExpiryAsync(TimeSpan.FromSeconds(1));
            // The original server lease expired by now without renewal. The optional total limit has not expired.
            await Assert.That(await client.GetBytesAsync(leases[0])).IsNotNull();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await client.GetBytesAsync(leases[0], deadline.Token) is not null) await Task.Delay(10, deadline.Token);
            await Assert.That(finish.Task.IsCompleted).IsFalse();
            await Assert.That(completed.Task.IsCompleted).IsFalse();
            var coordination = new RespireCoordination(client);
            await using var replacement = await coordination.TryAcquireFencedLockAsync(leases[0],
                leases[0].ToString()[..^"lease".Length] + "counter", TimeSpan.FromSeconds(30));
            await Assert.That(replacement.Acquired).IsTrue();
            await Assert.That(replacement.Lock.FencingToken).IsEqualTo(2);
            var replacementOwner = await client.GetBytesAsync(leases[0]);
            finish.TrySetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await cache.GetOrDefaultAsync<int>("product")).IsEqualTo(42);
            // Factory success precedes FusionCache's finally. Teardown joins any still-tracked original cleanup.
            await locker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That((await client.GetBytesAsync(leases[0]))!.SequenceEqual(replacementOwner!)).IsTrue();
            await Assert.That(await client.Strings.IncrementAsync("client-survives")).IsEqualTo(1);
        }
        finally
        {
            finish.TrySetResult();
            await foreground.WaitAsync(TimeSpan.FromSeconds(5));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiredStaleHandleCannotDeleteReplacementOwner(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await using var original = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(200),
        });
        await using var next = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        var stale = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(original, name, TimeSpan.Zero))!;
        await client.DeleteAsync(stale.LeaseKey);
        var current = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(next, name, TimeSpan.Zero))!;
        var currentOwner = await client.GetBytesAsync(current.LeaseKey);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await IndependentLeaseLifetimeWireTests.WaitForCancellationAsync(stale, deadline.Token);
        await stale.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        await Assert.That(stale.LifetimeLimitExpired).IsTrue();
        await Assert.That(current.FencingToken).IsEqualTo(2);
        await Assert.That((await client.GetBytesAsync(current.LeaseKey))!.SequenceEqual(currentOwner!)).IsTrue();
    }

    private Task<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
    {
        Protocol = (RespProtocol)protocol, Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
    }).AsTask();
}
