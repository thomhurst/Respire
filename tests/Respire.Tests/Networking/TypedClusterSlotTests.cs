using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TypedClusterSlotTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HyperLogLogRejectsMixedSlotsBeforeSendingOrQueueing(bool includeKeyContext)
    {
        await using var server = new FakeRespServer();
        await using var root = await ConnectClusterAsync(server, includeKeyContext);
        var client = root.WithKeyPrefix("tenant:");
        RespireKey expected = client.ResolveKey("{left}:first");
        RespireKey conflicting = client.ResolveKey("{right}:second");

        await AssertRejectedAsync(async () => await client.HyperLogLog.CountAsync("{left}:first", "{right}:second"),
            "PFCOUNT", expected, conflicting, includeKeyContext);
        await AssertRejectedAsync(async () => await client.HyperLogLog.MergeAsync(
                "{left}:output", "{left}:first", "{right}:second"),
            "PFMERGE", expected, conflicting, includeKeyContext);

        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();

        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
        {
            await AssertRejected(() => queue.HyperLogLog.Count("{left}:first", "{right}:second"),
                "PFCOUNT", expected, conflicting, includeKeyContext);
            await AssertRejected(() => queue.HyperLogLog.Merge("{left}:output", "{right}:second"),
                "PFMERGE", expected, conflicting, includeKeyContext);
        }

        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task SetAlgebraRejectsMixedSlotsBeforeSendingOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var root = await ConnectClusterAsync(server, includeKeyContext: true);
        var client = root.WithKeyPrefix("tenant:");
        const string Left = "{left}:source";
        const string Right = "{right}:source";
        const string Destination = "{left}:output";
        RespireKey expected = client.ResolveKey(Left);
        RespireKey conflicting = client.ResolveKey(Right);

        (string Operation, Func<Task> Invoke)[] immediate =
        [
            ("SINTER", async () => await client.Sets.IntersectAsync(Left, Right)),
            ("SUNION", async () => await client.Sets.UnionAsync(Left, Right)),
            ("SDIFF", async () => await client.Sets.DifferenceAsync(Left, Right)),
            ("SINTERSTORE", async () => await client.Sets.IntersectStoreAsync(Destination, Right)),
            ("SUNIONSTORE", async () => await client.Sets.UnionStoreAsync(Destination, Right)),
            ("SDIFFSTORE", async () => await client.Sets.DifferenceStoreAsync(Destination, Right)),
            ("SINTERCARD", async () => await client.Sets.IntersectCountAsync(1, Left, Right)),
            ("SMOVE", async () => await client.Sets.MoveAsync(Left, Right, "member")),
        ];

        foreach (var (operation, invoke) in immediate)
        {
            await AssertRejectedAsync(invoke, operation, expected, conflicting, includeKeyContext: true);
        }

        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();

        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
        {
            (string Operation, Action Invoke)[] deferred =
            [
                ("SINTER", () => queue.Sets.Intersect(Left, Right)),
                ("SUNION", () => queue.Sets.Union(Left, Right)),
                ("SDIFF", () => queue.Sets.Difference(Left, Right)),
                ("SINTERSTORE", () => queue.Sets.IntersectStore(Destination, Right)),
                ("SUNIONSTORE", () => queue.Sets.UnionStore(Destination, Right)),
                ("SDIFFSTORE", () => queue.Sets.DifferenceStore(Destination, Right)),
                ("SINTERCARD", () => queue.Sets.IntersectCount(1, Left, Right)),
                ("SMOVE", () => queue.Sets.Move(Left, Right, "member")),
            ];

            foreach (var (operation, invoke) in deferred)
            {
                await AssertRejected(invoke, operation, expected, conflicting, includeKeyContext: true);
            }
        }

        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task HashTagInPrefixAllowsSameSlotCommandsAndKeepsBatchUsable()
    {
        await using var server = new FakeRespServer();
        await using var root = await ConnectClusterAsync(server, includeKeyContext: false);
        var client = root.WithKeyPrefix("{tenant}:");
        var topology = server.ReplyOverride!;
        server.ReplyOverride = (connection, command) => command.Split(' ')[0] switch
        {
            "PFCOUNT" => ":2\r\n"u8.ToArray(),
            "PFMERGE" => FakeRespServer.OkReply,
            "SINTERCARD" or "SINTERSTORE" => ":1\r\n"u8.ToArray(),
            _ => topology(connection, command)
        };

        await Assert.That(await client.HyperLogLog.CountAsync("{left}:first", "{right}:second")).IsEqualTo(2);
        await Assert.That(await client.Sets.IntersectCountAsync(1, "{left}:first", "{right}:second")).IsEqualTo(1);

        using (var rejected = root.CreateBatch())
        {
            await Assert.That(() => rejected.HyperLogLog.Count("{left}:first", "{right}:second"))
                .ThrowsExactly<RespireCrossSlotException>();
            var valid = rejected.HyperLogLog.Count("{right}:second");
            await rejected.ExecuteAsync();
            await Assert.That(valid.Result).IsEqualTo(2);
        }

        using var batch = client.CreateBatch();
        var merge = batch.HyperLogLog.Merge("{left}:output", "{right}:second");
        var intersect = batch.Sets.IntersectStore("{left}:output", "{right}:second");
        await batch.ExecuteAsync();
        await Assert.That(merge.Result).IsTrue();
        await Assert.That(intersect.Result).IsEqualTo(1);

        await Assert.That(server.ReceivedCommands.SequenceEqual(
        [
            "CLUSTER SLOTS",
            "PFCOUNT {tenant}:{left}:first {tenant}:{right}:second",
            "SINTERCARD 2 {tenant}:{left}:first {tenant}:{right}:second LIMIT 1",
            "PFCOUNT {right}:second",
            "PFMERGE {tenant}:{left}:output {tenant}:{right}:second",
            "SINTERSTORE {tenant}:{left}:output {tenant}:{right}:second",
        ])).IsTrue();
    }

    private static ValueTask<RespireClient> ConnectClusterAsync(FakeRespServer server, bool includeKeyContext)
    {
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? topology
            : "-CROSSSLOT mixed slots reached the server\r\n"u8.ToArray();

        return RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            IncludeCrossSlotKeyContext = includeKeyContext,
        });
    }

    private static async Task AssertRejectedAsync(Func<Task> invoke, string operation, RespireKey expected,
        RespireKey conflicting, bool includeKeyContext)
    {
        var error = await Assert.That(invoke).ThrowsExactly<RespireCrossSlotException>();
        await AssertCrossSlot(error!, operation, expected, conflicting, includeKeyContext);
    }

    private static async Task AssertRejected(Action invoke, string operation, RespireKey expected,
        RespireKey conflicting, bool includeKeyContext)
    {
        var error = await Assert.That(invoke).ThrowsExactly<RespireCrossSlotException>();
        await AssertCrossSlot(error!, operation, expected, conflicting, includeKeyContext);
    }

    private static async Task AssertCrossSlot(RespireCrossSlotException error, string operation, RespireKey expected,
        RespireKey conflicting, bool includeKeyContext)
    {
        await Assert.That(error.CommandName).IsEqualTo(operation);
        await Assert.That(error.ExpectedSlot).IsEqualTo(expected.ClusterSlot);
        await Assert.That(error.ConflictingSlot).IsEqualTo(conflicting.ClusterSlot);
        await Assert.That(error.ConflictingKeyHex).IsEqualTo(includeKeyContext
            ? Convert.ToHexString(conflicting.ToBytes())
            : null);
    }
}
