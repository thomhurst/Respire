using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FairLockTests(RedisTestContainer fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.OptOut)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task IndependentClientsAcquireInServerArrivalOrderWithIncreasingFences(RespireClientTrackingMode trackingMode)
    {
        await using var first = await ConnectAsync(trackingMode);
        await using var second = await ConnectAsync(trackingMode);
        var fair = Create(first);
        var other = new RespireCoordination(second).CreateFairLock(fair.Key, fair.FencingCounterKey, fair.QueueKey);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        var pending = new List<Task<RespireFencedLock>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 8; i++)
        {
            pending.Add((i % 2 == 0 ? fair : other).AcquireAsync(Lease, Wait, timeout.Token).AsTask());
            await WaitForQueueAsync(first, fair, i + 1, timeout.Token);
        }
        await holder.DisposeAsync();
        for (var i = 0; i < pending.Count; i++)
        {
            await using var acquired = await pending[i].WaitAsync(timeout.Token);
            await Assert.That(acquired.FencingToken).IsEqualTo(i + 2L);
            await Assert.That(pending.Skip(i + 1).All(task => !task.IsCompleted)).IsTrue();
            await Assert.That(await acquired.VerifyStillHeldAsync()).IsTrue();
        }
        await Assert.That(await first.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CancellationUnlinksHeadMiddleAndTail(int cancelled)
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var pending = new List<Task<RespireFencedLock>>();
        for (var i = 0; i < 3; i++)
        {
            pending.Add(fair.AcquireAsync(Lease, Wait, i == cancelled ? cancel.Token : timeout.Token).AsTask());
            await WaitForQueueAsync(client, fair, i + 1, timeout.Token);
        }
        await cancel.CancelAsync();
        await Assert.That(async () => await pending[cancelled]).Throws<OperationCanceledException>();
        await WaitForQueueAsync(client, fair, 2, timeout.Token);
        await holder.DisposeAsync();
        long fence = 2;
        for (var i = 0; i < 3; i++)
        {
            if (i == cancelled) continue;
            await using var acquired = await pending[i].WaitAsync(timeout.Token);
            await Assert.That(acquired.FencingToken).IsEqualTo(fence++);
        }
    }

    [Test]
    public async Task CrashedHeadIsSkippedAtItsDeadlineWithoutNotifications()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        // Enqueue an owner with no managed waiter to cancel, release or process notifications.
        using (var orphan = await client.Scripts.ExecuteAsync(RespireFairLock.Acquire,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], ["orphan", 20000, 10000, "join"]))
            await Assert.That(orphan[0].AsInteger()).IsEqualTo(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = fair.AcquireAsync(Lease, Wait, timeout.Token).AsTask();
        await WaitForQueueAsync(client, fair, 2, timeout.Token);
        // Start the short abandoned-waiter deadline only after the successor has joined.
        using (var shortened = await client.Scripts.ExecuteAsync(RespireScript.Create("""
            local t = redis.call('TIME')
            redis.call('HSET', KEYS[1], 'dorphan', t[1] * 1000 + math.floor(t[2] / 1000) + 200)
            return 1
            """), [fair.QueueKey], [])) { }
        await holder.DisposeAsync();
        await using var acquired = await next.WaitAsync(timeout.Token);
        await Assert.That(acquired.FencingToken).IsEqualTo(2);
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
    }

    [Test]
    public async Task ExpiredHeadCleanupIsBoundedAndPreservesLiveWaiterOrder()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await SeedExpiredWaitersAsync(client, fair);
        using (var first = await client.Scripts.ExecuteAsync(RespireFairLock.Acquire,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], ["first", 20000, 10000, "join"]))
            await Assert.That(first[0].AsInteger()).IsEqualTo(2);
        await Assert.That(await client.Hashes.GetStringAsync(fair.QueueKey, "head")).IsEqualTo("orphan64");
        var deadline = await client.Hashes.GetStringAsync(fair.QueueKey, "dfirst");
        using (var second = await client.Scripts.ExecuteAsync(RespireFairLock.Acquire,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], ["second", 20000, 10000, "join"]))
            await Assert.That(second[0].AsInteger()).IsEqualTo(2);
        await Assert.That(await client.Hashes.GetStringAsync(fair.QueueKey, "head")).IsEqualTo("orphan128");
        using (var waiting = await client.Scripts.ExecuteAsync(RespireFairLock.Acquire,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], ["second", 20000, 10000, "retry"]))
            await Assert.That(waiting[0].AsInteger()).IsEqualTo(0);
        await Assert.That(await client.Hashes.GetStringAsync(fair.QueueKey, "head")).IsEqualTo("first");
        await Assert.That(await client.Hashes.GetStringAsync(fair.QueueKey, "dfirst")).IsEqualTo(deadline);
        using (var acquired = await client.Scripts.ExecuteAsync(RespireFairLock.Acquire,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], ["first", 20000, 10000, "retry"]))
            await Assert.That(acquired[0].AsInteger()).IsEqualTo(1);
        await Assert.That(await client.Hashes.GetStringAsync(fair.QueueKey, "head")).IsEqualTo("second");
    }

    [Test]
    public async Task ManagedAcquisitionContinuesExpiredHeadCleanupWithoutWaiting()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await SeedExpiredWaitersAsync(client, fair);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var acquired = await fair.AcquireAsync(Lease, Wait, timeout.Token);
        await Assert.That(acquired.FencingToken).IsEqualTo(1);
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CleanupOnlyReleasesMatchingUnreturnedOwner(bool matchingOwner)
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        RespireValue owner = matchingOwner ? holder.OwnerToken.Bytes : "another-owner";
        using var cleanup = await client.Scripts.ExecuteAsync(RespireFairLock.RemoveWaiter,
            [fair.Key, fair.FencingCounterKey, fair.QueueKey], [owner, "release"]);
        await Assert.That(await holder.VerifyStillHeldAsync()).IsEqualTo(!matchingOwner);
    }

    private static async Task SeedExpiredWaitersAsync(IRespireClient client, RespireFairLock fair)
    {
        using var seeded = await client.Scripts.ExecuteAsync(RespireScript.Create("""
            for i = 0, 129 do
                local owner = 'orphan' .. i
                redis.call('HSET', KEYS[1], 'd' .. owner, '0')
                if i > 0 then redis.call('HSET', KEYS[1], 'p' .. owner, 'orphan' .. (i - 1)) end
                if i < 129 then redis.call('HSET', KEYS[1], 'n' .. owner, 'orphan' .. (i + 1)) end
            end
            redis.call('HSET', KEYS[1], 'head', 'orphan0', 'tail', 'orphan129')
            redis.call('PEXPIRE', KEYS[1], 60000)
            return 1
            """), [fair.QueueKey], []);
    }

    [Test]
    public async Task WaitTimeoutDoesNotRejoinOrConsumeAFence()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        await Assert.That(async () => await fair.AcquireAsync(Lease, TimeSpan.FromMilliseconds(100)))
            .Throws<TimeoutException>();
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
        await Assert.That(await client.GetStringAsync(fair.FencingCounterKey)).IsEqualTo("1");
        await Assert.That(await holder.VerifyStillHeldAsync()).IsTrue();
    }

    [Test]
    public async Task ExpiredLeaseWakesQueueAndStaleOwnerCannotReleaseSuccessor()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await using var holder = await fair.AcquireAsync(Lease, Wait);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var next = fair.AcquireAsync(Lease, Wait, timeout.Token).AsTask();
        await WaitForQueueAsync(client, fair, 1, timeout.Token);
        await client.Keys.ExpireAsync(fair.Key, RespireExpiry.In(TimeSpan.FromMilliseconds(100)));
        await using var acquired = await next.WaitAsync(timeout.Token);
        await Assert.That(acquired.FencingToken).IsEqualTo(2);
        await holder.ReleaseAsync();
        await Assert.That(await acquired.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await acquired.ResetExpiryAsync(Lease)).IsTrue();
        await Assert.That(acquired.FencingToken).IsEqualTo(2);
    }

    [Test]
    public async Task PrefixAndMutableBinaryKeysAreSnapshottedAndFenceKeepsInt64Precision()
    {
        await using var client = await ConnectAsync();
        var view = client.WithKeyPrefix($"fair:{Guid.NewGuid():N}:");
        byte[] lease = [0xff, 0, 1], counter = [0xff, 0, 2], queue = [0xff, 0, 3];
        var fair = new RespireCoordination(view).CreateFairLock(lease, counter, queue);
        lease[2] = counter[2] = queue[2] = 4;
        await view.SetAsync(new byte[] { 0xff, 0, 2 }, "9007199254740992");
        await using var acquired = await fair.AcquireAsync(Lease, Wait);
        await Assert.That(acquired.FencingToken).IsEqualTo(9007199254740993L);
        await Assert.That(await view.Locks.GetOwnerTokenAsync(new byte[] { 0xff, 0, 1 })).IsEqualTo(acquired.OwnerToken);
        await acquired.DisposeAsync();
        await Assert.That(await view.Keys.ExistsAsync(new byte[] { 0xff, 0, 1 })).IsFalse();
    }

    [Test]
    [Arguments("9223372036854775807")]
    [Arguments("-1")]
    [Arguments("01")]
    public async Task InvalidCounterDoesNotLeaveALeaseOrQueue(string counter)
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await client.SetAsync(fair.FencingCounterKey, counter);
        await Assert.That(async () => await fair.AcquireAsync(Lease, Wait)).Throws<RespireServerException>();
        await Assert.That(await client.Keys.ExistsAsync(fair.Key)).IsFalse();
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
        await Assert.That(await client.GetStringAsync(fair.FencingCounterKey)).IsEqualTo(counter);
    }

    [Test]
    public async Task RejectsMissingTrackingAliasedKeysAndInvalidTimeoutsBeforeEnqueue()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var fair = Create(client);
        await Assert.That(() => new RespireCoordination(client).CreateFairLock("same", "same", "queue"))
            .Throws<ArgumentException>();
        await Assert.That(async () => await fair.AcquireAsync(Lease, Wait)).Throws<RespireConfigurationException>();
        await Assert.That(async () => await fair.AcquireAsync(Lease, TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await fair.AcquireAsync(Lease, TimeSpan.FromDays(25))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
    }

    [Test]
    public async Task RejectsNoLoopBeforeEnqueue()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { NoLoop = true }, Connections = 1,
        });
        var fair = Create(client);
        await Assert.That(async () => await fair.AcquireAsync(Lease, Wait)).Throws<RespireConfigurationException>();
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
    }

    [Test]
    public async Task ExpiringCounterIsRejectedWithoutMutation()
    {
        await using var client = await ConnectAsync();
        var fair = Create(client);
        await client.SetAsync(fair.FencingCounterKey, "5");
        await client.Keys.ExpireAsync(fair.FencingCounterKey, RespireExpiry.In(Lease));
        await Assert.That(async () => await fair.AcquireAsync(Lease, Wait)).Throws<RespireServerException>();
        await Assert.That(await client.Keys.ExistsAsync(fair.Key)).IsFalse();
        await Assert.That(await client.Keys.ExistsAsync(fair.QueueKey)).IsFalse();
        await Assert.That(await client.GetStringAsync(fair.FencingCounterKey)).IsEqualTo("5");
    }

    [Test]
    public async Task RejectsUntrackedPrefixesAndCacheBypassBeforeEnqueue()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Protocol = RespireProtocol.Resp3, ClientSideCache = new() { KeyPrefixes = ["covered:"] }, Connections = 1,
        });
        var uncovered = Create(client);
        await Assert.That(async () => await uncovered.AcquireAsync(Lease, Wait)).Throws<RespireConfigurationException>();
        var bypass = Create(client.WithKeyPrefix("covered:").WithoutClientCache());
        await Assert.That(async () => await bypass.AcquireAsync(Lease, Wait)).Throws<RespireConfigurationException>();
        var covered = Create(client.WithKeyPrefix("covered:"));
        await using var acquired = await covered.AcquireAsync(Lease, Wait);
        await Assert.That(acquired.FencingToken).IsEqualTo(1);
    }

    private ValueTask<RespireClient> ConnectAsync(RespireClientTrackingMode trackingMode = RespireClientTrackingMode.OptIn) => RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
        Protocol = RespireProtocol.Resp3, ClientSideCache = new() { TrackingMode = trackingMode }, Connections = 1,
    });

    private static RespireFairLock Create(IRespireClient client)
    {
        var tag = Guid.NewGuid().ToString("N");
        return new RespireCoordination(client).CreateFairLock($"{{{tag}}}:lease", $"{{{tag}}}:counter", $"{{{tag}}}:queue");
    }

    private static async Task WaitForQueueAsync(IRespireClient client, RespireFairLock fair, int count, CancellationToken token)
    {
        // Observe committed enqueue order; do not infer FIFO from task scheduling or sleeps.
        while (true)
        {
            using var length = await client.ExecuteAsync(RespireCommands.Hash.HLEN, token, (RespireValue)fair.QueueKey);
            if (length.AsInteger() == count * 3) return;
            await Task.Delay(10, token);
        }
    }
}
