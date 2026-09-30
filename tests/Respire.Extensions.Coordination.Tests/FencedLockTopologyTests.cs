using Respire.Testing.Containers;
using Respire.Commands;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class FencedLockTopologyTests
{
    [Test]
    public async Task ClusterAcquireWaitsForSlotOwnerInvalidation()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            Connections = 1,
            ClientSideCache = new(),
        });
        var coordination = new RespireCoordination(client.WithKeyPrefix("coordination:"));
        await using var owner = await coordination.TryAcquireFencedLockAsync("{wait}:lease", "{wait}:counter", TimeSpan.FromSeconds(20));

        var waiting = coordination.AcquireFencedLockAsync("{wait}:lease", "{wait}:counter", TimeSpan.FromSeconds(20)).AsTask();
        await Task.Delay(100);
        await Assert.That(waiting.IsCompleted).IsFalse();

        await Assert.That(await owner.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(acquired.FencingToken).IsEqualTo(owner.Lock.FencingToken + 1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClusterSupportsSameSlotKeysAndRejectsCrossSlotPairs(int protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Protocol = (RespProtocol)protocol, Connections = 1 });
        var view = client.WithKeyPrefix("coordination:");
        var coordination = new RespireCoordination(view);
        await using var attempt = await coordination.TryAcquireFencedLockAsync("{job}:lease", "{job}:counter", TimeSpan.FromSeconds(30));
        await Assert.That(attempt.Lock.FencingToken).IsEqualTo(1);
        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await using var next = await coordination.TryAcquireFencedLockAsync("{job}:lease", "{job}:counter", TimeSpan.FromSeconds(30));
        await Assert.That(next.Lock.FencingToken).IsEqualTo(2);

        var readKey = (RespireKey)"{job}:rw";
        await using var firstReader = await coordination.TryAcquireReadLockAsync(readKey, TimeSpan.FromSeconds(30));
        await using var secondReader = await coordination.TryAcquireReadLockAsync(readKey, TimeSpan.FromSeconds(30));
        await Assert.That(firstReader.Acquired && secondReader.Acquired).IsTrue();
        await using (var rejectedWriter = await coordination.TryAcquireWriteLockAsync(readKey, TimeSpan.FromSeconds(30)))
            await Assert.That(rejectedWriter.Acquired).IsFalse();
        await firstReader.Lock.DisposeAsync();
        await secondReader.Lock.DisposeAsync();
        await using var writer = await coordination.TryAcquireWriteLockAsync(readKey, TimeSpan.FromSeconds(30));
        await Assert.That(writer.Acquired).IsTrue();
        await using (var rejectedReader = await coordination.TryAcquireReadLockAsync(readKey, TimeSpan.FromSeconds(30)))
            await Assert.That(rejectedReader.Acquired).IsFalse();

        var semaphore = new RespireSemaphore(view, "{job}:semaphore", capacity: 2);
        await using var firstPermit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30));
        await using var secondPermit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30));
        await Assert.That(firstPermit.Acquired && secondPermit.Acquired).IsTrue();
        await using (var atCapacity = await semaphore.TryAcquireAsync())
            await Assert.That(atCapacity.Acquired).IsFalse();
        await Assert.That(await firstPermit.Permit.ReleaseAsync()).IsTrue();
        var crossSlot = await Assert.That(async () => await coordination.TryAcquireFencedLockAsync("{first}:lease", "{second}:counter", TimeSpan.FromSeconds(30)))
            .Throws<RespireServerException>();
        await Assert.That(crossSlot!.Code).IsEqualTo("CROSSSLOT");
        await Assert.That(await view.GetBytesAsync("{first}:lease")).IsNull();
        await Assert.That(await view.GetBytesAsync("{second}:counter")).IsNull();
    }

    [Test]
    public async Task PromotedReplicaContinuesAnExplicitlyReplicatedCounterHistory()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Sentinel });
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], Connections = 1, AllowAdmin = true,
        });
        await using var old = await new RespireCoordination(primary).TryAcquireFencedLockAsync("lease", "counter", TimeSpan.FromSeconds(2));
        // This fixture deliberately uses one physical connection so WAIT acknowledges
        // the preceding acquisition. Raw ExecuteAsync correctly rejects affinity commands.
        using (var replicated = await primary.Core.Multiplexer.GetConnection().SendAsync(
            new Cmd2(new Verb(-1, "WAIT"), 1, 5000), default))
            await Assert.That(replicated.AsInteger()).IsEqualTo(1);
        await using var promoted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]], Connections = 1, AllowAdmin = true,
        });
        using (var promotion = await promoted.ExecuteAsync(RespireCommands.Server.REPLICAOF, "NO", "ONE"))
            await Assert.That(promotion.AsString()).IsEqualTo("OK");
        await Task.Delay(old.Lock.RemainingEstimate + TimeSpan.FromMilliseconds(100));
        await using var current = await new RespireCoordination(promoted).TryAcquireFencedLockAsync("lease", "counter", TimeSpan.FromSeconds(20));
        await Assert.That(current.Lock.FencingToken).IsEqualTo(old.Lock.FencingToken + 1);
        await Assert.That(await old.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsFalse();
        await Assert.That(await current.Lock.VerifyStillHeldAsync()).IsTrue();
    }

    [Test]
    public async Task ReplicatedReadLeaseStillBlocksWriterAfterReplicaPromotion()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Sentinel });
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], Connections = 1, AllowAdmin = true,
        });
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:rw";
        await using var reader = await new RespireCoordination(primary).TryAcquireReadLockAsync(key, TimeSpan.FromSeconds(30));
        await Assert.That(reader.Acquired).IsTrue();

        using (var replicated = await primary.Core.Multiplexer.GetConnection().SendAsync(
            new Cmd2(new Verb(-1, "WAIT"), 1, 5000), default))
            await Assert.That(replicated.AsInteger()).IsEqualTo(1);

        await using var promoted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]], Connections = 1, AllowAdmin = true,
        });
        using (var promotion = await promoted.ExecuteAsync(RespireCommands.Server.REPLICAOF, "NO", "ONE"))
            await Assert.That(promotion.AsString()).IsEqualTo("OK");

        await using var blockedWriter = await new RespireCoordination(promoted).TryAcquireWriteLockAsync(key, TimeSpan.FromSeconds(30));
        await Assert.That(blockedWriter.Acquired).IsFalse();
        await Assert.That(await reader.Lock.ReleaseAsync()).IsTrue();
    }

    [Test]
    public async Task ReplicatedSemaphorePermitStillConsumesCapacityAfterReplicaPromotion()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Sentinel });
        await using var primary = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]], Connections = 1, AllowAdmin = true,
        });
        var key = (RespireKey)$"{{{Guid.NewGuid():N}}}:semaphore";
        var semaphore = new RespireSemaphore(primary, key, capacity: 1);
        await using var permit = await semaphore.TryAcquireAsync(TimeSpan.FromSeconds(30));
        await Assert.That(permit.Acquired).IsTrue();

        using (var replicated = await primary.Core.Multiplexer.GetConnection().SendAsync(
            new Cmd2(new Verb(-1, "WAIT"), 1, 5000), default))
            await Assert.That(replicated.AsInteger()).IsEqualTo(1);

        await using var promoted = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]], Connections = 1, AllowAdmin = true,
        });
        using (var promotion = await promoted.ExecuteAsync(RespireCommands.Server.REPLICAOF, "NO", "ONE"))
            await Assert.That(promotion.AsString()).IsEqualTo("OK");

        await using var blocked = await new RespireSemaphore(promoted, key, capacity: 1).TryAcquireAsync();
        await Assert.That(blocked.Acquired).IsFalse();
        await Assert.That(await permit.Permit.ReleaseAsync()).IsTrue();
    }
}
