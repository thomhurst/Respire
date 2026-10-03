using System.Diagnostics.Metrics;
using FluentAssertions;
using Respire.Internal;
using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class HedgedReadIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments(RespireContainerServer.Redis, false, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Redis, false, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Redis, true, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Redis, true, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Valkey, false, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Valkey, false, RespProtocol.Resp3)]
    [Arguments(RespireContainerServer.Valkey, true, RespProtocol.Resp2)]
    [Arguments(RespireContainerServer.Valkey, true, RespProtocol.Resp3)]
    public async Task PausedReplicaLosesToPrimaryAndLaterRepliesStayOrdered(
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
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Protocol = protocol,
            Connections = 1,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(20), MaximumExtraLoadPercent = 100 },
        });
        var replicaOptions = new RespireOptions
        {
            Endpoints = [fixture.DataEndpoints[1]], Protocol = protocol, Connections = 1,
        };
        await using var control = await RespireClient.ConnectAsync(replicaOptions);
        await using var witness = await RespireClient.ConnectAsync(replicaOptions);
        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        var reader = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await reader.SetAsync("hedged-first", "first", cancellationToken: deadline.Token);
        await reader.SetAsync("hedged-second", "second", cancellationToken: deadline.Token);
        while (await strict.GetStringAsync("hedged-second", deadline.Token) != "second")
            await Task.Delay(20, deadline.Token);

        await AssertPausedReplicaLosesAsync(reader, control, witness, fixture.DataEndpoints[0].Port, "hedged-first", deadline.Token);
        // The losing reply must drain before the next response on the same replica socket.
        (await strict.GetStringAsync("hedged-second", deadline.Token)).Should().Be("second");
    }

    [Test]
    [NotInParallel]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task PausedClusterReplicaLosesToItsSlotPrimary(RespProtocol protocol)
    {
        await using var cluster = await RedisReadReplicaClusterTestContainer.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(cluster.Host, cluster.Port(0))],
            UseCluster = true, Protocol = protocol, Connections = 1,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(20), MaximumExtraLoadPercent = 100 },
        });
        var key = Enumerable.Range(0, 1000).Select(i => $"{{hedged-cluster-{i}}}:first")
            .First(key => ClusterHash.GetSlot(key) < 5461);
        var second = key.Replace(":first", ":second", StringComparison.Ordinal);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.SetAsync(key, "first", cancellationToken: deadline.Token);
        await client.SetAsync(second, "second", cancellationToken: deadline.Token);
        var strict = client.WithReadFrom(RespireReadFrom.Replica);
        while (await strict.GetStringAsync(second, deadline.Token) != "second")
            await Task.Delay(20, deadline.Token);
        var replicaOptions = new RespireOptions
        {
            Endpoints = [new(cluster.Host, cluster.Port(3))], Protocol = protocol, Connections = 1,
        };
        await using var control = await RespireClient.ConnectAsync(replicaOptions);
        await using var witness = await RespireClient.ConnectAsync(replicaOptions);
        await AssertPausedReplicaLosesAsync(client.WithReadFrom(RespireReadFrom.ReplicaPreferred),
            control, witness, cluster.Port(0), key, deadline.Token);
        (await strict.GetStringAsync(second, deadline.Token)).Should().Be("second");
    }

    private static async Task AssertPausedReplicaLosesAsync(IRespireClient reader, RespireClient control,
        RespireClient witness, int primaryPort, string key, CancellationToken cancellationToken)
    {
        long won = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.read.hedge.won")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && Convert.ToInt32(tag.Value) == primaryPort)
                    Interlocked.Add(ref won, value);
        });
        listener.Start();
        using (await control.ExecuteAsync("CLIENT", "PAUSE", 3_000, "ALL")) { }
        // A separate socket proves the replica has not resumed when the hedge returns.
        var pausedPing = witness.PingAsync(cancellationToken).AsTask();
        try
        {
            (await reader.GetStringAsync(key, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("first");
            Interlocked.Read(ref won).Should().Be(1);
            pausedPing.IsCompleted.Should().BeFalse();
        }
        finally
        {
            // Wait for the bounded server pause to expire before checking the drained socket.
            await pausedPing;
        }
    }
}
