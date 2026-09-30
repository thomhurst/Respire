using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerHotKeysTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WireOptionsOwnedSnapshotsAndPinnedConnection(int protocol)
    {
        byte[][] replies = protocol == 3 ? ["%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply] : [FakeRespServer.OkReply];
        await using var server = new FakeRespServer(2, replies);
        server.SuppressReply = command =>
        {
            byte[]? reply = command switch
            {
                "HOTKEYS GET" => WireSnapshot(protocol == 3),
                _ => null,
            };
            if (reply is null) return false;
            _ = server.SendRawAsync(reply, server.ReceivedConnectionIds[^1]); return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with
            { Connections = 2, Protocol = (RespProtocol)protocol, ClientSideCache = protocol == 3 ? new() : null });
        var tracker = await client.WithKeyPrefix("ignored:").Server.GetHotKeysTrackerAsync();
        var second = await client.Server.GetHotKeysTrackerAsync();
        int[] slots = [1, 3];
        await tracker.StartAsync(new() { Count = 64, DurationSeconds = 1_000_000, SampleRatio = int.MaxValue, Slots = slots });
        slots[0] = 9;
        await second.StartAsync(new() { Metrics = RespireHotKeysMetrics.Network });
        var cache = client.Core.ClientCache;
        if (cache is not null)
        {
            RespireKey key = "cached"; var token = cache.BeginRead(in key); var value = RespValue.BulkString("v");
            cache.CompleteRead(in token, in value, allowInsert: true);
        }
        var snapshot = (await tracker.GetAsync())!.Single();
        if (cache is not null) await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(await tracker.StopAsync()).IsTrue();
        if (cache is not null) await Assert.That(cache.Count).IsEqualTo(0);
        await tracker.ResetAsync();
        var commands = server.ReceivedCommands.Select((command, index) => (command, socket: server.ReceivedConnectionIds[index]))
            .Where(row => row.command.StartsWith("HOTKEYS ")).ToArray();
        await Assert.That(commands.Select(x => x.command)).IsEquivalentTo([
            "HOTKEYS START METRICS 2 CPU NET COUNT 64 DURATION 1000000 SAMPLE 2147483647 SLOTS 2 1 3",
            "HOTKEYS START METRICS 1 NET", "HOTKEYS GET", "HOTKEYS STOP", "HOTKEYS RESET"], CollectionOrdering.Matching);
        await Assert.That(commands[0].socket).IsNotEqualTo(commands[1].socket);
        await Assert.That(commands.Skip(2).All(row => row.socket == commands[0].socket)).IsTrue();
        await client.DisposeAsync();
        await Assert.That(tracker.IsConnected).IsFalse();
        await Assert.That(tracker.Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(snapshot.ByCpuTime![0].Key).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(snapshot.ByNetworkBytes![0].Bytes).IsEqualTo(100);
        await Assert.That(snapshot.ByCpuTime[0].Microseconds).IsEqualTo(7);
        await Assert.That(snapshot.TotalCpuUserMilliseconds).IsEqualTo(2);
        await Assert.That(snapshot.SampledCommandsSelectedSlotsMicroseconds).IsNull();
        await Assert.That(snapshot.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo(new byte[] { 254, 0 });
    }

    [Test]
    public async Task ParserRejectsMalformedValuesAndCopiesAllBytes()
    {
        byte[] key = [255, 0];
        var fields = Fields(key);
        var source = RespValue.Array(RespValue.Array(fields));
        var result = HotKeysParser.Parse(in source)!.Single();
        source.Dispose(); key.AsSpan().Clear();
        await Assert.That(result.ByNetworkBytes![0].Key).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(HotKeysParser.Parse(RespValue.Null)).IsNull();
        await Assert.That(HotKeysParser.Stopped(RespValue.Null)).IsFalse();
        await Assert.That(() => HotKeysParser.Ok(RespValue.BulkString("OK"))).Throws<RespireProtocolException>();
        RespValue[] bad = [RespValue.Integer(1), RespValue.Array(RespValue.Integer(1)), RespValue.Array(RespValue.Array()),
            RespValue.Array(RespValue.Array([.. fields, RespValue.BulkString("extra")])),
            RespValue.Array(RespValue.Array([.. fields, fields[0], fields[1]]))];
        foreach (var value in bad) await Assert.That(() => HotKeysParser.Parse(in value)).Throws<RespireProtocolException>();
        foreach (var (name, value) in new (string, RespValue)[]
        {
            ("tracking-active", RespValue.Integer(2)), ("sample-ratio", RespValue.Integer(0)),
            ("collection-duration-ms", RespValue.Integer(-1)), ("total-net-bytes", RespValue.BulkString("3")),
            ("selected-slots", RespValue.Array(RespValue.Array(RespValue.Integer(3), RespValue.Integer(2)))),
            ("by-net-bytes", RespValue.Array(RespValue.BulkString("key"))),
            ("by-cpu-time-us", RespValue.Array(RespValue.Integer(3), RespValue.Integer(1))),
        })
        {
            var changed = Fields([1]);
            for (var index = 0; index < changed.Length; index += 2) if (changed[index].AsString() == name) changed[index + 1] = value;
            var malformed = RespValue.Array(RespValue.Array(changed));
            await Assert.That(() => HotKeysParser.Parse(in malformed)).Throws<RespireProtocolException>();
        }
    }

    [Test]
    public async Task AdminValidationAndCancellationSendNothing()
    {
        await using var server = new FakeRespServer(2);
        await using var client = RespireClient.Create(Options(server.Port) with { AllowAdmin = false });
        Func<Task>[] denied = [async () => await client.Server.StartHotKeysOnAllNodesAsync(new()),
            async () => await client.Server.StopHotKeysOnAllNodesAsync(), async () => await client.Server.ResetHotKeysOnAllNodesAsync()];
        foreach (var action in denied) await Assert.That(action).Throws<NotSupportedException>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await client.Server.GetHotKeysTrackerAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.GetHotKeysOnAllNodesAsync(cancellation.Token)).Throws<OperationCanceledException>();
        var tracker = await client.Server.GetHotKeysTrackerAsync();
        Func<Task>[] pinnedDenied = [async () => await tracker.StartAsync(new()), async () => await tracker.StopAsync(), async () => await tracker.ResetAsync()];
        foreach (var action in pinnedDenied) await Assert.That(action).Throws<NotSupportedException>();
        await using var admin = await RespireClient.ConnectAsync(Options(server.Port));
        var allowed = await admin.Server.GetHotKeysTrackerAsync();
        RespireHotKeysOptions[] invalid = [new() { Metrics = 0 }, new() { Metrics = (RespireHotKeysMetrics)4 },
            new() { Count = 0 }, new() { Count = 65 }, new() { DurationSeconds = 0 }, new() { DurationSeconds = 1_000_001 },
            new() { SampleRatio = 0 }, new() { Slots = new[] { -1 } }, new() { Slots = new[] { 16384 } }, new() { Slots = new[] { 1, 1 } }];
        foreach (var options in invalid)
        {
            await Assert.That(async () => await allowed.StartAsync(options)).Throws<ArgumentException>();
            await Assert.That(async () => await admin.Server.StartHotKeysOnAllNodesAsync(options)).Throws<ArgumentException>();
        }
        Func<Task>[] cancelled = [async () => await allowed.StartAsync(new(), cancellation.Token),
            async () => await allowed.GetAsync(cancellation.Token), async () => await allowed.StopAsync(cancellation.Token),
            async () => await allowed.ResetAsync(cancellation.Token), async () => await admin.Server.StartHotKeysOnAllNodesAsync(new(), cancellation.Token),
            async () => await admin.Server.StopHotKeysOnAllNodesAsync(cancellation.Token), async () => await admin.Server.ResetHotKeysOnAllNodesAsync(cancellation.Token)];
        foreach (var action in cancelled) await Assert.That(action).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task PinnedOperationsNeverFollowRedirects()
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(), Encoding.ASCII.GetBytes($"-MOVED 0 127.0.0.1:{target.Port}\r\n"), "$-1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with { UseCluster = true });
        var tracker = await client.Server.GetHotKeysTrackerAsync();
        var error = await Assert.That(async () => await tracker.StartAsync(new())).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("MOVED");
        await Assert.That(await tracker.GetAsync()).IsNull();
        await Assert.That(target.CommandsSeen).IsEqualTo(0);
        await Assert.That(seed.CommandsSeen).IsEqualTo(3);
    }

    [Test]
    public async Task FanOutKeepsReplicaErrorsAndEndpointProvenance()
    {
        await using var replica = new FakeRespServer(4, "-NOPERM hotkeys denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(5, FakeRespServer.OkReply);
        seed.SuppressReply = command =>
        {
            byte[]? reply = command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "CLUSTER NODES" => Bulk(Encoding.ASCII.GetBytes($"self 127.0.0.1:{seed.Port}@1 myself,master - 0 0 1 connected\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n")),
                "HOTKEYS GET" => WireSnapshot(false), _ => null,
            };
            if (reply is null) return false;
            _ = seed.SendRawAsync(reply, seed.ReceivedConnectionIds[^1]); return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with { UseCluster = true });
        var start = await client.Server.StartHotKeysOnAllNodesAsync(new());
        var get = await client.Server.GetHotKeysOnAllNodesAsync();
        var stop = await client.Server.StopHotKeysOnAllNodesAsync();
        var reset = await client.Server.ResetHotKeysOnAllNodesAsync();
        foreach (var results in new[] { start, stop, reset })
        {
            await Assert.That(results.Length).IsEqualTo(2);
            await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value).IsTrue();
            await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        }
        await Assert.That(get.Single(x => x.Endpoint.Port == seed.Port).Value![0].TrackingActive).IsTrue();
        await Assert.That(get.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DispatchedCancellationDoesNotReplay(bool allNodes)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        { SuppressReply = command => { if (!command.StartsWith("HOTKEYS ")) return false; received.TrySetResult(); return true; } };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        using var cancellation = new CancellationTokenSource();
        var tracker = await client.Server.GetHotKeysTrackerAsync();
        var task = allNodes ? AllNodes() : tracker.StartAsync(new(), cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.That(async () => await task.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count(x => x.StartsWith("HOTKEYS "))).IsEqualTo(1);
        async Task AllNodes()
        {
            var results = await client.Server.StartHotKeysOnAllNodesAsync(new(), cancellation.Token);
            await Assert.That(results[0].Endpoint.Port).IsEqualTo(server.Port);
            throw results[0].Error!;
        }
    }

    private static RespireOptions Options(int port) => new() { Endpoints = [new("127.0.0.1", port)], Connections = 1, Protocol = RespProtocol.Resp2, AllowAdmin = true };
    private static RespValue[] Fields(byte[] key) => [
        RespValue.BulkString("tracking-active"), RespValue.Integer(1), RespValue.BulkString("sample-ratio"), RespValue.Integer(1),
        RespValue.BulkString("selected-slots"), RespValue.Array(RespValue.Array(RespValue.Integer(0), RespValue.Integer(16383))),
        RespValue.BulkString("collection-start-time-unix-ms"), RespValue.Integer(1000), RespValue.BulkString("collection-duration-ms"), RespValue.Integer(4),
        RespValue.BulkString("all-commands-all-slots-us"), RespValue.Integer(10), RespValue.BulkString("net-bytes-all-commands-all-slots"), RespValue.Integer(100),
        RespValue.BulkString("total-cpu-time-user-ms"), RespValue.Integer(2), RespValue.BulkString("total-net-bytes"), RespValue.Integer(100),
        RespValue.BulkString("by-cpu-time-us"), RespValue.Array(RespValue.BulkString(key), RespValue.Integer(7)),
        RespValue.BulkString("by-net-bytes"), RespValue.Array(RespValue.BulkString(key), RespValue.Integer(100)),
        RespValue.BulkString("future"), RespValue.Array(RespValue.BulkString(new byte[] { 254, 0 }))];
    private static byte[] WireSnapshot(bool resp3)
    {
        var fields = Fields([255, 0]);
        return Sequence('*', Sequence(resp3 ? '%' : '*', fields.Select(Encode).ToArray()));
    }
    private static byte[] Encode(RespValue value) => value.Type switch
    {
        RespDataType.Integer => Encoding.ASCII.GetBytes($":{value.AsInteger()}\r\n"),
        RespDataType.Array => Sequence('*', value.AsArray().ToArray().Select(Encode).ToArray()),
        _ => Bulk(value.AsSpan().ToArray()),
    };
    private static byte[] Bulk(byte[] bytes) => [.. Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"), .. bytes, 13, 10];
    private static byte[] Sequence(char type, params byte[][] items)
        => [.. Encoding.ASCII.GetBytes($"{type}{(type == '%' ? items.Length / 2 : items.Length)}\r\n"), .. items.SelectMany(x => x)];
}
