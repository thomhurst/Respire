using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class IdempotencyTests(RedisTestContainer fixture)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentClientsHaveOneOwnerAndReplayFirstBinaryResult(int protocol)
    {
        await using var first = await ConnectAsync(protocol);
        await using var second = await ConnectAsync(protocol);
        var stores = new[] { new RespireIdempotency(first), new RespireIdempotency(second) };
        var key = Guid.NewGuid().ToString("N");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
            stores[i % 2].TryBeginAsync(key, Ttl, "request-digest").AsTask()));
        var winner = attempts.Single(x => x.State == RespireIdempotencyState.Started);
        await Assert.That(attempts.Count(x => x.State == RespireIdempotencyState.InProgress)).IsEqualTo(31);
        await Assert.That(attempts.Where(x => x.State != RespireIdempotencyState.Started).All(x => x.OwnerToken is null)).IsTrue();
        await Assert.That(await stores[1].CompleteAsync(key, "wrong-owner", new byte[] { 1 })).IsFalse();
        byte[] bytes = [0, 255, 128, 13, 10];
        await Assert.That(await stores[0].CompleteAsync(key, winner.OwnerToken!, bytes)).IsTrue();
        await Assert.That(await stores[1].CompleteAsync(key, winner.OwnerToken!, new byte[] { 2 })).IsFalse();
        await Assert.That(await stores[0].AbandonAsync(key, winner.OwnerToken!)).IsFalse();
        var replay = await stores[1].TryBeginAsync(key, Ttl, "request-digest");
        await Assert.That(replay.State).IsEqualTo(RespireIdempotencyState.Completed);
        await Assert.That(replay.OwnerToken is null).IsTrue();
        await Assert.That(replay.Result.ToArray().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FingerprintMismatchDoesNotExposePendingOwnerOrCompletedResult(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var store = new RespireIdempotency(client);
        var key = Guid.NewGuid().ToString("N");
        var owner = await store.TryBeginAsync(key, Ttl);
        foreach (var completed in new[] { false, true })
        {
            if (completed) await store.CompleteAsync(key, owner.OwnerToken!, new byte[] { 42 });
            foreach (var fingerprint in new[] { "", "different" })
            {
                var mismatch = await store.TryBeginAsync(key, Ttl, fingerprint);
                await Assert.That(mismatch.State).IsEqualTo(RespireIdempotencyState.FingerprintMismatch);
                await Assert.That(mismatch.OwnerToken is null && mismatch.Result.IsEmpty).IsTrue();
            }
        }
    }

    [Test]
    public async Task ExpiredOwnerCannotCompleteOrAbandonReplacement()
    {
        await using var client = await ConnectAsync(3);
        var store = new RespireIdempotency(client);
        var key = Guid.NewGuid().ToString("N");
        var old = await store.TryBeginAsync(key, Ttl, "old");
        await Assert.That(await TtlMilliseconds(client, key) > 0).IsTrue();
        // Deterministically expire the crashed claim at Redis, without timing the acquisition round trip.
        await client.Keys.ExpireAsync(key, RespireExpiry.In(TimeSpan.Zero));
        var replacement = await store.TryBeginAsync(key, Ttl, "new");
        await Assert.That(replacement.State).IsEqualTo(RespireIdempotencyState.Started);
        await Assert.That(replacement.OwnerToken != old.OwnerToken).IsTrue();
        await Assert.That(await store.CompleteAsync(key, old.OwnerToken!, new byte[] { 99 })).IsFalse();
        await Assert.That(await store.AbandonAsync(key, old.OwnerToken!)).IsFalse();
        await Assert.That(await store.CompleteAsync(key, replacement.OwnerToken!, ReadOnlyMemory<byte>.Empty)).IsTrue();
        var replay = await store.TryBeginAsync(key, Ttl, "new");
        await Assert.That(replay.State).IsEqualTo(RespireIdempotencyState.Completed);
        await Assert.That(replay.Result.IsEmpty).IsTrue();
        await client.Keys.ExpireAsync(key, RespireExpiry.In(TimeSpan.Zero));
        await Assert.That((await store.TryBeginAsync(key, Ttl)).State).IsEqualTo(RespireIdempotencyState.Started);
    }

    [Test]
    public async Task CompletionRestartsOriginalTtlAndReadsDoNotExtendIt()
    {
        await using var client = await ConnectAsync(3);
        var store = new RespireIdempotency(client);
        var key = Guid.NewGuid().ToString("N");
        var owner = await store.TryBeginAsync(key, TimeSpan.FromMinutes(10));
        await client.Keys.ExpireAsync(key, RespireExpiry.In(TimeSpan.FromMinutes(1)));
        await store.TryBeginAsync(key, TimeSpan.FromHours(1));
        await Assert.That(await TtlMilliseconds(client, key) <= 60_000).IsTrue();
        await store.CompleteAsync(key, owner.OwnerToken!, ReadOnlyMemory<byte>.Empty);
        var completedTtl = await TtlMilliseconds(client, key);
        await Assert.That(completedTtl > 60_000 && completedTtl <= 600_000).IsTrue();
        await client.Keys.ExpireAsync(key, RespireExpiry.In(TimeSpan.FromMinutes(1)));
        await store.TryBeginAsync(key, TimeSpan.FromHours(1));
        await Assert.That(await TtlMilliseconds(client, key) <= 60_000).IsTrue();
    }

    [Test]
    public async Task AbandonRequiresOwnerAndAllowsImmediateRetry()
    {
        await using var client = await ConnectAsync(3);
        var store = new RespireIdempotency(client);
        var key = Guid.NewGuid().ToString("N");
        var owner = await store.TryBeginAsync(key, Ttl);
        await Assert.That(await store.AbandonAsync(key, "wrong")).IsFalse();
        await Assert.That(await store.AbandonAsync(key, owner.OwnerToken!)).IsTrue();
        await Assert.That(await store.AbandonAsync(key, owner.OwnerToken!)).IsFalse();
        await Assert.That(await store.CompleteAsync(key, owner.OwnerToken!, ReadOnlyMemory<byte>.Empty)).IsFalse();
        await Assert.That((await store.TryBeginAsync(key, Ttl, "replacement")).State).IsEqualTo(RespireIdempotencyState.Started);
    }

    [Test]
    public async Task PrefixedBinaryKeysAndResultsAreSnapshottedBeforeReturning()
    {
        await using var client = await ConnectAsync(3);
        var view = client.WithKeyPrefix($"idempotency:{Guid.NewGuid():N}:");
        var store = new RespireIdempotency(view);
        byte[] key = [0, 255, 1];
        var pending = store.TryBeginAsync(key, Ttl);
        key[2] = 2;
        var owner = await pending;
        key[2] = 1;
        byte[] bytes = [0, 255, 3];
        var completion = store.CompleteAsync(key, owner.OwnerToken!, bytes);
        key[2] = 2;
        bytes[2] = 4;
        await Assert.That(await completion).IsTrue();
        var replay = await store.TryBeginAsync(new byte[] { 0, 255, 1 }, Ttl);
        await Assert.That(replay.State).IsEqualTo(RespireIdempotencyState.Completed);
        await Assert.That(replay.Result.ToArray().SequenceEqual(new byte[] { 0, 255, 3 })).IsTrue();
        await Assert.That((await store.TryBeginAsync(key, Ttl)).State).IsEqualTo(RespireIdempotencyState.Started);
        await Assert.That((await new RespireIdempotency(client).TryBeginAsync(new byte[] { 0, 255, 1 }, Ttl)).State)
            .IsEqualTo(RespireIdempotencyState.Started);
    }

    private async Task<RespireClient> ConnectAsync(int protocol)
        => await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Connections = 1, Protocol = protocol == 2 ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
        });

    private static async Task<long> TtlMilliseconds(IRespireClient client, RespireKey key)
    {
        using var result = await client.ExecuteAsync(RespireCommands.Key.PTTL, (RespireValue)key);
        return result.AsInteger();
    }
}
