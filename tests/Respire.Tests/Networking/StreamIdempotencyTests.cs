using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StreamIdempotencyTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task WireAndSnapshotsMatchAcrossSurfaces(int surface)
    {
        byte[][] replies = ["$3\r\n9-0\r\n"u8.ToArray(), "$3\r\n9-1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray()];
        if (surface == 2)
        {
            var executed = Encoding.ASCII.GetBytes("*5\r\n").Concat(replies.SelectMany(x => x)).ToArray();
            replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), replies.Length),
                executed];
        }
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = surface == 1 ? view.CreateBatch() : null;
        await using var transaction = surface == 2 ? view.CreateTransaction() : null;
        IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
        byte[] producer = [0, 255, 32], identity = [254, 0, 32], group = [0, 253], key = "events"u8.ToArray();
        var manual = new StreamAddOptions { Idempotency = StreamIdempotency.Manual(producer, identity), MaxLength = 10,
            ApproximateTrim = false, ReferencePolicy = StreamReferencePolicy.KeepReferences, CreateStream = false };
        var automatic = new StreamAddOptions { Id = "*", Idempotency = StreamIdempotency.Automatic(producer) };
        Array.Fill(producer, (byte)'x');
        Array.Fill(identity, (byte)'x');
        RespirePending<RespireStreamId?>? first = null, second = null;
        List<RespirePending<long>> pendingCounts = [];
        RespireStreamId? ownedId;
        if (queue is null)
        {
            ownedId = await view.Streams.AddAsync(key, manual, ("f", "v"));
            await Assert.That(await view.Streams.AddAsync(key, automatic, ("f", "v"))).IsEqualTo((RespireStreamId?)"9-1");
        }
        else
        {
            first = queue.Streams.Add(key, manual, ("f", "v"));
            second = queue.Streams.Add(key, automatic, ("f", "v"));
            ownedId = null;
        }
        RespireStreamId[] ids = ["1-0", "2-0"];
        foreach (var mode in Enum.GetValues<StreamNackMode>())
        {
            var options = new StreamNackOptions { RetryCount = 4, Force = true };
            if (queue is null)
                await Assert.That(await view.Streams.NegativeAcknowledgeAsync(key, group, mode, options, ids)).IsEqualTo(2 - (long)mode);
            else pendingCounts.Add(queue.Streams.NegativeAcknowledge(key, group, mode, options, ids));
        }
        Array.Fill(key, (byte)'x'); Array.Fill(group, (byte)'x'); Array.Fill(ids, (RespireStreamId)"99-0");
        if (transaction is not null) await transaction.CommitAsync();
        else if (batch is not null) await batch.ExecuteAsync();
        if (queue is not null)
        {
            ownedId = first!.Result;
            await Assert.That(second!.Result).IsEqualTo((RespireStreamId?)"9-1");
            await Assert.That(pendingCounts.Select(x => x.Result)).IsEquivalentTo(new long[] { 2, 1, 0 }, CollectionOrdering.Matching);
        }
        var commands = server.ReceivedArguments.Where(x => Encoding.ASCII.GetString(x[0]) is "XADD" or "XNACK").ToArray();
        byte[][] expected = ["XADD"u8.ToArray(), "tenant:events"u8.ToArray(), "NOMKSTREAM"u8.ToArray(), "KEEPREF"u8.ToArray(),
            "MAXLEN"u8.ToArray(), "10"u8.ToArray(), "IDMP"u8.ToArray(), [0, 255, 32], [254, 0, 32], "*"u8.ToArray(), "f"u8.ToArray(), "v"u8.ToArray()];
        await Assert.That(commands[0].Select(Convert.ToHexString)).IsEquivalentTo(expected.Select(Convert.ToHexString), CollectionOrdering.Matching);
        await Assert.That(commands[1][2]).IsEquivalentTo("IDMPAUTO"u8.ToArray());
        await Assert.That(commands[1][3]).IsEquivalentTo(new byte[] { 0, 255, 32 });
        for (var index = 0; index < 3; index++)
        {
            await Assert.That(commands[index + 2][2]).IsEquivalentTo(new byte[] { 0, 253 });
            await Assert.That(commands[index + 2].Where((_, position) => position != 2).Select(Encoding.ASCII.GetString))
                .IsEquivalentTo(new[] { "XNACK", "tenant:events", new[] { "SILENT", "FAIL", "FATAL" }[index], "IDS", "2", "1-0", "2-0", "RETRYCOUNT", "4", "FORCE" }, CollectionOrdering.Matching);
        }
        await client.DisposeAsync();
        await Assert.That(ownedId).IsEqualTo((RespireStreamId?)"9-0");
    }

    [Test]
    public async Task InvalidInputsNeverConnectOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions { Connections = 1, Endpoints = [new("127.0.0.1", server.Port)] });
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (RespireValue invalid in new RespireValue[] { default, "", Array.Empty<byte>() })
        {
            await Assert.That(() => StreamIdempotency.Automatic(invalid)).Throws<ArgumentException>();
            await Assert.That(() => StreamIdempotency.Manual("p", invalid)).Throws<ArgumentException>();
        }
        foreach (var id in new[] { "1-0", "1-*", "$" })
        {
            var options = new StreamAddOptions { Id = id, Idempotency = StreamIdempotency.Automatic("p") };
            await Assert.That(async () => await client.Streams.AddAsync("s", options, ("f", "v"))).Throws<ArgumentException>();
            await Assert.That(() => batch.Streams.Add("s", options, ("f", "v"))).Throws<ArgumentException>();
            await Assert.That(() => transaction.Streams.Add("s", options, ("f", "v"))).Throws<ArgumentException>();
        }
        foreach (RespireStreamId[] ids in new RespireStreamId[][] { [], ["$"], ["-"], ["+"], ["1-*"], ["1-"], ["bad"] })
        {
            await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, ids)).Throws<ArgumentException>();
            await Assert.That(() => batch.Streams.NegativeAcknowledge("s", "g", StreamNackMode.Fail, ids)).Throws<ArgumentException>();
        }
        await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", "g", (StreamNackMode)99, "1-0")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => transaction.Streams.NegativeAcknowledge("s", "g", StreamNackMode.Fail, new() { RetryCount = -1 }, "1-0")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", default, StreamNackMode.Fail, "1-0")).Throws<ArgumentNullException>();
        await Assert.That(batch.Count + transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        var manual = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("p", "i") };
        var equal = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("p"u8.ToArray(), "i") };
        await Assert.That(manual).IsEqualTo(equal);
        await Assert.That(manual.GetHashCode()).IsEqualTo(equal.GetHashCode());
        await Assert.That(manual == default).IsFalse();
        await Assert.That(manual == new StreamAddOptions { Idempotency = StreamIdempotency.Automatic("p") }).IsFalse();
    }

    [Test]
    [Arguments("*1\r\n:1\r\n")]
    [Arguments(":-1\r\n")]
    [Arguments("+1\r\n")]
    public async Task MalformedCountDoesNotPoisonFollowingReply(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, "1-0")).Throws<RespireProtocolException>();
        await Assert.That(await client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, "1-0")).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAndServerErrorsDoNotReplayWrites()
    {
        await using var server = new FakeRespServer("-ERR unsupported\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var options = new StreamAddOptions { Idempotency = StreamIdempotency.Automatic("p") };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await client.Streams.AddAsync("s", options, [("f", "v")], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, default, ["1-0"], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(async () => await client.Streams.AddAsync("s", options, ("f", "v"))).Throws<RespireServerException>();
        await Assert.That(async () => await client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, "1-0")).Throws<RespireServerException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task ClusterRoutesOnlyThePrefixedStreamKey()
    {
        await using var owner = new FakeRespServer("$3\r\n1-0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("{tenant}:events");
        await using var seed = new FakeRespServer(Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions { UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)] });
        var view = client.WithKeyPrefix("{tenant}:");
        await view.Streams.AddAsync("events", new StreamAddOptions { Idempotency = StreamIdempotency.Manual("{other}", "id") }, ("f", "v"));
        await view.Streams.NegativeAcknowledgeAsync("events", "{another}", StreamNackMode.Fail, "1-0");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "CLUSTER SLOTS" });
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(new[] { "XADD {tenant}:events IDMP {other} id * f v", "XNACK {tenant}:events {another} FAIL IDS 1 1-0" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CancellationAfterDispatchDoesNotResendNack()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => { if (!command.StartsWith("XNACK ")) return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var operation = client.Streams.NegativeAcknowledgeAsync("s", "g", StreamNackMode.Fail, default, ["1-0"], cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await operation.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "XNACK s g FAIL IDS 1 1-0", "XLEN s" }, CollectionOrdering.Matching);
    }
}
