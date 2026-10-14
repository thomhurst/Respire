using System.Net.Sockets;
using FluentAssertions;
using Respire.Internal;
using Respire.Testing.Containers;
using TUnit.Core;
using static Respire.IntegrationTests.StandaloneCommandRetryIntegrationTests;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class TopologyCommandRetryIntegrationTests
{
    [Test]
    [Arguments("Sentinel", "typed", false)]
    [Arguments("Sentinel", "typed", true)]
    [Arguments("Sentinel", "raw", false)]
    [Arguments("Sentinel", "raw", true)]
    [Arguments("Sentinel", "script", false)]
    [Arguments("Sentinel", "script", true)]
    [Arguments("Sentinel", "function", false)]
    [Arguments("Sentinel", "function", true)]
    [Arguments("group", "typed", false)]
    [Arguments("group", "typed", true)]
    public async Task LostReplyAcrossRealPromotionPreservesReplicatedEffectAndExplicitRisk(string topology, string shape, bool allowDuplicate)
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
        var key = "retry-promotion:" + Guid.NewGuid().ToString("N");
        await observer.SetAsync(key, "0", cancellationToken: deadline.Token);
        var script = RespireScript.Create("return redis.call('INCR', KEYS[1])")
            .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating);
        await observer.Scripts.LoadAsync(script, deadline.Token);
        var library = RespireFunctionLibrary.Create("""
            #!lua name=topology_retry
            redis.register_function('topology_increment', function(keys,args) return redis.call('INCR',keys[1]) end)
            """, replace: true);
        await observer.Functions.LoadAsync(library, deadline.Token);
        var function = library.Function("topology_increment").WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating);
        var fault = new LostReply(shape switch { "script" => "EVALSHA", "function" => "FCALL", _ => "INCR" });
        fault.BeforeDrop = async _ =>
        {
            var token = deadline.Token;
            // This connection writes a marker after the lost command has executed. WAIT
            // acknowledges that marker, hence the preceding counter and loaded code too.
            await observer.SetAsync(key + ":barrier", "replicated", cancellationToken: token);
            using var replicated = await observer.ExecuteAsync("WAIT", ["1", "5000"], cancellationToken: token);
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
        var policy = new RespireRetryPolicy
        {
            InitialBackoff = TimeSpan.Zero,
            MaxCommandRetryCategory = allowDuplicate ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly,
        };
        var options = WithFault(fixture.CreateOptions(), fault) with { RetryPolicy = policy };
        await using var client = topology == "Sentinel" ? await RespireClient.ConnectAsync(options, deadline.Token) : null;
        await using var group = topology == "group" ? await RespireFailoverGroup.ConnectAsync(
            [new(WithFault(DirectOptions(former), fault), 0), new(DirectOptions(promoted), 1)],
            new() { RetryPolicy = policy, ProbeInterval = TimeSpan.FromHours(1), ProbeTimeout = TimeSpan.FromSeconds(2),
                FailureThreshold = 1, CircuitOpenDuration = TimeSpan.FromMinutes(1) }, deadline.Token) : null;
        fault.Armed = true;
        async ValueTask<long> Increment(IRespireClient member, CancellationToken token)
        {
            if (shape == "script") return await member.Scripts.ExecuteIntegerAsync(script, [key], cancellationToken: token);
            if (shape == "function") return await member.Functions.ExecuteIntegerAsync(function, [key], cancellationToken: token);
            if (shape == "raw")
            {
                using var result = await member.ExecuteAsync(RespireCommand.Create("INCR").WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating),
                    [key], cancellationToken: token);
                return result.AsInteger();
            }
            return await member.IncrementAsync(key, cancellationToken: token);
        }
        Exception? failure = null;
        try
        {
            var result = group is null ? await Increment(client!, deadline.Token) : await group.ExecuteAsync(async (member, token) =>
            {
                try { return await Increment(member, token); }
                catch (RespireConnectionException)
                {
                    await fault.RouteReady.Task.WaitAsync(token);
                    await group!.ForTests.ProbeAsync(0, token);
                    await group.ForTests.SelectActiveAsync();
                    // Sentinel kills old replica client sockets during promotion. Select
                    // the group member, then admit its retry only after reconnect completes.
                    await ((RespireClient)group.ActiveClient).Core.Multiplexer.GetHealthyConnectionAsync(token);
                    throw;
                }
            }, RespireCommandRetryCategory.WriteAccumulating, deadline.Token);
            result.Should().Be(2);
        }
        catch (Exception error) { failure = error; }
        if (allowDuplicate) failure.Should().BeNull();
        else failure.Should().BeAssignableTo<RespireConnectionException>();
        fault.Dropped.Should().Be(1);
        // The old primary is stopped. Observe the actual promoted Redis directly, with
        // retry disabled, rather than deriving its value from fault counters.
        await fault.RouteReady.Task.WaitAsync(deadline.Token);
        await using var promotedObserver = await RespireClient.ConnectAsync(DirectOptions(promoted), deadline.Token);
        (await promotedObserver.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        if (allowDuplicate) (group?.ActiveClient.Endpoint ?? client!.Endpoint).Should().Be(promoted);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostReplyAcrossRealClusterSlotMovePreservesEffectAndExplicitRisk(bool allowDuplicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster }, deadline.Token);
        var key = "retry-slot:" + Guid.NewGuid().ToString("N");
        var slot = ClusterHash.GetSlot(key);
        var fault = new LostReply("INCR");
        RespireEndpoint replacement = default;
        RespireEndpoint former = default;
        fault.BeforeDrop = async token =>
        {
            var source = fault.Endpoint;
            former = source;
            replacement = fixture.DataEndpoints.First(endpoint => endpoint != source);
            await using var oldNode = await RespireClient.ConnectAsync(DirectOptions(source), token);
            await using var newNode = await RespireClient.ConnectAsync(DirectOptions(replacement), token);
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
        await using var client = await RespireClient.ConnectAsync(WithFault(fixture.CreateOptions(), fault) with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = allowDuplicate ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly },
        }, deadline.Token);
        fault.Armed = true;
        Exception? failure = null;
        try { (await client.IncrementAsync(key, cancellationToken: deadline.Token)).Should().Be(2); }
        catch (Exception error) { failure = error; }
        if (allowDuplicate) failure.Should().BeNull();
        else failure.Should().BeAssignableTo<RespireConnectionException>();
        fault.Dropped.Should().Be(1);
        // Surface migration failures instead of mistaking them for the injected reply loss.
        await fault.RouteReady.Task.WaitAsync(deadline.Token);
        await using var observer = await RespireClient.ConnectAsync(DirectOptions(replacement), deadline.Token);
        (await observer.GetStringAsync(key, deadline.Token)).Should().Be(allowDuplicate ? "2" : "1");
        await using var formerObserver = await RespireClient.ConnectAsync(DirectOptions(former), deadline.Token);
        using var keysOnFormer = await formerObserver.ExecuteAsync("CLUSTER GETKEYSINSLOT", [slot, "10"], cancellationToken: deadline.Token);
        keysOnFormer.Count.Should().Be(0);
        // A subsequent routed public read must agree with the independent node observer.
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
