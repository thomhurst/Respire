using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SemaphoreTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentClientsNeverExceedCapacity(int protocol)
    {
        await using var firstClient = await ConnectAsync(protocol);
        await using var secondClient = await ConnectAsync(protocol);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore";
        var semaphores = new[]
        {
            new RespireSemaphore(firstClient, key, capacity: 3),
            new RespireSemaphore(secondClient, key, capacity: 3),
        };
        var attempts = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            semaphores[i % semaphores.Length].TryAcquireAsync(TimeSpan.FromSeconds(20)).AsTask()));
        var winners = attempts.Where(attempt => attempt.Acquired).ToArray();
        await Assert.That(winners.Length).IsEqualTo(3);
        await Assert.That(winners.Select(attempt => attempt.Permit).Distinct().Count()).IsEqualTo(3);
        foreach (var winner in winners) await winner.Permit.ReleaseAsync();
        await using var replacement = await new RespireSemaphore(firstClient, key, capacity: 1).TryAcquireAsync();
        await Assert.That(replacement.Acquired).IsTrue();
    }

    [Test]
    public async Task CapacityChangeRequiresAllExistingPermitsToDrain()
    {
        await using var client = await ConnectAsync(3);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore-capacity";
        await using var first = await new RespireSemaphore(client, key, capacity: 2).TryAcquireAsync();
        await using var second = await new RespireSemaphore(client, key, capacity: 2).TryAcquireAsync();
        var changed = new RespireSemaphore(client, key, capacity: 1);
        var error = await Assert.That(async () => await changed.TryAcquireAsync())
            .Throws<RespireSemaphoreCapacityMismatchException>();
        await Assert.That(error!.RequestedCapacity).IsEqualTo(1);
        await Assert.That(error.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(error.InnerException!.Message).Contains("capacity cannot change while permits are active");

        await first.Permit.ReleaseAsync();
        await Assert.That(async () => await changed.TryAcquireAsync()).Throws<RespireSemaphoreCapacityMismatchException>();
        await second.Permit.ReleaseAsync();
        await using var afterDrain = await changed.TryAcquireAsync();
        await Assert.That(afterDrain.Acquired).IsTrue();
        await using (var atCapacity = await changed.TryAcquireAsync())
            await Assert.That(atCapacity.Acquired).IsFalse();
    }

    [Test]
    public async Task OptionalExpiryCanRenewAndCannotReleaseReplacementOwner()
    {
        await using var client = await ConnectAsync(3);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore-expiry";
        var semaphore = new RespireSemaphore(client, key, capacity: 1);
        await using var permit = await semaphore.TryAcquireAsync();
        await Assert.That(permit.Permit.Expiry).IsNull();
        await Assert.That(permit.Permit.RemainingEstimate).IsNull();
        await Assert.That(await permit.Permit.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await permit.Permit.ResetExpiryAsync(TimeSpan.FromMilliseconds(180))).IsTrue();
        await Assert.That(permit.Permit.Expiry).IsEqualTo(TimeSpan.FromMilliseconds(180));
        await Task.Delay(280);

        await using var replacement = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(20));
        await Assert.That(replacement.Acquired).IsTrue();
        await Assert.That(await permit.Permit.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsFalse();
        await Assert.That(await permit.Permit.ReleaseAsync()).IsFalse();
        await Assert.That(await replacement.Permit.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await replacement.Permit.ResetExpiryAsync(null)).IsTrue();
        await Assert.That(replacement.Permit.RemainingEstimate).IsNull();
    }

    [Test]
    public async Task BinaryPrefixedKeyIsSnapshottedAndCapacityValidated()
    {
        await using var client = await ConnectAsync(3);
        var view = client.WithKeyPrefix($"semaphore:{Guid.NewGuid():N}:");
        byte[] key = [0xff, 0, 1];
        var semaphore = new RespireSemaphore(view, key, capacity: 1);
        key[2] = 2;
        await using var permit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(20));
        await Assert.That(permit.Acquired).IsTrue();
        await using (var blocked = await new RespireSemaphore(view, new byte[] { 0xff, 0, 1 }, capacity: 1).TryAcquireAsync())
            await Assert.That(blocked.Acquired).IsFalse();
        await Assert.That(() => new RespireSemaphore(client, "invalid", 0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task KeyTtlTracksLatestPermitAndPersistsForOwnerReleasedPermits()
    {
        await using var client = await ConnectAsync(3);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore-ttl";
        var semaphore = new RespireSemaphore(client, key, capacity: 3);
        await using var shortPermit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(10));
        await using var longPermit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(60));
        var ttl = await PttlAsync(client, key);
        await Assert.That(ttl).IsGreaterThan(30_000).And.IsLessThanOrEqualTo(60_000);

        await using var persistent = await semaphore.TryAcquireAsync();
        await Assert.That(await PttlAsync(client, key)).IsEqualTo(-1);
        await Assert.That(await persistent.Permit.ReleaseAsync()).IsTrue();
        ttl = await PttlAsync(client, key);
        await Assert.That(ttl).IsGreaterThan(30_000).And.IsLessThanOrEqualTo(60_000);

        await Assert.That(await longPermit.Permit.ReleaseAsync()).IsTrue();
        await Assert.That(await PttlAsync(client, key)).IsLessThanOrEqualTo(10_000);
        await Assert.That(await shortPermit.Permit.ReleaseAsync()).IsTrue();
        await Assert.That(await PttlAsync(client, key)).IsEqualTo(-2);
    }

    [Test]
    public async Task CanceledVerificationKeepsPermit()
    {
        await using var client = await ConnectAsync(3);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore-verify";
        var semaphore = new RespireSemaphore(client, key, capacity: 1);
        await using var permit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(20));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await permit.Permit.VerifyStillHeldAsync(canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(permit.Permit.IsReleased).IsFalse();
        await Assert.That(await permit.Permit.VerifyStillHeldAsync()).IsTrue();
        await using (var blocked = await semaphore.TryAcquireAsync())
            await Assert.That(blocked.Acquired).IsFalse();
    }

    [Test]
    public async Task MaximumExpiryDoesNotOverflowLocalEstimate()
    {
        await using var client = await ConnectAsync(3);
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore-max-expiry";
        var semaphore = new RespireSemaphore(client, key, capacity: 1);
        await using var permit = await semaphore.TryAcquireAsync(TimeSpan.MaxValue);
        await Assert.That(permit.Acquired).IsTrue();
        await Assert.That(permit.Permit.RemainingEstimate!.Value).IsGreaterThan(TimeSpan.FromDays(365 * 100));
        await Assert.That(await permit.Permit.ResetExpiryAsync(TimeSpan.MaxValue)).IsTrue();
        await Assert.That(await permit.Permit.ReleaseAsync()).IsTrue();
    }

    private static async Task<long> PttlAsync(RespireClient client, RespireKey key)
    {
        using var ttl = await client.ExecuteAsync(RespireCommands.Key.PTTL, (RespireValue)key);
        return ttl.AsInteger();
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
        Connections = 1, Protocol = (RespProtocol)protocol,
    });
}
