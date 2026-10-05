using System.Text;
using Respire.Internal;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetCardinalityTests
{
    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task CountsWriteOptionsAfterAllPrefixedKeys(string mode)
    {
        var expected = new long[] { 2, 1, 5, 3, 5, 2 };
        await using var server = new FakeRespServer(Replies(mode, expected));
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = root.WithKeyPrefix("tenant:");
        RespireKey[] keys = ["first", "second"];
        var actual = new[]
        {
            await Count(client, mode, false, keys),
            await Count(client, mode, false, keys, 1),
            await Count(client, mode, true, keys),
            await Count(client, mode, true, keys, 3),
            await Count(client, mode, true, keys, approximate: true),
            await Count(client, mode, true, keys, 2, approximate: true),
        };
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
        await Assert.That(server.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC").SequenceEqual(
        [
            "SDIFFCARD 2 tenant:first tenant:second",
            "SDIFFCARD 2 tenant:first tenant:second LIMIT 1",
            "SUNIONCARD 2 tenant:first tenant:second",
            "SUNIONCARD 2 tenant:first tenant:second LIMIT 3",
            "SUNIONCARD 2 tenant:first tenant:second APPROX",
            "SUNIONCARD 2 tenant:first tenant:second APPROX LIMIT 2",
        ])).IsTrue();
    }

    [Test]
    public async Task InvalidCountsAndEmptyKeysNeverSendOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.Sets.DifferenceCountAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sets.UnionCountAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Sets.DifferenceCountAsync(-1, "key")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Sets.UnionCountAsync(new RespireSetUnionCountOptions(-1), "key")).Throws<ArgumentOutOfRangeException>();
        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
        {
            await Assert.That(() => queue.Sets.DifferenceCount([])).Throws<ArgumentException>();
            await Assert.That(() => queue.Sets.UnionCount([])).Throws<ArgumentException>();
            await Assert.That(() => queue.Sets.DifferenceCount(-1, "key")).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => queue.Sets.UnionCount(new RespireSetUnionCountOptions(-1), "key")).Throws<ArgumentOutOfRangeException>();
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task DeferredKeySequenceIsSnapshottedAndBinarySafe()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] binary = [0xff, 0, 0x80];
        RespireKey[] keys = [binary];
        using var batch = client.CreateBatch();
        var difference = batch.Sets.DifferenceCount(keys);
        var union = batch.Sets.UnionCount(keys);
        keys[0] = "changed";
        await batch.ExecuteAsync();
        await Assert.That(difference.Result).IsEqualTo(1);
        await Assert.That(union.Result).IsEqualTo(1);
        foreach (var arguments in server.ReceivedArguments)
            await Assert.That(arguments[2].SequenceEqual(binary)).IsTrue();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ClusterRoutesPastCountAndRejectsMismatchedSlots(string mode)
    {
        await using var owner = new FakeRespServer(Replies(mode, [2, 5]));
        var slot = ClusterHash.GetSlot("tenant:{sets}:first");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var client = root.WithKeyPrefix("tenant:");
        await Assert.That(await Count(client, mode, false, ["{sets}:first", "{sets}:second"])).IsEqualTo(2);
        await Assert.That(await Count(client, mode, true, ["{sets}:first", "{sets}:second"])).IsEqualTo(5);
        foreach (var union in new[] { false, true })
            await Assert.That(async () => await Count(client, mode, union, ["{a}:first", "{b}:second"]))
                .Throws<RespireServerException>();
        await Assert.That(seed.ReceivedCommands.SequenceEqual(["CLUSTER SLOTS"])).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationReachesThePendingRead(bool union)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = union ? client.Sets.UnionCountAsync(new(2, true), ["key"], cancellation.Token)
            : client.Sets.DifferenceCountAsync(2, ["key"], cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    private static byte[][] Replies(string mode, long[] values)
        => values.SelectMany(value => mode == "transaction"
            ? new[] { FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), Encoding.ASCII.GetBytes($"*1\r\n:{value}\r\n") }
            : [Encoding.ASCII.GetBytes($":{value}\r\n")]).ToArray();

    private static async Task<long> Count(IRespireClient client, string mode, bool union, RespireKey[] keys,
        long limit = 0, bool approximate = false)
    {
        if (mode == "immediate") return union ? await client.Sets.UnionCountAsync(new(limit, approximate), keys)
            : await client.Sets.DifferenceCountAsync(limit, keys);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = union ? queue.Sets.UnionCount(new(limit, approximate), keys) : queue.Sets.DifferenceCount(limit, keys);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
