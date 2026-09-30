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
}
