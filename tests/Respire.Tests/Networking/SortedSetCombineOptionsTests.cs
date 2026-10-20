using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetCombineOptionsTests
{
    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task AllAggregatesAndWeights_SerializeAndParseResults(string mode)
    {
        foreach (var aggregate in Enum.GetValues<RespireSortedSetAggregate>())
        foreach (var weighted in new[] { false, true })
        {
            double[] weights = weighted ? [1.5, -2] : [];
            var options = new RespireSortedSetCombineOptions { Weights = weights, Aggregate = aggregate };
            var members = "*2\r\n$1\r\na\r\n$1\r\nb\r\n";
            byte[][] replies = mode == "transaction"
                ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                    "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                    System.Text.Encoding.UTF8.GetBytes("*4\r\n" + members + members + ":2\r\n:2\r\n")]
                : [System.Text.Encoding.UTF8.GetBytes(members), System.Text.Encoding.UTF8.GetBytes(members),
                    ":2\r\n"u8.ToArray(), ":2\r\n"u8.ToArray()];
            await using var server = new FakeRespServer(replies);
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var view = client.WithKeyPrefix("tenant:");
            if (mode == "immediate")
            {
                await Assert.That(await view.SortedSets.UnionAsync(["first", "second"], options)).IsEquivalentTo(new[] { "a", "b" });
                await Assert.That(await view.SortedSets.IntersectAsync(["first", "second"], options)).IsEquivalentTo(new[] { "a", "b" });
                await Assert.That(await view.SortedSets.UnionStoreAsync("dest", ["first", "second"], options)).IsEqualTo(2);
                await Assert.That(await view.SortedSets.IntersectStoreAsync("dest", ["first", "second"], options)).IsEqualTo(2);
            }
            else
            {
                using var batch = view.CreateBatch();
                await using var transaction = view.CreateTransaction();
                IRespireCommandQueue queue = mode == "batch" ? batch : transaction;
                var union = queue.SortedSets.Union(["first", "second"], options);
                var inter = queue.SortedSets.Intersect(["first", "second"], options);
                var unionStore = queue.SortedSets.UnionStore("dest", ["first", "second"], options);
                var interStore = queue.SortedSets.IntersectStore("dest", ["first", "second"], options);
                // Deferred commands own the serialized weights, even if caller memory changes.
                if (weighted) weights[0] = 999;
                if (mode == "batch") await batch.ExecuteAsync();
                else await transaction.CommitAsync();
                await Assert.That(union.Result).IsEquivalentTo(new[] { "a", "b" });
                await Assert.That(inter.Result).IsEquivalentTo(new[] { "a", "b" });
                await Assert.That(unionStore.Result).IsEqualTo(2);
                await Assert.That(interStore.Result).IsEqualTo(2);
            }
            var suffix = "2 tenant:first tenant:second" + (weighted ? " WEIGHTS 1.5 -2" : "") + " AGGREGATE " + aggregate.ToString().ToUpperInvariant();
            await Assert.That(server.ReceivedCommands.Where(c => c.StartsWith('Z')).SequenceEqual(new[]
            {
                "ZUNION " + suffix, "ZINTER " + suffix,
                "ZUNIONSTORE tenant:dest " + suffix, "ZINTERSTORE tenant:dest " + suffix,
            })).IsTrue();
        }
    }

    [Test]
    public async Task CrossSlotKeys_RejectBeforeIoOnAllSurfaces()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = true,
            Endpoints = [new("localhost", 1)], Connections = 1,
        });
        var options = new RespireSortedSetCombineOptions { Weights = new double[] { 2, 3 }, Aggregate = RespireSortedSetAggregate.Count };
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.SortedSets.UnionAsync(["{a}:one", "{b}:two"], options)).Throws<RespireCrossSlotException>();
        await Assert.That(async () => await client.SortedSets.IntersectAsync(["{a}:one", "{b}:two"], options)).Throws<RespireCrossSlotException>();
        // Matching source slots still require the destination to share that slot.
        await Assert.That(async () => await client.SortedSets.UnionStoreAsync("{b}:dest", ["{a}:one", "{a}:two"], options)).Throws<RespireCrossSlotException>();
        await Assert.That(async () => await client.SortedSets.IntersectStoreAsync("{b}:dest", ["{a}:one", "{a}:two"], options)).Throws<RespireCrossSlotException>();
        foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
        {
            await Assert.That(() => queue.SortedSets.Union(["{a}:one", "{b}:two"], options)).Throws<RespireCrossSlotException>();
            await Assert.That(() => queue.SortedSets.Intersect(["{a}:one", "{b}:two"], options)).Throws<RespireCrossSlotException>();
            await Assert.That(() => queue.SortedSets.UnionStore("{b}:dest", ["{a}:one", "{a}:two"], options)).Throws<RespireCrossSlotException>();
            await Assert.That(() => queue.SortedSets.IntersectStore("{b}:dest", ["{a}:one", "{a}:two"], options)).Throws<RespireCrossSlotException>();
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidOptions_RejectBeforeDispatchOrQueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var options in new RespireSortedSetCombineOptions[]
        {
            null!, new() { Weights = new double[] { 1 } }, new() { Weights = new double[] { 1, 2, 3 } },
            new() { Aggregate = (RespireSortedSetAggregate)(-1) }, new() { Aggregate = (RespireSortedSetAggregate)4 },
        })
        {
            await Assert.That(async () => await client.SortedSets.UnionAsync(["a", "b"], options)).Throws<ArgumentException>();
            await Assert.That(async () => await client.SortedSets.IntersectAsync(["a", "b"], options)).Throws<ArgumentException>();
            await Assert.That(async () => await client.SortedSets.UnionStoreAsync("dest", ["a", "b"], options)).Throws<ArgumentException>();
            await Assert.That(async () => await client.SortedSets.IntersectStoreAsync("dest", ["a", "b"], options)).Throws<ArgumentException>();
            foreach (var queue in new IRespireCommandQueue[] { batch, transaction })
            {
                await Assert.That(() => queue.SortedSets.Union(["a", "b"], options)).Throws<ArgumentException>();
                await Assert.That(() => queue.SortedSets.Intersect(["a", "b"], options)).Throws<ArgumentException>();
                await Assert.That(() => queue.SortedSets.UnionStore("dest", ["a", "b"], options)).Throws<ArgumentException>();
                await Assert.That(() => queue.SortedSets.IntersectStore("dest", ["a", "b"], options)).Throws<ArgumentException>();
            }
        }
        await Assert.That(async () => await client.SortedSets.UnionAsync([], new RespireSortedSetCombineOptions())).Throws<ArgumentException>();
        await Assert.That(() => batch.SortedSets.Intersect([], new RespireSortedSetCombineOptions())).Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }
}
