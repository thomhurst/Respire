using System.Collections.Concurrent;
using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Respire.Internal;
using Respire.Networking;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ReplicaLatencyBandIntegrationTests
{
    [Test]
    [ParallelLimiter<DockerHeavy>]
    public async Task SeveralRealReplicasDistributeReadsAcrossMeasuredBand()
    {
        // One owned network namespace allows replicas to reach the primary on loopback.
        await using var container = new ContainerBuilder("redis:7.2-alpine")
            .WithPortBinding(6379, true).WithPortBinding(6380, true)
            .WithPortBinding(6381, true).WithPortBinding(6382, true)
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var bindings in parameters.HostConfig!.PortBindings.Values)
                    foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
            })
            .WithEntrypoint("sh", "-c")
            .WithCommand("redis-server --port 6379 --save '' --appendonly no & " +
                "for port in 6380 6381 6382; do mkdir -p /data/$port; " +
                "redis-server --port $port --dir /data/$port --save '' --appendonly no --replicaof 127.0.0.1 6379 & done; wait")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)
                .UntilInternalTcpPortIsAvailable(6380).UntilInternalTcpPortIsAvailable(6381)
                .UntilInternalTcpPortIsAvailable(6382))
            .Build();
        await container.StartAsync();
        var endpoints = Enumerable.Range(6379, 4)
            .Select(port => new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(port))).ToArray();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [endpoints[0]], ReplicaEndpoints = endpoints[1..],
            Connections = 1, Protocol = RespireProtocol.Resp2,
            ReplicaLatencyTolerance = TimeSpan.FromMilliseconds(1),
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        const string key = "replica-latency-band";
        await client.SetAsync(key, "value");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Replication readiness is independent of advisory RTT measurements.
        foreach (var endpoint in endpoints[1..])
        {
            await using var direct = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [endpoint] });
            while (await direct.GetStringAsync(key, deadline.Token) != "value")
                await Task.Delay(20, deadline.Token);
        }
        // Keep band membership deterministic while exercising real ROLE validation and read I/O.
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(TimeSpan.FromMilliseconds(connection.Port == endpoints[1].Port ? 1
                : connection.Port == endpoints[2].Port ? 2 : 10).Ticks), () => 0);
        var reader = client.WithReadFrom(RespireReadFrom.Replica);
        using var scope = new Activity("latency-band-routing").Start();
        var traceId = scope.TraceId;
        var selectedPorts = new ConcurrentQueue<int>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.GetTagItem("db.operation.name") as string == "GET"
                    && activity.GetTagItem("server.port") is { } port)
                    selectedPorts.Enqueue(Convert.ToInt32(port));
            },
        };
        ActivitySource.AddActivityListener(listener);
        for (var index = 0; index < 24; index++)
        {
            (await reader.GetStringAsync(key, deadline.Token)).Should().Be("value");
            var selection = client.Core.ReadRouter.LastReadSelection!;
            selection.EligibleEndpoints.Should().BeEquivalentTo(endpoints[1..3]);
        }
        selectedPorts.Should().HaveCount(24);
        selectedPorts.Distinct().Should().BeEquivalentTo(endpoints[1..3].Select(endpoint => endpoint.Port));
    }
}
