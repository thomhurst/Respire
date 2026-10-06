using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamConsumerOptionTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task GroupOptionsAndClaimMetadataReachEverySurface(bool map, int surface)
    {
        var entry = "*4\r\n$3\r\n1-0\r\n*2\r\n$1\r\nf\r\n$1\r\nv\r\n:1250\r\n:7\r\n";
        var normal = "*2\r\n$3\r\n2-0\r\n*2\r\n$1\r\nf\r\n$1\r\nw\r\n";
        var reply = Encoding.ASCII.GetBytes((map ? "%2\r\n" : "*2\r\n*2\r\n")
            + "$14\r\ntenant:{s}:one\r\n*1\r\n" + entry
            + (map ? "" : "*2\r\n") + "$14\r\ntenant:{s}:two\r\n*1\r\n" + normal);
        byte[][] replies = surface == 2
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. reply], ":1\r\n"u8.ToArray()]
            : [reply, ":1\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var owner = Create(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        byte[] key = "{s}:one"u8.ToArray();
        (RespireKey Key, RespireStreamId After)[] streams = [(key, ">"), ("{s}:two", ">")];
        var options = new StreamReadOptions { Count = 2, NoAck = true, ClaimMinIdle = TimeSpan.FromMilliseconds(500) };
        RespireStreamReadResult[] result;
        if (surface == 0)
        {
            var pending = client.Streams.ReadGroupAsync(streams, "g", "c", options);
            Array.Fill(key, (byte)'x');
            result = await pending;
        }
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Streams.ReadGroup(streams, "g", "c", options);
            Array.Fill(key, (byte)'x');
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        await Assert.That(result.Length).IsEqualTo(2);
        var claimed = result[0].Entries[0];
        await Assert.That(claimed.PreviousIdleTime).IsEqualTo((TimeSpan?)TimeSpan.FromMilliseconds(1250));
        await Assert.That(claimed.PreviousDeliveryCount).IsEqualTo((long?)7);
        await Assert.That(claimed.GetString("f")).IsEqualTo("v");
        await Assert.That(result[1].Entries[0].PreviousIdleTime).IsNull();
        await Assert.That(result[1].Entries[0].PreviousDeliveryCount).IsNull();
        await Assert.That(await claimed.AckAsync()).IsTrue();
        await Assert.That(server.ReceivedCommands).Contains(
            "XREADGROUP GROUP g c COUNT 2 CLAIM 500 NOACK STREAMS tenant:{s}:one tenant:{s}:two > >");
        await Assert.That(server.ReceivedCommands).Contains("XACK tenant:{s}:one g 1-0");
    }

    [Test]
    public async Task PendingIdleAndClaimOptionsHaveExactWireShape()
    {
        byte[] empty = "*0\r\n"u8.ToArray();
        await using var server = new FakeRespServer(empty, empty, empty, empty);
        await using var owner = Create(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        await client.Streams.PendingAsync(new StreamPendingOptions
        { MinIdle = TimeSpan.FromMilliseconds(250), Start = "(1-0", End = "9-0", Count = 3, Consumer = "c" }, "events", "g");
        await client.Streams.ClaimAsync(new StreamClaimOptions
        { IdleTime = TimeSpan.FromMilliseconds(1250), RetryCount = 7, Force = true, LastId = "9-0" },
            "events", "g", "c", TimeSpan.FromMilliseconds(500), "1-0", "2-0");
        await client.Streams.ClaimIdsAsync(new StreamClaimOptions
        { DeliveryTime = DateTimeOffset.FromUnixTimeMilliseconds(123456), RetryCount = 0, Force = true, LastId = "10-0" },
            "events", "g", "c", TimeSpan.Zero, ["1-0"], CancellationToken.None);
        await client.Streams.ClaimIdsAsync("events", "g", "c", TimeSpan.Zero, "2-0");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "XPENDING tenant:events g IDLE 250 (1-0 9-0 3 c",
            "XCLAIM tenant:events g c 500 1-0 2-0 IDLE 1250 RETRYCOUNT 7 FORCE LASTID 9-0",
            "XCLAIM tenant:events g c 0 1-0 TIME 123456 RETRYCOUNT 0 FORCE JUSTID LASTID 10-0",
            "XCLAIM tenant:events g c 0 2-0 JUSTID",
        }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdOnlyClaimsOwnResultsAndSupportOldAutoClaimReplies(bool deletedIds)
    {
        var autoReply = Encoding.ASCII.GetBytes((deletedIds ? "*3" : "*2")
            + "\r\n$3\r\n9-0\r\n*1\r\n$3\r\n1-0\r\n" + (deletedIds ? "*1\r\n$3\r\n2-0\r\n" : ""));
        await using var server = new FakeRespServer("*1\r\n$3\r\n1-0\r\n"u8.ToArray(), autoReply);
        await using var client = Create(server.Port);
        var ids = await client.Streams.ClaimIdsAsync("events", "g", "c", TimeSpan.Zero, ["1-0"], CancellationToken.None);
        var page = await client.Streams.ClaimPendingIdsAsync("events", "g", "c", TimeSpan.FromMilliseconds(500), "3-0", 25);
        await Assert.That(ids).IsEquivalentTo(new RespireStreamId[] { "1-0" });
        await Assert.That(page.Ids).IsEquivalentTo(ids);
        await Assert.That(page.NextStart).IsEqualTo((RespireStreamId)"9-0");
        await Assert.That(page.DeletedIds).IsEquivalentTo(deletedIds ? new RespireStreamId[] { "2-0" } : []);
        await Assert.That(server.ReceivedCommands).Contains("XAUTOCLAIM events g c 500 3-0 COUNT 25 JUSTID");
    }

    [Test]
    public async Task InvalidOptionsFailBeforeIoOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = Create(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var options in new StreamReadOptions[] { new() { NoAck = true }, new() { ClaimMinIdle = TimeSpan.Zero } })
        {
            await Assert.That(async () => await client.Streams.ReadAsync(options, "events")).Throws<ArgumentException>();
            foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
                await Assert.That(() => queue.Streams.Read(options, "events")).Throws<ArgumentException>();
        }
        var negative = new StreamReadOptions { ClaimMinIdle = TimeSpan.FromTicks(-1) };
        await Assert.That(async () => await client.Streams.ReadGroupOnceAsync("events", "g", "c", negative)).Throws<ArgumentException>();
        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
            await Assert.That(() => queue.Streams.ReadGroup("events", "g", "c", negative)).Throws<ArgumentException>();
        foreach (var options in new StreamPendingOptions[] { new() { Count = 0 }, new() { MinIdle = TimeSpan.FromTicks(-1) } })
            await Assert.That(async () => await client.Streams.PendingAsync(options, "events", "g")).Throws<ArgumentException>();
        StreamClaimOptions[] invalid = [new() { IdleTime = TimeSpan.FromTicks(-1) }, new() { RetryCount = -1 },
            new() { DeliveryTime = DateTimeOffset.UnixEpoch.AddMilliseconds(-1) },
            new() { IdleTime = TimeSpan.Zero, DeliveryTime = DateTimeOffset.UnixEpoch }];
        foreach (var options in invalid)
        {
            await Assert.That(async () => await client.Streams.ClaimAsync(options, "events", "g", "c", TimeSpan.Zero, "1-0"))
                .Throws<ArgumentException>();
            await Assert.That(async () => await client.Streams.ClaimIdsAsync(options, "events", "g", "c", TimeSpan.Zero, "1-0"))
                .Throws<ArgumentException>();
        }
        await Assert.That(async () => await client.Streams.ClaimIdsAsync("events", "g", "c", TimeSpan.Zero, []))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Streams.ClaimPendingIdsAsync("events", "g", "c", TimeSpan.Zero, count: 0))
            .Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task IncompleteClaimMetadataIsRejected()
    {
        await using var server = new FakeRespServer(
            "*1\r\n*2\r\n$6\r\nevents\r\n*1\r\n*3\r\n$3\r\n1-0\r\n*0\r\n:100\r\n"u8.ToArray());
        await using var client = Create(server.Port);
        await Assert.That(async () => await client.Streams.ReadGroupOnceAsync("events", "g", "c",
            new StreamReadOptions { ClaimMinIdle = TimeSpan.Zero })).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingConsumerOptionsReachDedicatedConnection(bool noAck)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = Create(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var options = new StreamReadOptions
        { NoAck = noAck, ClaimMinIdle = TimeSpan.Zero, WaitFor = Timeout.InfiniteTimeSpan };
        var pending = client.Streams.ReadGroupOnceAsync("events", "g", "c", options, cancellationToken: cancel.Token).AsTask();
        await received.Task.WaitAsync(timeout.Token);
        cancel.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).Contains(
            "XREADGROUP GROUP g c BLOCK 0 CLAIM 0" + (noAck ? " NOACK" : "") + " STREAMS events >");
    }

    private static RespireClient Create(int port) => RespireClient.Create(new RespireOptions
    { Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", port)] });
}
