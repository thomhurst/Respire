using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DeferredStreamCommandTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CommandsSnapshotArgumentsAndOwnRangeReplies(bool transactional, bool prefixed)
    {
        byte[] range = [.. "*1\r\n*2\r\n$3\r\n1-0\r\n*2\r\n$5\r\nvalue\r\n$2\r\n"u8, 0xff, 0, .. "\r\n"u8];
        byte[][] replies = ["$3\r\n1-0\r\n"u8.ToArray(), "$-1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            range, range, ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(WrapReplies(replies, transactional));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefix = prefixed ? "tenant:" : "";
        var view = prefixed ? client.WithKeyPrefix(prefix) : client;
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        byte[] key = "events"u8.ToArray();
        byte[] payload = [0xff, 0];
        (string Field, RespireValue Value)[] fields = [("value", payload)];
        RespireStreamId[] ids = ["1-0"];
        var added = queue.Streams.Add(key, fields);
        var absent = queue.Streams.Add("missing", new StreamAddOptions { CreateStream = false, MaxLength = 10, ApproximateTrim = true }, fields);
        var count = queue.Streams.Count(key);
        var entries = queue.Streams.Range(key);
        var reverse = queue.Streams.Range(key, "1-0", "9-0", 2, descending: true);
        var removed = queue.Streams.Remove(key, ids);
        var trimmed = queue.Streams.TrimByMaxLength(key, 1);
        var approximate = queue.Streams.TrimByMaxLength(key, 10, approximate: true);
        var acknowledged = queue.Streams.Acknowledge(key, "workers", ids);
        Array.Fill(key, (byte)'x');
        Array.Fill(payload, (byte)'x');
        fields[0] = ("changed", "changed");
        ids[0] = "9-9";
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(added.Result.ToString()).IsEqualTo("1-0");
        await Assert.That(absent.Result).IsNull();
        await Assert.That(count.Result).IsEqualTo(1);
        await Assert.That(removed.Result).IsEqualTo(1);
        await Assert.That(trimmed.Result).IsEqualTo(1);
        await Assert.That(approximate.Result).IsEqualTo(0);
        await Assert.That(acknowledged.Result).IsEqualTo(1);
        // Disposal of the execution owner does not invalidate copied entry fields.
        batch?.Dispose();
        if (transaction is not null) await transaction.DisposeAsync();
        await Assert.That(entries.Result[0]["value"]!).IsEquivalentTo(new byte[] { 0xff, 0 }, CollectionOrdering.Matching);
        await Assert.That(reverse.Result[0].Id.ToString()).IsEqualTo("1-0");
        var commands = server.ReceivedArguments.Where(args => Encoding.UTF8.GetString(args[0]) is not "MULTI" and not "EXEC").ToArray();
        await Assert.That(Encoding.UTF8.GetString(commands[0][1])).IsEqualTo(prefix + "events");
        await Assert.That(commands[0][3]).IsEquivalentTo(new byte[] { (byte)'v', (byte)'a', (byte)'l', (byte)'u', (byte)'e' });
        await Assert.That(commands[0][4]).IsEquivalentTo(new byte[] { 0xff, 0 }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands.Skip(transactional ? 3 : 2).Take(7)).IsEquivalentTo(new[]
        {
            $"XLEN {prefix}events", $"XRANGE {prefix}events - +", $"XREVRANGE {prefix}events 9-0 1-0 COUNT 2",
            $"XDEL {prefix}events 1-0", $"XTRIM {prefix}events MAXLEN 1", $"XTRIM {prefix}events MAXLEN ~ 10", $"XACK {prefix}events workers 1-0",
        }, CollectionOrdering.Matching);
        await Assert.That(Encoding.UTF8.GetString(commands[1][1])).IsEqualTo(prefix + "missing");
        await Assert.That(commands[1].Skip(2).Take(5).Select(Encoding.UTF8.GetString))
            .IsEquivalentTo(new[] { "NOMKSTREAM", "MAXLEN", "~", "10", "*" }, CollectionOrdering.Matching);
        if (transactional)
        {
            await Assert.That(server.ReceivedCommands[0]).IsEqualTo("MULTI");
            await Assert.That(server.ReceivedCommands[^1]).IsEqualTo("EXEC");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerErrorsPreserveSuccessfulDeferredResults(bool transactional)
    {
        byte[][] replies = ["-WRONGTYPE test\r\n"u8.ToArray(), ":7\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(WrapReplies(replies, transactional));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var failed = queue.Streams.Add("wrong", ("value", "value"));
        var successful = queue.Streams.Count("events");
        if (transaction is not null) await transaction.CommitAsync();
        else await Assert.That(async () => await batch!.ExecuteAsync()).Throws<RespireServerException>();
        await Assert.That(failed.Error).IsTypeOf<RespireServerException>();
        await Assert.That(() => failed.Result).Throws<RespireServerException>();
        await Assert.That(successful.Result).IsEqualTo(7);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task EmptyAcknowledgementPreservesServerValidationAcrossSurfaces(int surface)
    {
        byte[][] replies = ["-ERR wrong number of arguments for 'xack' command\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(WrapReplies(replies, transactional: surface == 2));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        if (surface == 0)
        {
            await Assert.That(async () => await client.Streams.AcknowledgeAsync("events", "workers", []))
                .Throws<RespireServerException>();
        }
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var acknowledged = queue.Streams.Acknowledge("events", "workers", []);
            if (transaction is not null) await transaction.CommitAsync();
            else await Assert.That(async () => await batch!.ExecuteAsync()).Throws<RespireServerException>();
            await Assert.That(() => acknowledged.Result).Throws<RespireServerException>();
        }
        await Assert.That(server.ReceivedCommands).Contains("XACK events workers");
    }

    [Test]
    public async Task InvalidArgumentsAndCrossSlotCommandsDoNotEnqueue()
    {
        await using var client = RespireClient.Create(new RespireOptions { UseCluster = true, Endpoints = [new("127.0.0.1", 1)] });
        var view = client.WithKeyPrefix("tenant:");
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        foreach (var streams in new[] { batch.Streams, transaction.Streams })
        {
            await Assert.That(() => streams.Remove("{a}:events", [])).Throws<ArgumentException>();
            await Assert.That(() => streams.TrimByMaxLength("{a}:events", -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => streams.Add("{a}:events", new StreamAddOptions { MaxLength = -1 }, ("field", "value")))
                .Throws<ArgumentOutOfRangeException>();
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        _ = transaction.Set("{a}:state", "ready");
        await Assert.That(() => transaction.Streams.Add("{b}:events", ("field", "value"))).Throws<InvalidOperationException>();
        await Assert.That(transaction.Count).IsEqualTo(1);
        _ = transaction.Streams.Add("{a}:events", ("field", "value"));
        await Assert.That(transaction.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRoutesStreamCommandsToPrefixedKeyOwner(bool transactional)
    {
        byte[][] replies = ["$3\r\n1-0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        await using var other = new FakeRespServer(":9\r\n"u8.ToArray());
        await using var owner = new FakeRespServer(WrapReplies(replies, transactional));
        var firstSlot = ClusterHash.GetSlot("tenant:{bar}:events");
        var secondSlot = ClusterHash.GetSlot("tenant:{foo}:events");
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:{firstSlot}\r\n:{firstSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{other.Port}\r\n" +
            $"*3\r\n:{secondSlot}\r\n:{secondSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var otherCount = batch?.Streams.Count("{bar}:events");
        var added = queue.Streams.Add("{foo}:events", ("value", "value"));
        var count = queue.Streams.Count("{foo}:events");
        var range = queue.Streams.Range("{foo}:events");
        _ = queue.Streams.Remove("{foo}:events", "1-0");
        _ = queue.Streams.TrimByMaxLength("{foo}:events", 1);
        _ = queue.Streams.Acknowledge("{foo}:events", "workers", "1-0");
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(added.Result.ToString()).IsEqualTo("1-0");
        await Assert.That(count.Result).IsEqualTo(1);
        await Assert.That(range.Result).IsEmpty();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "CLUSTER SLOTS" });
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(new[]
            {
                "XADD tenant:{foo}:events * value value", "XLEN tenant:{foo}:events", "XRANGE tenant:{foo}:events - +",
                "XDEL tenant:{foo}:events 1-0", "XTRIM tenant:{foo}:events MAXLEN 1", "XACK tenant:{foo}:events workers 1-0",
            }, CollectionOrdering.Matching);
        if (otherCount is not null)
        {
            await Assert.That(otherCount.Result).IsEqualTo(9);
            await Assert.That(other.ReceivedCommands).IsEquivalentTo(new[] { "XLEN tenant:{bar}:events" });
        }
        else await Assert.That(other.ReceivedCommands).IsEmpty();
    }

    private static byte[][] WrapReplies(byte[][] replies, bool transactional)
        => !transactional ? replies : [FakeRespServer.OkReply,
            .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), replies.Length),
            [.. Encoding.UTF8.GetBytes($"*{replies.Length}\r\n"), .. replies.SelectMany(reply => reply)]];
}
