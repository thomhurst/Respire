using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.FusionCache.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DistributedLockerTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ImmediateContentionFiniteTimeoutAndPersistentCounters(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await using var first = new RespireFusionCacheDistributedLocker(client);
        await using var second = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        var owner = (RespireFusionCacheLock)(await AcquireAsync(first, name, TimeSpan.Zero))!;
        await Assert.That(owner.FencingToken).IsEqualTo(1);
        await Assert.That(await AcquireAsync(second, name, TimeSpan.Zero)).IsNull();
        await Assert.That(await AcquireAsync(second, name, TimeSpan.FromMilliseconds(100))).IsNull();
        await Assert.That(await client.GetStringAsync(owner.FencingCounterKey)).IsEqualTo("1");
        var counterTtl = await client.Keys.ExpiryAsync(owner.FencingCounterKey);
        await Assert.That(counterTtl.Exists && !counterTtl.HasExpiry).IsTrue();
        await ReleaseAsync(first, name, owner);
        await ReleaseAsync(first, name, owner);
        var next = (RespireFusionCacheLock)(await AcquireAsync(second, name, TimeSpan.Zero))!;
        await Assert.That(next.FencingToken).IsEqualTo(2);
        await Assert.That((await client.Keys.ExpiryAsync(next.LeaseKey)).HasExpiry).IsTrue();
        await ReleaseAsync(second, name, next);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InfiniteAndVeryLargeFiniteWaitsAcquireAfterReleaseWithoutTracking(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await using var first = new RespireFusionCacheDistributedLocker(client);
        await using var second = new RespireFusionCacheDistributedLocker(client);
        foreach (var timeout in new[] { Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        {
            var name = Guid.NewGuid().ToString();
            var owner = (RespireFusionCacheLock)(await AcquireAsync(first, name, TimeSpan.Zero))!;
            var pending = AcquireAsync(second, name, timeout).AsTask();
            await Task.Delay(100);
            await Assert.That(pending.IsCompleted).IsFalse();
            await ReleaseAsync(first, name, owner);
            var next = (RespireFusionCacheLock)(await pending.WaitAsync(TimeSpan.FromSeconds(5)))!;
            await Assert.That(next.FencingToken).IsEqualTo(2);
            await ReleaseAsync(second, name, next);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CallerCancellationWhileWaitingAndAfterHandoffStopsRenewal(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        using var ownerCancellation = new CancellationTokenSource();
        var owner = (RespireFusionCacheLock)(await AcquireAsync(locker, name, TimeSpan.Zero, ownerCancellation.Token))!;
        using var waitingCancellation = new CancellationTokenSource();
        var pending = AcquireAsync(locker, name, Timeout.InfiniteTimeSpan, waitingCancellation.Token).AsTask();
        await waitingCancellation.CancelAsync();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(waitingCancellation.Token);
        await ownerCancellation.CancelAsync();
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(owner.OwnershipCancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNull();
        await Assert.That(await client.GetStringAsync(owner.FencingCounterKey)).IsEqualTo("1");
        await Assert.That(async () => await ReleaseAsync(locker, name, owner, ownerCancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNull();
    }

    [Test]
    public async Task CacheNamesLockNamesAndClientPrefixesIsolateLeaseIdentities()
    {
        await using var client = await ConnectAsync(2);
        await using var first = new RespireFusionCacheDistributedLocker(client.WithKeyPrefix("first:"));
        await using var second = new RespireFusionCacheDistributedLocker(client.WithKeyPrefix("second:"));
        var name = Guid.NewGuid().ToString();
        var one = (RespireFusionCacheLock)(await AcquireAsync(first, name, TimeSpan.Zero))!;
        var two = (RespireFusionCacheLock)(await AcquireAsync(first, name + ":other", TimeSpan.Zero))!;
        var prefixed = (RespireFusionCacheLock)(await AcquireAsync(second, name, TimeSpan.Zero))!;
        var differentLock = (RespireFusionCacheLock)(await first.AcquireLockAsync(name, "another-instance", "another-operation",
            "ignored-diagnostic-key", "other-lock", TimeSpan.Zero, null, default))!;
        await Assert.That(one.LeaseKey == two.LeaseKey).IsFalse();
        await Assert.That(one.LeaseKey == prefixed.LeaseKey).IsTrue();
        await Assert.That(one.LeaseKey == differentLock.LeaseKey).IsFalse();
        await Assert.That(await AcquireAsync(first, name, TimeSpan.Zero)).IsNull();
        // Length framing and raw UTF-16 code units keep ambiguous or malformed strings distinct.
        var malformedOne = (RespireFusionCacheLock)(await AcquireAsync(first, "\ud800", TimeSpan.Zero))!;
        var malformedTwo = (RespireFusionCacheLock)(await AcquireAsync(first, "\ud801", TimeSpan.Zero))!;
        await Assert.That(malformedOne.LeaseKey == malformedTwo.LeaseKey).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StaleOwnerReleaseCannotDeleteReplacement(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        await using var first = new RespireFusionCacheDistributedLocker(client);
        await using var second = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        var stale = (RespireFusionCacheLock)(await AcquireAsync(first, name, TimeSpan.Zero))!;
        await client.DeleteAsync(stale.LeaseKey);
        var current = (RespireFusionCacheLock)(await AcquireAsync(second, name, TimeSpan.Zero))!;
        await Assert.That(current.FencingToken).IsEqualTo(2);
        var currentOwner = await client.GetBytesAsync(current.LeaseKey);
        await ReleaseAsync(first, name, stale);
        await Assert.That((await client.GetBytesAsync(current.LeaseKey))!.SequenceEqual(currentOwner!)).IsTrue();
        await Assert.That(await AcquireAsync(first, name, TimeSpan.Zero)).IsNull();
    }

    [Test]
    [NotInParallel]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RenewalRetainsFenceAndOwnershipLossEndsHandle(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var options = new RespireFusionCacheDistributedLockerOptions { LeaseDuration = TimeSpan.FromSeconds(2) };
        await using var locker = new RespireFusionCacheDistributedLocker(client, options);
        var name = Guid.NewGuid().ToString();
        var owner = (RespireFusionCacheLock)(await AcquireAsync(locker, name, TimeSpan.Zero))!;
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNotNull();
        await Assert.That(await client.GetStringAsync(owner.FencingCounterKey)).IsEqualTo("1");
        await Assert.That(owner.OwnershipLost).IsFalse();
        await client.DeleteAsync(owner.LeaseKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.OwnershipCancellationToken.IsCancellationRequested) await Task.Delay(10, timeout.Token);
        await owner.DisposeAsync();
        await Assert.That(owner.OwnershipLost).IsTrue();
        var next = (RespireFusionCacheLock)(await AcquireAsync(locker, name, TimeSpan.Zero))!;
        await Assert.That(next.FencingToken).IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentLockerTeardownJoinsHandlesCancelsWaitersAndPreservesClient()
    {
        await using var client = await ConnectAsync(2);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        var owner = (RespireFusionCacheLock)(await AcquireAsync(locker, name, TimeSpan.Zero))!;
        var pending = AcquireAsync(locker, name, Timeout.InfiniteTimeSpan).AsTask();
        await Task.WhenAll(locker.DisposeAsync().AsTask(), locker.DisposeAsync().AsTask(), owner.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<ObjectDisposedException>();
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNull();
        await client.SetAsync("client-survives", "yes");
        await Assert.That(await client.GetStringAsync("client-survives")).IsEqualTo("yes");
        await Assert.That(async () => await AcquireAsync(locker, name, TimeSpan.Zero)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task SyncEntryPointsWaitReleaseAndRespectCancellation()
    {
        await using var client = await ConnectAsync(2);
        using var first = new RespireFusionCacheDistributedLocker(client);
        using var second = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        var owner = (RespireFusionCacheLock)first.AcquireLock(name, "first", "operation", "key", "lock", TimeSpan.Zero, null, default)!;
        await Assert.That(second.AcquireLock(name, "second", "other", "key", "lock", TimeSpan.Zero, null, default)).IsNull();
        var pending = Task.Run(() => second.AcquireLock(name, "second", "other", "key", "lock", Timeout.InfiniteTimeSpan, null, default));
        first.ReleaseLock(name, "first", "operation", "key", "lock", owner, null, default);
        var next = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(() => second.ReleaseLock(name, "second", "other", "key", "lock", next, null, cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(await client.GetBytesAsync(owner.LeaseKey)).IsNull();
        await Assert.That(() => first.AcquireLock(name, "first", "operation", "key", "lock", TimeSpan.Zero, null, cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InvalidTimeoutAndForeignHandleFailWithoutReleasingOwner()
    {
        await using var client = await ConnectAsync(2);
        await using var first = new RespireFusionCacheDistributedLocker(client);
        await using var second = new RespireFusionCacheDistributedLocker(client);
        var name = Guid.NewGuid().ToString();
        await Assert.That(async () => await AcquireAsync(first, name, TimeSpan.FromMilliseconds(-2))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireFusionCacheDistributedLocker(client, new() { LeaseDuration = TimeSpan.Zero }))
            .Throws<ArgumentOutOfRangeException>();
        var owner = (RespireFusionCacheLock)(await AcquireAsync(first, name, TimeSpan.Zero))!;
        await Assert.That(async () => await ReleaseAsync(second, name, owner)).Throws<ArgumentException>();
        await Assert.That(await AcquireAsync(second, name, TimeSpan.Zero)).IsNull();
        await ReleaseAsync(second, name, null);
    }

    private Task<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
    {
        Protocol = (RespProtocol)protocol, Connections = 1, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
    }).AsTask();

    internal static ValueTask<object?> AcquireAsync(RespireFusionCacheDistributedLocker locker, string cacheName, TimeSpan timeout,
        CancellationToken token = default) => locker.AcquireLockAsync(cacheName, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            "key", "lock", timeout, null, token);

    internal static ValueTask ReleaseAsync(RespireFusionCacheDistributedLocker locker, string cacheName, object? handle,
        CancellationToken token = default) => locker.ReleaseLockAsync(cacheName, "instance", "operation", "key", "lock", handle, null, token);
}
