using System.Net.Sockets;
using FluentAssertions;
using Respire.Internal;
using Respire.Testing.Containers;
using TUnit.Core;
using static Respire.IntegrationTests.StandaloneCommandRetryIntegrationTests;
using static Respire.IntegrationTests.StandaloneUnitRetryIntegrationTests;

namespace Respire.IntegrationTests;

// Whole-unit retries across real topology changes. The executed unit's reply is lost while
// Sentinel promotes a replica or a Cluster slot moves, and nodes are then read directly.
[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class TopologyUnitRetryIntegrationTests
{
    [Test]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task LostUnitReplyAcrossRealPromotionPreservesReplicatedEffectAndExplicitRisk(string unit, bool allowDuplicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Sentinel,
            SentinelDownAfter = TimeSpan.FromSeconds(3), SentinelFailoverTimeout = TimeSpan.FromSeconds(3),
        }, deadline.Token);
        var former = fixture.DataEndpoints[0];
        var promoted = fixture.DataEndpoints[1];
        await using var observer = await RespireClient.ConnectAsync(DirectOptions(former), deadline.Token);
        await using var replica = await RespireClient.ConnectAsync(DirectOptions(promoted) with
        { RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxAttempts = 10 } }, deadline.Token);
        var key = "unit-retry-promotion:" + Guid.NewGuid().ToString("N");
        var fault = new LostReply(UnitVerb(unit));
        fault.BeforeDrop = async _ =>
        {
            var token = deadline.Token;
            await WaitForValueAsync(observer, key, "1", token);
            // WAIT acknowledges a marker written after the complete unit, so the replica
            // that Sentinel promotes holds exactly one effect before the primary stops.
            await observer.SetAsync(key + ":barrier", "replicated", cancellationToken: token);
            using (var replicated = await observer.ExecuteAsync("WAIT", ["1", "5000"], cancellationToken: token))
                replicated.AsInteger().Should().Be(1);
            (await replica.GetStringAsync(key, token)).Should().Be("1");
            await fixture.StopDataNodeAsync(0, token);
            while (true)
            {
                using var role = await replica.ExecuteAsync(RespireCommands.Server.ROLE, [], cancellationToken: token);
                if (role[0].AsString() == "master") break;
                await Task.Delay(100, token);
            }
            foreach (var endpoint in fixture.SentinelEndpoints)
            {
                await using var sentinel = await RespireClient.ConnectAsync(DirectOptions(endpoint), token);
                while (true)
                {
                    using var discovered = await sentinel.ExecuteAsync("SENTINEL GET-MASTER-ADDR-BY-NAME",
                        [RespireContainerFixture.SentinelServiceName], cancellationToken: token);
                    if (int.Parse(discovered[1].AsString()!) == promoted.Port) break;
                    await Task.Delay(100, token);
                }
            }
        };
        await using var client = await RespireClient.ConnectAsync(
            WithFault(fixture.CreateOptions(), fault) with { RetryPolicy = Policy(allowDuplicate) }, deadline.Token);
        fault.Armed = true;

        var outcome = await RunUnitAsync(client, unit, key, deadline.Token);

        AssertOutcome(outcome, allowDuplicate);
        fault.Dropped.Should().Be(1);
        fault.Sends.Should().Be(allowDuplicate ? 2 : 1);
        await fault.RouteReady.Task.WaitAsync(deadline.Token);
        // The old primary is stopped. Read the promoted node directly, with retry disabled.
        await using var promotedObserver = await RespireClient.ConnectAsync(DirectOptions(promoted), deadline.Token);
        (await promotedObserver.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        if (allowDuplicate) client.Endpoint.Should().Be(promoted);
    }

    [Test]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task LostUnitReplyAcrossRealClusterSlotMovePreservesEffectAndExplicitRisk(string unit, bool allowDuplicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster }, deadline.Token);
        var key = "unit-retry-slot:" + Guid.NewGuid().ToString("N");
        var slot = ClusterHash.GetSlot(key);
        var fault = new LostReply(UnitVerb(unit));
        RespireEndpoint replacement = default;
        RespireEndpoint former = default;
        fault.BeforeDrop = async token =>
        {
            var source = fault.Endpoint;
            former = source;
            replacement = fixture.DataEndpoints.First(endpoint => endpoint != source);
            await using var oldNode = await RespireClient.ConnectAsync(DirectOptions(source), token);
            await using var newNode = await RespireClient.ConnectAsync(DirectOptions(replacement), token);
            // Move the slot only after the complete unit applied its single effect.
            await WaitForValueAsync(oldNode, key, "1", token);
            using var sourceId = await oldNode.ExecuteAsync("CLUSTER MYID", [], cancellationToken: token);
            using var targetId = await newNode.ExecuteAsync("CLUSTER MYID", [], cancellationToken: token);
            using (await newNode.ExecuteAsync("CLUSTER SETSLOT", [slot, "IMPORTING", sourceId.AsString()!], cancellationToken: token)) { }
            using (await oldNode.ExecuteAsync("CLUSTER SETSLOT", [slot, "MIGRATING", targetId.AsString()!], cancellationToken: token)) { }
            using (await oldNode.ExecuteAsync("MIGRATE", [replacement.Host, replacement.Port, key, "0", "5000"], cancellationToken: token)) { }
            // Commit ownership on the destination before notifying the source and peers.
            foreach (var endpoint in new[] { replacement, source }.Concat(fixture.DataEndpoints.Where(endpoint => endpoint != replacement && endpoint != source)))
            {
                await using var node = await RespireClient.ConnectAsync(DirectOptions(endpoint), token);
                using var moved = await node.ExecuteAsync("CLUSTER SETSLOT", [slot, "NODE", targetId.AsString()!], cancellationToken: token);
                moved.AsString().Should().Be("OK");
            }
            (await newNode.GetStringAsync(key, token)).Should().Be("1");
        };
        await using var client = await RespireClient.ConnectAsync(
            WithFault(fixture.CreateOptions(), fault) with { RetryPolicy = Policy(allowDuplicate) }, deadline.Token);
        fault.Armed = true;

        var outcome = await RunUnitAsync(client, unit, key, deadline.Token);

        AssertOutcome(outcome, allowDuplicate);
        fault.Dropped.Should().Be(1);
        // Surface migration failures instead of mistaking them for the injected reply loss.
        await fault.RouteReady.Task.WaitAsync(deadline.Token);
        await using var observer = await RespireClient.ConnectAsync(DirectOptions(replacement), deadline.Token);
        (await observer.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        await using var formerObserver = await RespireClient.ConnectAsync(DirectOptions(former), deadline.Token);
        using (var keysOnFormer = await formerObserver.ExecuteAsync("CLUSTER GETKEYSINSLOT", [slot, "10"], cancellationToken: deadline.Token))
            keysOnFormer.Count.Should().Be(0);
        // A later routed read must agree with the independent node observer.
        (await client.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
    }

    private static RespireOptions DirectOptions(RespireEndpoint endpoint) => new()
    {
        Endpoints = [endpoint], Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
        Connections = 1, CommandTimeout = TimeSpan.FromSeconds(30), ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static RespireOptions WithFault(RespireOptions options, LostReply fault) => options with
    {
        Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false, Connections = 1, CommandTimeout = TimeSpan.FromSeconds(30),
        TestingStreamFactory = async (host, port, token) =>
        {
            // Reconnect only after the test finished moving the topology.
            if (Volatile.Read(ref fault.Dropped) != 0 && fault.BeforeDrop is not null)
                await fault.RouteReady.Task.WaitAsync(token);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new DropReplyStream(new NetworkStream(socket, ownsSocket: true), fault, new(host, port));
            }
            catch { socket.Dispose(); throw; }
        },
    };
}
