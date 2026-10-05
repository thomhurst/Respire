using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ReplicaReadRoutingIntegrationTests
{
    [Test]
    [NotInParallel]
    public void RouteListenerIgnoresUnrelatedCommandTraces()
    {
        using var scope = StartRoutingScope();
        var routes = new ConcurrentDictionary<string, ConcurrentQueue<RespireEndpoint>>(StringComparer.Ordinal);
        using var listener = Listen(routes, scope.TraceId);
        using var source = new ActivitySource("Respire");
        using (var command = source.StartActivity("SET"))
        {
            command!.SetTag("db.operation.name", "SET");
            command.SetTag("server.address", "127.0.0.1");
            command.SetTag("server.port", 1234);
        }
        var unrelated = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        using (var command = source.StartActivity("SET", ActivityKind.Client, unrelated))
        {
            command!.SetTag("db.operation.name", "SET");
            command.SetTag("server.address", "127.0.0.1");
            command.SetTag("server.port", 5678);
        }
        routes["SET"].Should().ContainSingle().Which.Should().Be(new RespireEndpoint("127.0.0.1", 1234));
    }

    [Test]
    [ParallelLimiter<DockerHeavy>]
    [Arguments(RespireContainerServer.Redis, true, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Redis, true, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Redis, false, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Redis, false, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Valkey, true, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Valkey, true, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Valkey, false, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Valkey, false, RespProtocol.Resp3)]
    public async Task NearestUsesEligibleEndpointsAndKeepsWritesOnPrimary(
        RespireContainerServer server, bool useSentinel, RespProtocol protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        var options = useSentinel ? fixture.CreateOptions() : new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]],
            ReplicaEndpoints = [fixture.DataEndpoints[1]],
        };
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = protocol, Connections = 1 });
        var routes = new ConcurrentDictionary<string, ConcurrentQueue<RespireEndpoint>>(StringComparer.Ordinal);
        using var scope = StartRoutingScope();
        using var listener = Listen(routes, scope.TraceId);
        var reader = client.WithReadFrom(RespireReadFrom.Nearest);
        await reader.SetAsync("nearest-key", "value");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await reader.GetStringAsync("nearest-key", deadline.Token) != "value")
            await Task.Delay(50, deadline.Token);
        routes["SET"].Last().Should().Be(fixture.DataEndpoints[0]);
        routes["GET"].Should().OnlyContain(endpoint => fixture.DataEndpoints.Contains(endpoint));
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), deadline.Token);
        (await reader.GetStringAsync("nearest-key", deadline.Token)).Should().Be("value");
    }

    [Test]
    [ParallelLimiter<DockerHeavy>]
    [Arguments(RespireContainerServer.Redis, true)]
    [Arguments(RespireContainerServer.Valkey, true)]
    [Arguments(RespireContainerServer.Redis, false)]
    [Arguments(RespireContainerServer.Valkey, false)]
    public async Task ReadViewRoutesCatalogReadsToValidatedReplicaAndKeepsWritesOnPrimary(
        RespireContainerServer server, bool useSentinel)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        var options = useSentinel
            ? fixture.CreateOptions()
            : new RespireOptions
            {
                Endpoints = [fixture.DataEndpoints[0]],
                ReplicaEndpoints = [fixture.DataEndpoints[1]],
            };
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new(),
        });
        var routes = new ConcurrentDictionary<string, ConcurrentQueue<RespireEndpoint>>(StringComparer.Ordinal);
        using var scope = StartRoutingScope();
        using var listener = Listen(routes, scope.TraceId);
        var prefixed = client.WithKeyPrefix("replica-read:");
        await prefixed.SetAsync("key", "value");
        (await prefixed.GetStringAsync("key")).Should().Be("value");
        var reader = client.WithReadFrom(RespireReadFrom.Replica).WithKeyPrefix("replica-read:");
        (await reader.GetStringAsync("key")).Should().Be("value");

        routes["SET"].Last().Should().Be(fixture.DataEndpoints[0]);
        routes["GET"].Last().Should().Be(fixture.DataEndpoints[1]);
    }

    [Test]
    [ParallelLimiter<DockerHeavy>]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task PrimaryPreferredUsesReplicaOnlyWhenPrimaryIsUnavailable(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]],
            ReplicaEndpoints = [fixture.DataEndpoints[1]],
        });
        await client.SetAsync("fallback-key", "available");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await fixture.WaitForDataNodeReplicaAsync(1, deadline.Token);
        await fixture.StopDataNodeAsync(0, deadline.Token);

        var reader = client.WithReadFrom(RespireReadFrom.PrimaryPreferred);
        (await reader.GetStringAsync("fallback-key", deadline.Token)).Should().Be("available");
    }

    [Test]
    [ParallelLimiter<DockerHeavy>]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task ReplicaPreferredFallsBackToPrimaryButReplicaPolicyStaysStrict(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Standalone,
        });
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var unavailable = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[0]],
            ReplicaEndpoints = [new("127.0.0.1", unavailable.Port)],
        });
        await client.SetAsync("fallback-key", "available");

        (await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("fallback-key")).Should().Be("available");
        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("fallback-key"))
            .Throws<RespireConnectionException>();
    }

    [Test]
    [ParallelLimiter<DockerHeavy>]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task StrictReplicaPolicyRejectsConfiguredEndpointWithPrimaryRole(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Topology = RespireContainerTopology.Sentinel,
        });
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]],
            ReplicaEndpoints = [fixture.DataEndpoints[0]],
        });
        await Assert.That(async () => await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("role-check"))
            .Throws<RespireConnectionException>();
    }

    private static Activity StartRoutingScope()
        // Do not inherit a test-runner trace shared with other tests or background commands.
        => new Activity("routing-test").SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();

    private static ActivityListener Listen(ConcurrentDictionary<string, ConcurrentQueue<RespireEndpoint>> routes, ActivityTraceId traceId)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != traceId
                    || activity.GetTagItem("db.operation.name") is not string operation
                    || activity.GetTagItem("server.address") is not string host
                    || activity.GetTagItem("server.port") is not { } port) return;
                routes.GetOrAdd(operation, static _ => new()).Enqueue(new(host, Convert.ToInt32(port)));
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
