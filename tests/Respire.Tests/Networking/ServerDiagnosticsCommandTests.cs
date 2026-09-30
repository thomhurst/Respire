using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerDiagnosticsCommandTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LiteralArgumentsAndOwnedResponsesWorkForBothProtocolShapes(bool resp3)
    {
        var report = resp3 ? "=14\r\ntxt:diagnosis!\r\n"u8.ToArray() : Bulk("diagnosis!");
        var histogram = Map(resp3, Bulk("get"), Map(resp3, Bulk("calls"), Integer(3),
            Bulk("histogram_usec"), Map(resp3, Integer(1), Integer(1), Integer(8), Integer(3))));
        await using var server = new FakeRespServer(report, Array(Array(Integer(123), Integer(4))), histogram,
            Bulk("memory"), Integer(9), FakeRespServer.OkReply, Map(resp3));
        await using var client = await Connect(server, true);
        var facet = client.WithKeyPrefix("ignored:").Server;
        await Assert.That(await facet.LatencyDoctorAsync()).IsEqualTo("diagnosis!");
        var history = await facet.LatencyHistoryAsync("fork event");
        var histograms = await facet.LatencyHistogramsAsync(["get", "config|get", "get"]);
        await Assert.That(await facet.MemoryDoctorAsync()).IsEqualTo("memory");
        await Assert.That(await facet.SlowLogLengthAsync()).IsEqualTo(9);
        await facet.PurgeMemoryAsync();
        await Assert.That(await facet.LatencyHistogramsAsync()).IsEmpty();
        await client.DisposeAsync();
        await Assert.That(history.Single()).IsEqualTo(new RespireLatencyHistorySample(
            DateTimeOffset.FromUnixTimeSeconds(123), TimeSpan.FromMilliseconds(4)));
        await Assert.That(histograms.Single().Buckets).IsEquivalentTo([
            new RespireLatencyHistogramBucket(1, 1), new RespireLatencyHistogramBucket(8, 3)]);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "LATENCY DOCTOR", "LATENCY HISTORY fork event", "LATENCY HISTOGRAM get config|get get",
            "MEMORY DOCTOR", "SLOWLOG LEN", "MEMORY PURGE", "LATENCY HISTOGRAM"]);
        await Assert.That(server.ReceivedArguments[1].Length).IsEqualTo(3);
    }

    [Test]
    public async Task AdminValidationAndCancellationHappenBeforeIo()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)] });
        await Assert.That(async () => await client.Server.PurgeMemoryAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.PurgeMemoryOnAllNodesAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.LatencyHistoryAsync(" ")).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await client.Server.LatencyHistoryOnAllNodesAsync(null!)).ThrowsExactly<ArgumentNullException>();
        var nullCommand = await Assert.That(async () => await client.Server.LatencyHistogramsAsync(["get", null!]))
            .ThrowsExactly<ArgumentNullException>();
        await Assert.That(nullCommand!.ParamName).IsEqualTo("commands");
        await Assert.That(nullCommand.Message).Contains("index 1");
        await Assert.That(async () => await client.Server.LatencyHistogramsOnAllNodesAsync([null!])).ThrowsExactly<ArgumentNullException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Server.MemoryDoctorAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.SlowLogLengthOnAllNodesAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task ServerErrorsAndMalformedRepliesDoNotReplayOrLoseTheNextResponse()
    {
        await using var server = new FakeRespServer("-ERR unknown subcommand 'histogram'\r\n"u8.ToArray(), Integer(-1), FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = await Assert.That(async () => await client.Server.LatencyHistogramsAsync()).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("ERR");
        await Assert.That(async () => await client.Server.SlowLogLengthAsync()).ThrowsExactly<RespireProtocolException>();
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["LATENCY HISTOGRAM", "SLOWLOG LEN", "PING"]);
    }

    [Test]
    public async Task FanOutSnapshotsNamesAndPreservesReplicaErrorsWithoutChangingTheSlotOwner()
    {
        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replica = new FakeRespServer("-NOPERM diagnostics denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, Array());
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command != "CLUSTER NODES") return false;
            discovering.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var original = await client.Core.Cluster!.GetConnectionAsync(42, default);
        string[] commands = ["get", "config|get"];
        var execution = client.Server.LatencyHistogramsOnAllNodesAsync(commands).AsTask();
        await discovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        commands[0] = "mutated";
        var topology = $"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected 0-16383\n" +
            $"replica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n";
        await seed.SendRawAsync(Bulk(topology));
        var results = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Single(item => item.Endpoint.Port == seed.Port).Value).IsEmpty();
        await Assert.That(results.Single(item => item.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        await Assert.That(ReferenceEquals(original, await client.Core.Cluster.GetConnectionAsync(42, default))).IsTrue();
        foreach (var node in new[] { seed, replica })
            await Assert.That(node.ReceivedCommands).Contains("LATENCY HISTOGRAM get config|get");
    }

    [Test]
    public async Task FanOutCancellationKeepsEndpointAndCallerToken()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => { if (command != "MEMORY DOCTOR") return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var execution = client.Server.MemoryDoctorOnAllNodesAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = (await execution.WaitAsync(TimeSpan.FromSeconds(5))).Single();
        await Assert.That(result.Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(result.Error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)result.Error!).CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiagnosticsPreserveCachedValuesAndInflightReadTokens(bool allNodes)
    {
        await using var server = new FakeRespServer(7, FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            byte[] reply = command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "LATENCY HISTORY command" or "LATENCY HISTOGRAM" => Array(),
                "SLOWLOG LEN" => Integer(0),
                _ => FakeRespServer.OkReply,
            };
            _ = server.SendRawAsync(reply, server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = true, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        RespireKey cached = "cached", pending = "pending";
        var inserted = cache.BeginRead(in cached);
        using var value = RespValue.BulkString("value");
        cache.CompleteRead(in inserted, in value, allowInsert: true);
        var reading = cache.BeginRead(in pending);
        if (allNodes)
        {
            _ = await client.Server.LatencyDoctorOnAllNodesAsync();
            _ = await client.Server.LatencyHistoryOnAllNodesAsync("command");
            _ = await client.Server.LatencyHistogramsOnAllNodesAsync();
            _ = await client.Server.MemoryDoctorOnAllNodesAsync();
            _ = await client.Server.SlowLogLengthOnAllNodesAsync();
            _ = await client.Server.PurgeMemoryOnAllNodesAsync();
        }
        else
        {
            _ = await client.Server.LatencyDoctorAsync();
            _ = await client.Server.LatencyHistoryAsync("command");
            _ = await client.Server.LatencyHistogramsAsync();
            _ = await client.Server.MemoryDoctorAsync();
            _ = await client.Server.SlowLogLengthAsync();
            await client.Server.PurgeMemoryAsync();
        }
        cache.CompleteRead(in reading, in value, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EveryStandaloneFanOutReturnsOneOwnedEndpointResult()
    {
        await using var server = new FakeRespServer(7, FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            byte[] reply = command switch
            {
                "LATENCY HISTORY command" or "LATENCY HISTOGRAM" => Array(),
                "SLOWLOG LEN" => Integer(0),
                _ => FakeRespServer.OkReply,
            };
            _ = server.SendRawAsync(reply, server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await Connect(server, true);
        await Assert.That((await client.Server.LatencyDoctorOnAllNodesAsync()).Single().Value).IsEqualTo("OK");
        await Assert.That((await client.Server.LatencyHistoryOnAllNodesAsync("command")).Single().Value).IsEmpty();
        await Assert.That((await client.Server.LatencyHistogramsOnAllNodesAsync()).Single().Value).IsEmpty();
        await Assert.That((await client.Server.MemoryDoctorOnAllNodesAsync()).Single().Value).IsEqualTo("OK");
        await Assert.That((await client.Server.PurgeMemoryOnAllNodesAsync()).Single().Value).IsTrue();
        var length = (await client.Server.SlowLogLengthOnAllNodesAsync()).Single();
        await Assert.That(length.Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(length.Value).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(6);
    }

    private static ValueTask<RespireClient> Connect(FakeRespServer server, bool admin)
        => RespireClient.ConnectAsync(new RespireOptions { Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = admin });
    private static byte[] Bulk(string value) => Bulk(Encoding.UTF8.GetBytes(value));
    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($":{value}\r\n");
    private static byte[] Array(params byte[][] values) => [.. Encoding.ASCII.GetBytes($"*{values.Length}\r\n"), .. values.SelectMany(value => value)];
    private static byte[] Map(bool resp3, params byte[][] values) => resp3
        ? [.. Encoding.ASCII.GetBytes($"%{values.Length / 2}\r\n"), .. values.SelectMany(value => value)] : Array(values);
    private static byte[] Slots(int port) => Array(Array(Integer(0), Integer(16383), Array(Bulk("127.0.0.1"), Integer(port), Bulk("self"))));
}
