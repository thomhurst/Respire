using Respire.Commands;
using Respire.Protocol;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class HashFieldLeaseTests
{
    [Test]
    [NotInParallel]
    public async Task BinaryFieldLeaseExpiresIndependentlyAndRejectsStaleOwner()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            Connections = 1,
            ClientSideCache = new(),
        });
        var view = client.WithKeyPrefix("tenant:");
        var coordination = new RespireCoordination(view);
        byte[] hashBytes = [0xff, 0, 1];
        byte[] fieldBytes = [0xff, 0, 2];
        byte[] otherFieldBytes = [0xff, 0, 3];
        RespireKey hash = hashBytes;
        RespireKey field = fieldBytes;
        RespireKey otherField = otherFieldBytes;

        await using var oldOwner = await coordination.TryAcquireLeaseAsync(hash, field, TimeSpan.FromSeconds(2))
            ?? throw new InvalidOperationException("Expected first lease acquisition.");
        await Assert.That(await view.Hashes.GetBytesAsync(hash, field)).IsEquivalentTo(oldOwner.OwnerToken.Bytes.ToArray());
        await using var independent = await coordination.TryAcquireLeaseAsync(hash, otherField, TimeSpan.FromSeconds(10))
            ?? throw new InvalidOperationException("Expected independent field lease acquisition.");
        await Assert.That((await view.Hashes.ExpiryAsync(hash, otherField)).HasExpiry).IsTrue();

        var waiting = coordination.AcquireLeaseAsync(hash, field, TimeSpan.FromSeconds(10)).AsTask();
        await Task.Delay(100);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await using var replacement = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(replacement.OwnerToken == oldOwner.OwnerToken).IsFalse();
        await Assert.That(await oldOwner.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsFalse();
        await Assert.That(await oldOwner.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.NotOwned);
        await Assert.That(await independent.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await replacement.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await independent.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
    }

    [Test]
    public async Task OlderRedisFailsBeforeWritingAField()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.2-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Connections = 1 });
        var coordination = new RespireCoordination(client);

        var error = await Assert.That(async () => await coordination.TryAcquireLeaseAsync(
                "registry", "worker-1", TimeSpan.FromSeconds(1)))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("Redis 7.4+");
        await Assert.That(await client.Hashes.GetBytesAsync("registry", "worker-1")).IsNull();
    }

    [Test]
    public async Task OlderRedisReportsUnsupportedLeaseExpiryEvenWhenFieldIsOccupied()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.2-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Connections = 1 });
        await client.Hashes.SetAsync("registry", "worker-1", "another owner");
        var coordination = new RespireCoordination(client);

        var error = await Assert.That(async () => await coordination.TryAcquireLeaseAsync(
                "registry", "worker-1", TimeSpan.FromSeconds(1)))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("Redis 7.4+");
        await Assert.That(await client.Hashes.GetStringAsync("registry", "worker-1"))
            .IsEqualTo("another owner");
    }

    [Test]
    public async Task ExpiringHashKeyIsRejectedBeforeLeaseFieldIsWritten()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Connections = 1 });
        await client.Hashes.SetAsync("registry", "unrelated", "value");
        await client.Keys.ExpireAsync("registry", RespireExpiry.In(TimeSpan.FromSeconds(30)));
        var coordination = new RespireCoordination(client);

        var error = await Assert.That(async () => await coordination.TryAcquireLeaseAsync(
                "registry", "worker", TimeSpan.FromSeconds(5)))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("hash key without key expiration");
        await Assert.That(await client.Hashes.GetBytesAsync("registry", "worker")).IsNull();
    }

    [Test]
    public async Task LeaseDurationReportsWholeMillisecondsAppliedByRedis()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Connections = 1 });
        var coordination = new RespireCoordination(client);
        var requested = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * 1500 + 7);
        await using var lease = await coordination.TryAcquireLeaseAsync("registry", "worker", requested)
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        await Assert.That(lease.Duration).IsEqualTo(TimeSpan.FromMilliseconds(1500));
        await Assert.That(await lease.ResetExpiryAsync(requested)).IsTrue();
        await Assert.That(lease.Duration).IsEqualTo(TimeSpan.FromMilliseconds(1500));
    }

    [Test]
    public async Task EarlyReleaseWakesLeaseWaiterBeforeExpiry()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var ownerClient = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new(),
        });
        await using var waiterClient = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new(),
        });
        var ownerCoordination = new RespireCoordination(ownerClient);
        var waiterCoordination = new RespireCoordination(waiterClient);
        await using var owner = await ownerCoordination.TryAcquireLeaseAsync(
            "registry", "worker", TimeSpan.FromSeconds(20))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        var waiting = waiterCoordination.AcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(20)).AsTask();
        await Task.Delay(100);
        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(await owner.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await using var replacement = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(replacement.OwnerToken == owner.OwnerToken).IsFalse();
    }

    [Test]
    public async Task ClusterKeepsHashFieldLeaseOperationsOnThePrefixedSlotOwner()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster,
            Image = "redis:7.4-alpine",
        });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            Connections = 1,
        });
        var view = client.WithKeyPrefix("coordination:");
        var coordination = new RespireCoordination(view);
        await using var lease = await coordination.TryAcquireLeaseAsync(
            "{tenant}:leases", "{worker}:binary-field"u8.ToArray(), TimeSpan.FromSeconds(5))
            ?? throw new InvalidOperationException("Expected Cluster lease acquisition.");

        await Assert.That(await lease.VerifyStillHeldAsync()).IsTrue();
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(await view.Hashes.GetBytesAsync("{tenant}:leases", "{worker}:binary-field"u8.ToArray()))
            .IsEquivalentTo(lease.OwnerToken.Bytes.ToArray());
        await Assert.That(await lease.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
    }

    [Test]
    public async Task PromotedReplicaCanReacquireAfterReplicatedFieldExpiry()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Sentinel,
            Image = "redis:7.4-alpine",
        });
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]],
            Connections = 1,
            Protocol = RespProtocol.Resp3,
            AllowAdmin = true,
        });
        var coordination = new RespireCoordination(primary);
        await using var oldOwner = await coordination.TryAcquireLeaseAsync(
            "registry", "worker", TimeSpan.FromMilliseconds(500))
            ?? throw new InvalidOperationException("Expected primary lease acquisition.");

        using (var replicated = await primary.Core.Multiplexer.GetConnection().SendAsync(
                   new Cmd2(new Verb(-1, "WAIT"), 1, 5000), default))
            await Assert.That(replicated.AsInteger()).IsEqualTo(1);

        await using var promoted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]],
            Connections = 1,
            Protocol = RespProtocol.Resp3,
            AllowAdmin = true,
        });
        using (var promotion = await promoted.ExecuteAsync(RespireCommands.Server.REPLICAOF, "NO", "ONE"))
            await Assert.That(promotion.AsString()).IsEqualTo("OK");
        await Task.Delay(700);

        await using var replacementClient = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]],
            Connections = 1,
            Protocol = RespProtocol.Resp3,
        });
        await using var replacement = await new RespireCoordination(replacementClient)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(10))
            ?? throw new InvalidOperationException("Expected promoted node to reacquire expired field.");
        await Assert.That(replacement.OwnerToken == oldOwner.OwnerToken).IsFalse();
    }
}
