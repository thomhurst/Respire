using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerCommandLogTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WireTypesCountsAndBinaryResultsRemainOwned(int protocol)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            byte[]? reply = command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith("COMMANDLOG GET ") => Rows(protocol == 3),
                _ when command.StartsWith("COMMANDLOG LEN ") => ":1\r\n"u8.ToArray(),
                _ => null,
            };
            if (reply is null) return false;
            _ = server.SendRawAsync(reply, server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = (RespProtocol)protocol,
            AllowAdmin = true, ClientSideCache = protocol == 3 ? new() : null,
        });
        var commands = client.WithKeyPrefix("ignored:").Server;
        List<RespireCommandLogEntry> owned = [];
        foreach (var type in Enum.GetValues<RespireCommandLogType>())
        {
            var cache = client.Core.ClientCache;
            if (cache is not null)
            {
                RespireKey key = "cached";
                var token = cache.BeginRead(in key);
                var value = RespValue.BulkString("retained");
                cache.CompleteRead(in token, in value, allowInsert: true);
            }
            var rows = await commands.CommandLogAsync(type, -1);
            owned.Add(rows.Single());
            await commands.CommandLogAsync(type, 0);
            await Assert.That(await commands.CommandLogLengthAsync(type)).IsEqualTo(1);
            if (cache is not null) await Assert.That(cache.Count).IsEqualTo(1);
            await commands.ResetCommandLogAsync(type);
            if (cache is not null) await Assert.That(cache.Count).IsEqualTo(0);
        }
        await client.DisposeAsync();
        foreach (var entry in owned)
        {
            await Assert.That(entry.Arguments[1]).IsEquivalentTo(new byte[] { 255, 0, 32 });
            await Assert.That(entry.ClientName).IsEquivalentTo(new byte[] { 254, 0 });
            await Assert.That(entry.AdditionalValues[0][0][1].AsBytes()).IsEquivalentTo(new byte[] { 253, 0 });
            await Assert.That(entry.DurationMicroseconds).IsEqualTo(entry.Type == RespireCommandLogType.Slow ? (long?)42 : null);
            await Assert.That(entry.RequestBytes).IsEqualTo(entry.Type == RespireCommandLogType.LargeRequest ? (long?)42 : null);
            await Assert.That(entry.ReplyBytes).IsEqualTo(entry.Type == RespireCommandLogType.LargeReply ? (long?)42 : null);
        }
        var expected = new[] { "SLOW", "LARGE-REQUEST", "LARGE-REPLY" }.SelectMany(type => new[]
            { $"COMMANDLOG GET -1 {type}", $"COMMANDLOG GET 0 {type}", $"COMMANDLOG LEN {type}", $"COMMANDLOG RESET {type}" });
        await Assert.That(server.ReceivedCommands.Where(x => x.StartsWith("COMMANDLOG "))).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ParserCopiesInputAndRejectsMalformedStructures()
    {
        byte[] payload = [255, 0];
        var binary = RespValue.BulkString(payload);
        var row = RespValue.Array(RespValue.Integer(1), RespValue.Integer(2), RespValue.Integer(3),
            RespValue.Array(binary), binary, binary, RespValue.Array(binary));
        var source = RespValue.Array(row);
        var result = CommandLogParser.Parse(in source, RespireCommandLogType.LargeReply).Single();
        source.Dispose(); payload.AsSpan().Clear();
        await Assert.That(result.Arguments[0]).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(result.ClientAddress).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(result.ClientName).IsEquivalentTo(new byte[] { 255, 0 });
        await Assert.That(result.AdditionalValues[0][0].AsBytes()).IsEquivalentTo(new byte[] { 255, 0 });
        RespValue[] malformed = [RespValue.Null, RespValue.Integer(0), RespValue.Array(RespValue.Array()),
            RespValue.Array(RespValue.Array(RespValue.Integer(-1), RespValue.Integer(0), RespValue.Integer(0), RespValue.Array(), binary, binary)),
            RespValue.Array(RespValue.Array(RespValue.Integer(0), RespValue.Integer(0), RespValue.Integer(0), RespValue.Array(RespValue.Integer(0)), binary, binary))];
        foreach (var value in malformed)
            await Assert.That(() => CommandLogParser.Parse(in value, RespireCommandLogType.Slow)).Throws<RespireProtocolException>();
        var badSecondRow = RespValue.Array(row, RespValue.Array());
        var contextual = await Assert.That(() => CommandLogParser.Parse(in badSecondRow, RespireCommandLogType.Slow)).Throws<RespireProtocolException>();
        await Assert.That(contextual!.Message).Contains("index 1");
        await Assert.That(contextual.InnerException).IsTypeOf<RespireProtocolException>();
        await Assert.That(() => CommandLogParser.NonnegativeInteger(RespValue.Integer(-1))).Throws<RespireProtocolException>();
        await Assert.That(() => CommandLogParser.Ok(RespValue.BulkString("OK"))).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task AdminValidationAndPreCancellationPreventIo()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        await Assert.That(async () => await client.Server.ResetCommandLogAsync(RespireCommandLogType.Slow)).Throws<NotSupportedException>();
        await Assert.That(async () => await client.Server.ResetCommandLogOnAllNodesAsync(RespireCommandLogType.Slow)).Throws<NotSupportedException>();
        await Assert.That(async () => await client.Server.CommandLogAsync(RespireCommandLogType.Slow, -2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.CommandLogOnAllNodesAsync(RespireCommandLogType.Slow, -2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.CommandLogLengthAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.CommandLogLengthOnAllNodesAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.CommandLogAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Server.CommandLogOnAllNodesAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        await using var admin = RespireClient.Create(new RespireOptions
            { Connections = 1, Endpoints = [new("127.0.0.1", server.Port)], AllowAdmin = true });
        await Assert.That(async () => await admin.Server.ResetCommandLogAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await admin.Server.ResetCommandLogOnAllNodesAsync((RespireCommandLogType)99)).Throws<ArgumentOutOfRangeException>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await client.Server.CommandLogAsync(RespireCommandLogType.Slow, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.CommandLogOnAllNodesAsync(RespireCommandLogType.Slow, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task FanOutKeepsReplicaFailuresAndEndpointProvenance()
    {
        await using var replica = new FakeRespServer("-NOPERM log denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, FakeRespServer.OkReply);
        var nodes = $"self 127.0.0.1:{seed.Port}@1 myself,master - 0 0 1 connected 0-16383\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n";
        seed.SuppressReply = command =>
        {
            byte[]? reply = command switch
            {
                "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n"),
                "CLUSTER NODES" => Bulk(Encoding.ASCII.GetBytes(nodes)),
                "COMMANDLOG GET 10 LARGE-REQUEST" => Rows(false),
                _ => null,
            };
            if (reply is null) return false;
            _ = seed.SendRawAsync(reply, seed.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
            { UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)] });
        var results = await client.Server.CommandLogOnAllNodesAsync(RespireCommandLogType.LargeRequest);
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value[0].RequestBytes).IsEqualTo(42);
        await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
    }

    [Test]
    public async Task CancellationAfterDiscoveryIsAttributedToNode()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => { if (!command.StartsWith("COMMANDLOG ")) return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var task = client.Server.CommandLogLengthOnAllNodesAsync(RespireCommandLogType.Slow, cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Single().Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].Error).IsAssignableTo<OperationCanceledException>();
    }

    private static byte[] Rows(bool resp3)
    {
        var fields = new[] { Bulk("future"u8.ToArray()), Bulk([253, 0]) };
        byte[] future = [.. Encoding.ASCII.GetBytes(resp3 ? "%1\r\n" : "*2\r\n"), .. fields.SelectMany(x => x)];
        return Array(Array(":9\r\n"u8.ToArray(), ":100\r\n"u8.ToArray(), ":42\r\n"u8.ToArray(),
            Array(Bulk("SET"u8.ToArray()), Bulk([255, 0, 32])), Bulk("127.0.0.1:1"u8.ToArray()), Bulk([254, 0]), Array(future)));
    }
    private static byte[] Bulk(byte[] bytes) => [.. Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"), .. bytes, 13, 10];
    private static byte[] Array(params byte[][] items) => [.. Encoding.ASCII.GetBytes($"*{items.Length}\r\n"), .. items.SelectMany(x => x)];
}
