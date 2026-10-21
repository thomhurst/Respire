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

    [Test]
    [Arguments(2, "hset")]
    [Arguments(3, "hset")]
    [Arguments(2, "pexpire")]
    [Arguments(3, "pexpire")]
    public async Task AcquisitionPermissionFailureLeavesNoClaimAndCanBeRetried(int protocol, string deniedCommand)
    {
        await using var admin = await ConnectAsync(protocol, allowAdmin: true);
        var username = $"idempotency-{Guid.NewGuid():N}";
        const string password = "idempotency-test-password";
        await admin.Server.AclSetUserAsync(username, ["reset", "on", $">{password}", "~*", "+@all", "-acl", $"-{deniedCommand}"]);
        try
        {
            await using var restricted = await ConnectAsync(protocol, username, password);
            var store = new RespireIdempotency(restricted);
            var key = Guid.NewGuid().ToString("N");
            var error = await Assert.That(async () => await store.TryBeginAsync(key, Ttl, "request"))
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("NOPERM");
            using (var exists = await admin.ExecuteAsync("EXISTS", key))
                await Assert.That(exists.AsInteger()).IsEqualTo(0L);
            await Assert.That(await TtlMilliseconds(admin, key)).IsEqualTo(-2L);

            await admin.Server.AclSetUserAsync(username, [$"+{deniedCommand}"]);
            var owner = await store.TryBeginAsync(key, Ttl, "request");
            await Assert.That(owner.State).IsEqualTo(RespireIdempotencyState.Started);
            await Assert.That(owner.OwnerToken).IsNotNull();
            var ttl = await TtlMilliseconds(admin, key);
            await Assert.That(ttl > 0 && ttl <= Ttl.TotalMilliseconds).IsTrue();
        }
        finally
        {
            await admin.Server.AclDeleteUsersAsync([username]);
        }
    }

    [Test]
    [Arguments(2, "hset")]
    [Arguments(3, "hset")]
    [Arguments(2, "hdel")]
    [Arguments(3, "hdel")]
    [Arguments(2, "pexpire")]
    [Arguments(3, "pexpire")]
    public async Task CompletionPermissionFailurePreservesClaimAndCanBeRetried(int protocol, string deniedCommand)
    {
        await using var admin = await ConnectAsync(protocol, allowAdmin: true);
        var username = $"idempotency-{Guid.NewGuid():N}";
        const string password = "idempotency-test-password";
        await admin.Server.AclSetUserAsync(username, ["reset", "on", $">{password}", "~*", "+@all", "-acl", $"-{deniedCommand}"]);
        try
        {
            await using var restricted = await ConnectAsync(protocol, username, password);
            var store = new RespireIdempotency(restricted);
            var key = Guid.NewGuid().ToString("N");
            var owner = await new RespireIdempotency(admin).TryBeginAsync(key, TimeSpan.FromMinutes(10), "request");
            await admin.Keys.ExpireAsync(key, RespireExpiry.In(Ttl));
            var expiry = await ExpiryMilliseconds(admin, key);
            // Compare the complete stored hash, including the absent result, without relying on field order.
            var before = await ClaimFields(admin, key);
            byte[] payload = [0, 255, 128, 13, 10];

            await Assert.That(await store.CompleteAsync(key, "wrong-owner", payload)).IsFalse();
            var error = await Assert.That(async () => await store.CompleteAsync(key, owner.OwnerToken!, payload))
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("NOPERM");
            await Assert.That((await ClaimFields(admin, key)).SequenceEqual(before)).IsTrue();
            await Assert.That(await ExpiryMilliseconds(admin, key)).IsEqualTo(expiry);
            var pending = await store.TryBeginAsync(key, Ttl, "request");
            await Assert.That(pending.State).IsEqualTo(RespireIdempotencyState.InProgress);
            await Assert.That(pending.OwnerToken is null && pending.Result.IsEmpty).IsTrue();
            await Assert.That((await store.TryBeginAsync(key, Ttl, "different")).State)
                .IsEqualTo(RespireIdempotencyState.FingerprintMismatch);

            await admin.Server.AclSetUserAsync(username, [$"+{deniedCommand}"]);
            await Assert.That(await store.CompleteAsync(key, owner.OwnerToken!, payload)).IsTrue();
            using (var hasOwner = await admin.ExecuteAsync("HEXISTS", key, "owner"))
                await Assert.That(hasOwner.AsInteger()).IsEqualTo(0L);
            var ttl = await TtlMilliseconds(admin, key);
            await Assert.That(ttl > Ttl.TotalMilliseconds && ttl <= 600_000).IsTrue();

            // Read-only outcomes and stale-owner checks must not require mutation permissions.
            await admin.Server.AclSetUserAsync(username, ["-hset", "-hdel", "-pexpire"]);
            var replay = await store.TryBeginAsync(key, Ttl, "request");
            await Assert.That(replay.State).IsEqualTo(RespireIdempotencyState.Completed);
            await Assert.That(replay.Result.ToArray().SequenceEqual(payload)).IsTrue();
            await Assert.That((await store.TryBeginAsync(key, Ttl, "different")).State)
                .IsEqualTo(RespireIdempotencyState.FingerprintMismatch);
            await Assert.That(await store.CompleteAsync(key, owner.OwnerToken!, ReadOnlyMemory<byte>.Empty)).IsFalse();
            await Assert.That(await store.AbandonAsync(key, owner.OwnerToken!)).IsFalse();
        }
        finally
        {
            await admin.Server.AclDeleteUsersAsync([username]);
        }
    }

    private static async Task<string?[]> ClaimFields(IRespireClient client, RespireKey key)
    {
        using var result = await client.ExecuteAsync("HMGET", (RespireValue)key, "state", "owner", "fingerprint", "ttl", "result");
        return Enumerable.Range(0, result.Count).Select(i => result[i].IsNull ? null : result[i].AsString()).ToArray();
    }

    private static async Task<long> ExpiryMilliseconds(IRespireClient client, RespireKey key)
    {
        using var result = await client.ExecuteAsync("PEXPIRETIME", (RespireValue)key);
        return result.AsInteger();
    }

    private async Task<RespireClient> ConnectAsync(int protocol, string? username = null, string? password = null,
        bool allowAdmin = false)
        => await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Connections = 1, Protocol = protocol == 2 ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
            Username = username, Password = password, AllowAdmin = allowAdmin,
        });

    private static async Task<long> TtlMilliseconds(IRespireClient client, RespireKey key)
    {
        using var result = await client.ExecuteAsync(RespireCommands.Key.PTTL, (RespireValue)key);
        return result.AsInteger();
    }
}
