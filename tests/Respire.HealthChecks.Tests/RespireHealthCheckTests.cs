using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.HealthChecks;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireHealthCheckTests
{
    private static IServiceCollection CreateServices()
        => new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

    private static HealthCheckContext Context(HealthStatus failureStatus = HealthStatus.Unhealthy)
        => new() { Registration = new("respire", _ => throw new NotSupportedException(), failureStatus, null) };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HealthyUsesExistingTransport(bool allNodes)
    {
        await using var server = new RespireFakeServer();
        var connections = 0;
        var options = server.CreateOptions();
        var factory = options.TestingStreamFactory!;
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Connections = 1,
            TestingStreamFactory = (host, port, token) =>
            {
                Interlocked.Increment(ref connections);
                return factory(host, port, token);
            },
        });
        var check = new RespireHealthCheck(client, new() { ProbeAllNodes = allNodes });
        var result = await check.CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(connections).IsEqualTo(1);
        await Assert.That(((RespireNodeHealth[])result.Data["nodes"]).Length).IsEqualTo(1);
        await Assert.That(client.IsConnected).IsTrue();
    }

    [Test]
    public async Task LazyClientDoesNotOpenConnections()
    {
        await using var server = new RespireFakeServer();
        await using var client = RespireClient.Create(server.CreateOptions());
        var result = await new RespireHealthCheck(client).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task DelayedPingIsDegraded()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("PING", RespireFakeFault.Delay(TimeSpan.FromMilliseconds(30)));
        var check = new RespireHealthCheck(client, new() { DegradedLatency = TimeSpan.FromTicks(1) });
        var result = await check.CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded);
        await Assert.That(fault.MatchedCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(HealthStatus.Unhealthy)]
    [Arguments(HealthStatus.Degraded)]
    public async Task PingErrorsHonorFailureStatus(HealthStatus failureStatus)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("PING", RespireFakeFault.Loading());
        var result = await new RespireHealthCheck(client).CheckHealthAsync(Context(failureStatus));
        await Assert.That(result.Status).IsEqualTo(failureStatus);
        await Assert.That(result.Exception).IsNotNull();
        await Assert.That(((RespireNodeHealth[])result.Data["nodes"])[0].ErrorType).IsEqualTo(nameof(RespireServerException));
    }

    [Test]
    public async Task ProbeTimeoutIsBoundedAndConnectionStillWorks()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        using var fault = server.InjectFault("PING", RespireFakeFault.Pause(new()));
        var check = new RespireHealthCheck(client, new() { ProbeTimeout = TimeSpan.FromMilliseconds(50) });
        var result = await check.CheckHealthAsync(Context()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        fault.Dispose();
        await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task CallerCancellationPropagates()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("PING", RespireFakeFault.Pause(new()));
        using var cancel = new CancellationTokenSource();
        var pending = new RespireHealthCheck(client).CheckHealthAsync(Context(), cancel.Token);
        await fault.Matched.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task RegistrationResolvesSharedClient()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var services = CreateServices();
        services.AddSingleton<IRespireClient>(client);
        services.AddHealthChecks().AddRespire(tags: ["ready"]);
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        await Assert.That(report.Entries["respire"].Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(report.Entries["respire"].Tags).Contains("ready");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task KnownUnusedReplicaFailsOnlyAllNodeMode(bool allNodes)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            ReplicaEndpoints = [new("unused-replica")],
        });
        var result = await new RespireHealthCheck(client, new() { ProbeAllNodes = allNodes }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(allNodes ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        if (allNodes)
        {
            var nodes = (RespireNodeHealth[])result.Data["nodes"];
            await Assert.That(nodes.Length).IsEqualTo(2);
            await Assert.That(nodes[1].IsConnected).IsFalse();
        }
    }

    [Test]
    public async Task AllNodesReuseValidatedReadReplica()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var replica = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray(),
                "GET key" => "$7\r\nreplica\r\n"u8.ToArray(),
                _ => FakeRespServer.PongReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        await Assert.That(await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("key")).IsEqualTo("replica");
        var result = await new RespireHealthCheck(client, new() { ProbeAllNodes = true }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(((RespireNodeHealth[])result.Data["nodes"]).Length).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
        await Assert.That(replica.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AllClusterPrimariesReuseOpenConnections(bool replica)
    {
        await using var first = new FakeRespServer(FakeRespServer.PongReply);
        await using var second = new FakeRespServer(FakeRespServer.PongReply);
        await using var unused = new FakeRespServer(FakeRespServer.PongReply);
        var extra = replica ? $"*2\r\n$9\r\n127.0.0.1\r\n:{unused.Port}\r\n" : "";
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*{(replica ? 4 : 3)}\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n{extra}"
            + $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        foreach (var server in new[] { first, second })
            server.ReplyOverride = (_, command) => command switch
            {
                "INFO SERVER" => FakeRespServer.PongReply,
                "CLUSTER SLOTS" => topology,
                _ => FakeRespServer.PongReply,
            };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null);
        var connections = first.ReceivedConnectionIds.Concat(second.ReceivedConnectionIds).ToArray();
        var result = await new RespireHealthCheck(client, new() { ProbeAllNodes = true }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(replica ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        var nodes = (RespireNodeHealth[])result.Data["nodes"];
        await Assert.That(nodes.Length).IsEqualTo(replica ? 3 : 2);
        await Assert.That(nodes.Count(node => node.Latency is not null)).IsEqualTo(2);
        await Assert.That(unused.CommandsSeen).IsEqualTo(0);
        await Assert.That(first.ReceivedConnectionIds.Concat(second.ReceivedConnectionIds).Except(connections).Count()).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CacheStatisticsAreOptional(bool include)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "PING" => FakeRespServer.PongReply,
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp3,
            ClientSideCache = new(), Connections = 1,
        });
        var result = await new RespireHealthCheck(client, new() { IncludeClientSideCache = include }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(result.Data.ContainsKey("clientSideCache")).IsEqualTo(include);
        if (include)
            await Assert.That((RespireClientSideCacheStatistics)result.Data["clientSideCache"]).IsEqualTo(client.ClientSideCache!.GetStatistics());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailoverDataIncludesFailedCandidate(bool degrade)
    {
        await using var first = new RespireFakeServer();
        await using var second = new RespireFakeServer();
        using var fault = first.InjectFault("PING", RespireFakeFault.Loading(), occurrences: null);
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [new(first.CreateOptions(), 0), new(second.CreateOptions(), 1)],
            new() { ProbeInterval = TimeSpan.FromHours(1) });
        var result = await new RespireHealthCheck(group, new() { DegradeOnFailover = degrade }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(degrade ? HealthStatus.Degraded : HealthStatus.Healthy);
        await Assert.That((bool)result.Data["failoverConnected"]).IsTrue();
        var statuses = (IReadOnlyList<RespireFailoverEndpointStatus>)result.Data["failoverEndpoints"];
        await Assert.That(statuses.Count).IsEqualTo(2);
        await Assert.That(statuses.Count(status => status.IsHealthy)).IsEqualTo(1);
    }

    [Test]
    public async Task GroupWithoutHealthyCandidateIsUnhealthy()
    {
        await using var server = new RespireFakeServer();
        await using var group = await RespireFailoverGroup.ConnectAsync([new(server.CreateOptions())],
            new() { ProbeInterval = TimeSpan.FromMilliseconds(10), FailureThreshold = 1 });
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        group.EndpointSwitched += change =>
        {
            if (change.Reason == RespireFailoverSwitchReasons.NoHealthyEndpoint) unavailable.TrySetResult();
        };
        using var fault = server.InjectFault("PING", RespireFakeFault.Loading(), occurrences: null);
        await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await new RespireHealthCheck(group).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That((bool)result.Data["failoverConnected"]).IsFalse();
    }

    [Test]
    public async Task KeyedRegistrationSelectsExistingClient()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var services = CreateServices();
        services.AddKeyedSingleton<IRespireClient>("redis", client);
        services.AddHealthChecks().AddRespire(name: "keyed",
            clientFactory: provider => provider.GetRequiredKeyedService<IRespireClient>("redis"));
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        await Assert.That(report.Entries["keyed"].Status).IsEqualTo(HealthStatus.Healthy);
    }

    [Test]
    public async Task InvalidOptionsAreRejectedAtRegistration()
    {
        var builder = CreateServices().AddHealthChecks();
        await Assert.That(() => builder.AddRespire(new() { ProbeTimeout = TimeSpan.Zero })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => builder.AddRespire(new() { DegradedLatency = TimeSpan.Zero })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SentinelProbeUsesValidatedDataPrimary()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : FakeRespServer.PongReply,
        };
        await using var sentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes(
                    $"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n"),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespProtocol.Resp2, Connections = 1,
        });
        var result = await new RespireHealthCheck(client, new() { ProbeAllNodes = true }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        var nodes = (RespireNodeHealth[])result.Data["nodes"];
        await Assert.That(nodes.Length).IsEqualTo(1);
        await Assert.That(nodes[0].Endpoint.Port).IsEqualTo(primary.Port);
    }

    [Test]
    public async Task UnresolvedSentinelReportsFailureWithoutDiscovery()
    {
        await using var sentinel = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
        });
        var result = await new RespireHealthCheck(client).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task FailoverRegistrationResolvesExistingGroup()
    {
        await using var server = new RespireFakeServer();
        await using var group = await RespireFailoverGroup.ConnectAsync([new(server.CreateOptions())],
            new() { ProbeInterval = TimeSpan.FromHours(1) });
        var services = CreateServices();
        services.AddSingleton(group);
        services.AddHealthChecks().AddRespireFailoverGroup();
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        await Assert.That(report.Entries["respire-failover"].Status).IsEqualTo(HealthStatus.Healthy);
    }
}
