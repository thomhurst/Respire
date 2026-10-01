using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FencedLockTests(RedisTestContainer fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(20);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReadWriteLeasesEnforceSharedAndExclusiveOwnership(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var coordination = new RespireCoordination(client);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:rw";
        await using var firstReader = await coordination.TryAcquireReadLockAsync(key, Lease);
        await using var secondReader = await coordination.TryAcquireReadLockAsync(key, Lease);
        await Assert.That(firstReader.Acquired && secondReader.Acquired).IsTrue();
        await using (var rejectedWriter = await coordination.TryAcquireWriteLockAsync(key, Lease))
            await Assert.That(rejectedWriter.Acquired).IsFalse();
        await using var thirdReader = await coordination.TryAcquireReadLockAsync(key, Lease);
        await Assert.That(thirdReader.Acquired).IsTrue();
        await Assert.That(await secondReader.Lock.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await firstReader.Lock.ReleaseAsync()).IsTrue();
        await using (var stillRejectedWriter = await coordination.TryAcquireWriteLockAsync(key, Lease))
            await Assert.That(stillRejectedWriter.Acquired).IsFalse();
        await secondReader.Lock.DisposeAsync();
        await thirdReader.Lock.DisposeAsync();

        await using var writer = await coordination.TryAcquireWriteLockAsync(key, Lease);
        await Assert.That(writer.Acquired).IsTrue();
        await Assert.That(writer.Lock.IsWriter).IsTrue();
        await using (var rejectedReader = await coordination.TryAcquireReadLockAsync(key, Lease))
            await Assert.That(rejectedReader.Acquired).IsFalse();
        await Assert.That(await writer.Lock.ResetExpiryAsync(Lease)).IsTrue();
        await Assert.That(await writer.Lock.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await writer.Lock.ReleaseAsync()).IsTrue();
        await Assert.That(await writer.Lock.ReleaseAsync()).IsFalse();
        await using var nextReader = await coordination.TryAcquireReadLockAsync(key, Lease);
        await Assert.That(nextReader.Acquired).IsTrue();
    }

    [Test]
    public async Task ExpiredOwnerCannotReleaseReplacementAndBinaryPrefixedKeyWorks()
    {
        await using var client = await ConnectAsync(3);
        var view = client.WithKeyPrefix($"rw:{Guid.NewGuid():N}:");
        byte[] key = [0xff, 0, 1];
        var pending = new RespireCoordination(view).TryAcquireReadLockAsync(key, TimeSpan.FromMilliseconds(150));
        key[2] = 2;
        await using var expired = await pending;
        await Assert.That(expired.Acquired).IsTrue();
        await Task.Delay(250);
        await using var replacement = await new RespireCoordination(view).TryAcquireWriteLockAsync(new byte[] { 0xff, 0, 1 }, Lease);
        await Assert.That(replacement.Acquired).IsTrue();
        await Assert.That(await expired.Lock.ReleaseAsync()).IsFalse();
        await Assert.That(await replacement.Lock.VerifyStillHeldAsync()).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ContentionReleaseAndRenewalPreserveMonotonicTokens(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var coordination = new RespireCoordination(client);
        var pair = Keys();
        await using var first = await coordination.TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);
        await Assert.That(first.Acquired).IsTrue();
        await Assert.That(first.Lock.FencingToken).IsEqualTo(1);
        await Assert.That(await client.Locks.GetOwnerTokenAsync(pair.Key)).IsEqualTo(first.Lock.OwnerToken);
        var losers = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await coordination.TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease)));
        await Assert.That(losers.All(attempt => !attempt.Acquired)).IsTrue();
        await Assert.That(await client.GetStringAsync(pair.Counter)).IsEqualTo("1");
        await Assert.That(await first.Lock.ResetExpiryAsync(Lease)).IsTrue();
        await Assert.That(first.Lock.FencingToken).IsEqualTo(1);
        await Assert.That(await first.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await first.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.AlreadyReleased);
        await using var second = await coordination.TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);
        await Assert.That(second.Lock.FencingToken).IsEqualTo(2);
        await Assert.That(second.Lock.OwnerToken == first.Lock.OwnerToken).IsFalse();
        await Assert.That(await first.Lock.ResetExpiryAsync(Lease)).IsFalse();
        await Assert.That(await second.Lock.VerifyStillHeldAsync()).IsTrue();
        using var ttl = await client.ExecuteAsync(RespireCommands.Key.PTTL, (RespireValue)pair.Counter);
        await Assert.That(ttl.AsInteger()).IsEqualTo(-1);
    }

    [Test]
    public async Task IndependentClientsHaveOneWinnerAndStaleOwnerCannotReleaseReplacement()
    {
        await using var first = await ConnectAsync(3);
        await using var second = await ConnectAsync(3);
        var pair = Keys();
        var coordinators = new[] { new RespireCoordination(first), new RespireCoordination(second) };
        var attempts = await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
            await coordinators[i % 2].TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease)));
        var winners = attempts.Where(attempt => attempt.Acquired).ToArray();
        await Assert.That(winners.Length).IsEqualTo(1);
        await using var old = winners.Single();
        // Delete simulates loss of the lease while retaining its persistent counter.
        await first.DeleteAsync(pair.Key);
        await using var next = await coordinators[1].TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);
        await Assert.That(next.Lock.FencingToken).IsEqualTo(old.Lock.FencingToken + 1);
        await Assert.That(await old.Lock.ResetExpiryAsync(Lease)).IsFalse();
        await Assert.That(await old.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.NotOwned);
        await Assert.That(await next.Lock.VerifyStillHeldAsync()).IsTrue();
        var protectedResource = new FencedResource();
        await Assert.That(protectedResource.Write(next.Lock.FencingToken, "new")).IsTrue();
        await Assert.That(protectedResource.Write(old.Lock.FencingToken, "stale")).IsFalse();
        await Assert.That(protectedResource.Value).IsEqualTo("new");
    }

    [Test]
    public async Task AcquireWaitsForOwnerReleaseNotification()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Connections = 1, Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        var pair = Keys();
        var coordination = new RespireCoordination(client);
        await using var owner = await coordination.TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);

        var waiting = coordination.AcquireFencedLockAsync(pair.Key, pair.Counter, Lease).AsTask();
        await Task.Delay(100);
        await Assert.That(waiting.IsCompleted).IsFalse();

        await Assert.That(await owner.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(acquired.FencingToken).IsEqualTo(owner.Lock.FencingToken + 1);
    }

    [Test]
    public async Task AcquireWaitsForLeaseExpiryNotification()
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Connections = 1, Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        var pair = Keys();
        var coordination = new RespireCoordination(client);
        await using var owner = await coordination.TryAcquireFencedLockAsync(
            pair.Key, pair.Counter, TimeSpan.FromMilliseconds(300));

        await using var acquired = await coordination.AcquireFencedLockAsync(pair.Key, pair.Counter, Lease)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(acquired.FencingToken).IsEqualTo(owner.Lock.FencingToken + 1);
    }

    [Test]
    public async Task ExpiryAndReconnectKeepCounterHistory()
    {
        var pair = Keys();
        await using (var first = await ConnectAsync(2))
        {
            var attempt = await new RespireCoordination(first).TryAcquireFencedLockAsync(pair.Key, pair.Counter, TimeSpan.FromMilliseconds(200));
            await Assert.That(attempt.Acquired).IsTrue();
            // A lost client cannot release; only the bounded Redis lease expires.
        }
        await Task.Delay(300);
        await using var next = await ConnectAsync(3);
        await using var replacement = await new RespireCoordination(next).TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);
        await Assert.That(replacement.Lock.FencingToken).IsEqualTo(2);
    }

    [Test]
    [Arguments("9007199254740992", "9007199254740993")]
    [Arguments("9223372036854775806", "9223372036854775807")]
    public async Task FullInt64PrecisionSurvivesLua(string initial, string expected)
    {
        await using var client = await ConnectAsync(3);
        var pair = Keys();
        await client.SetAsync(pair.Counter, initial);
        await using var attempt = await new RespireCoordination(client).TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease);
        await Assert.That(attempt.Lock.FencingToken).IsEqualTo(long.Parse(expected, CultureInfo.InvariantCulture));
        await Assert.That(await client.GetStringAsync(pair.Counter)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("9223372036854775807", false)]
    [Arguments("-1", false)]
    [Arguments("01", false)]
    [Arguments("garbage", false)]
    [Arguments("5", true)]
    public async Task InvalidCounterNeverCreatesLease(string counter, bool expires)
    {
        await using var client = await ConnectAsync(3);
        var pair = Keys();
        await client.SetAsync(pair.Counter, counter);
        if (expires)
        {
            using var expiry = await client.ExecuteAsync(RespireCommands.Key.PEXPIRE, (RespireValue)pair.Counter, 60000);
        }
        await Assert.That(async () => await new RespireCoordination(client).TryAcquireFencedLockAsync(pair.Key, pair.Counter, Lease))
            .Throws<RespireServerException>();
        await Assert.That(await client.GetBytesAsync(pair.Key)).IsNull();
        await Assert.That(await client.GetStringAsync(pair.Counter)).IsEqualTo(counter);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task DirectScriptRejectsIdenticalKeysBeforeAnyMutation(int protocol, bool existing)
    {
        await using var client = await ConnectAsync(protocol);
        var key = Keys().Key;
        if (existing) await client.SetAsync(key, "17");
        var error = await Assert.That(async () =>
        {
            using var result = await client.Scripts.ExecuteAsync(RespireCoordination.AcquireFencedLock,
                [key, key], ["owner", 20000]);
        }).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("lock and fencing counter keys must differ");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo(existing ? "17" : null);
    }

    [Test]
    public async Task BinaryKeysAndPrefixUseTheSamePhysicalPairForEveryOperation()
    {
        await using var client = await ConnectAsync(3);
        var prefix = $"prefix:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        byte[] key = [0xff, 0, 1];
        byte[] counter = [0xff, 0, 2];
        var pending = new RespireCoordination(view).TryAcquireFencedLockAsync(key, counter, Lease);
        key[2] = 3;
        counter[2] = 4;
        await using var attempt = await pending;
        await Assert.That(await view.Locks.GetOwnerTokenAsync(new byte[] { 0xff, 0, 1 })).IsEqualTo(attempt.Lock.OwnerToken);
        await Assert.That(await view.GetStringAsync(new byte[] { 0xff, 0, 2 })).IsEqualTo("1");
        await Assert.That(await attempt.Lock.ResetExpiryAsync(Lease)).IsTrue();
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await view.GetBytesAsync(new byte[] { 0xff, 0, 1 })).IsNull();
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
        Connections = 1, Protocol = (RespProtocol)protocol,
    });
    private static (RespireKey Key, RespireKey Counter) Keys()
    {
        var tag = Guid.NewGuid().ToString("N");
        return ($"{{{tag}}}:lease", $"{{{tag}}}:counter");
    }
    private sealed class FencedResource
    {
        private long _fence;
        internal string? Value;
        internal bool Write(long fence, string value)
        {
            lock (this)
            {
                if (fence < _fence) return false;
                _fence = fence;
                Value = value;
                return true;
            }
        }
    }
}
