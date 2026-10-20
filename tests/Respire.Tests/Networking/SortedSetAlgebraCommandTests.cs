using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SortedSetAlgebraCommandTests
{
    [Test]
    public async Task SingleKeyDefaultCancellation_EmitsOnlyOneSourceKey()
    {
        var membersReply = "*1\r\n$3\r\none\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, membersReply, membersReply,
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        _ = await client.SortedSets.IntersectAsync("a", default);
        _ = await client.SortedSets.UnionAsync("a", default);
        _ = await client.SortedSets.DifferenceAsync("a", default);
        _ = await client.SortedSets.IntersectStoreAsync("dest", "a", default);
        _ = await client.SortedSets.UnionStoreAsync("dest", "a", default);
        _ = await client.SortedSets.DifferenceStoreAsync("dest", "a", default);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZINTER 1 a",
            "ZUNION 1 a",
            "ZDIFF 1 a",
            "ZINTERSTORE dest 1 a",
            "ZUNIONSTORE dest 1 a",
            "ZDIFFSTORE dest 1 a",
        });
    }

    [Test]
    [Arguments("ZINTER")]
    [Arguments("ZUNION")]
    [Arguments("ZDIFF")]
    [Arguments("ZINTERSTORE")]
    [Arguments("ZUNIONSTORE")]
    [Arguments("ZDIFFSTORE")]
    public async Task ImmediateAlgebra_RejectsCrossSlotBeforeDiscovery(string operation)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = true,
            Endpoints = [new("localhost", 1)], Connections = 1,
        });
        foreach (var (prefix, first, second) in new[]
        {
            ("", "{a}:one", "{b}:two"),
            ("tenant:", "{a}:one", "{b}:two"),
            // The prefix changes the first hash tag despite matching logical key tags.
            ("tenant:{", "one{same}", "two{same}"),
        })
        {
            var view = prefix.Length == 0 ? client : client.WithKeyPrefix(prefix);
            var error = (await Assert.That(() => ExecuteAlgebraAsync(view, operation, first, [first, second]))
                .ThrowsExactly<RespireCrossSlotException>())!;
            await Assert.That(error.CommandName).IsEqualTo(operation);
            await Assert.That(error.ExpectedSlot).IsEqualTo(ClusterHash.GetSlot(prefix + first));
            await Assert.That(error.ConflictingSlot).IsEqualTo(ClusterHash.GetSlot(prefix + second));
            if (operation.EndsWith("STORE", StringComparison.Ordinal))
            {
                // Matching source slots must also match the destination slot.
                var destinationError = (await Assert.That(() => ExecuteAlgebraAsync(view, operation, second, [first, first]))
                    .ThrowsExactly<RespireCrossSlotException>())!;
                await Assert.That(destinationError.ExpectedSlot).IsEqualTo(ClusterHash.GetSlot(prefix + second));
                await Assert.That(destinationError.ConflictingSlot).IsEqualTo(ClusterHash.GetSlot(prefix + first));
            }
        }
    }

    [Test]
    [Arguments("", "{sets}:first", "{sets}:second", "{sets}:dest")]
    [Arguments("tenant:", "{sets}:first", "{sets}:second", "{sets}:dest")]
    [Arguments("{tenant}:", "{a}:first", "{b}:second", "{c}:dest")]
    public async Task ImmediateAlgebra_DispatchesMappedKeysToSlotOwner(
        string prefix, string first, string second, string destination)
    {
        var slot = ClusterHash.GetSlot(prefix + first);
        await Assert.That(slot).IsNotEqualTo(ClusterHash.GetSlot("2"));
        await using var owner = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.Contains("STORE", StringComparison.Ordinal)
                ? ":1\r\n"u8.ToArray() : "*1\r\n$3\r\none\r\n"u8.ToArray(),
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(
            topology, "-ERR command reached seed instead of slot owner\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = true,
            Endpoints = [new("127.0.0.1", seed.Port)], Connections = 1,
        });
        var view = prefix.Length == 0 ? client : client.WithKeyPrefix(prefix);
        string[] operations = ["ZINTER", "ZUNION", "ZDIFF", "ZINTERSTORE", "ZUNIONSTORE", "ZDIFFSTORE"];
        foreach (var operation in operations)
        {
            await ExecuteAlgebraAsync(view, operation, destination, [first, second]);
        }
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.SequenceEqual(operations.Select(operation =>
            operation + (operation.EndsWith("STORE", StringComparison.Ordinal) ? $" {prefix}{destination}" : "")
            + $" 2 {prefix}{first} {prefix}{second}"))).IsTrue();
    }

    private static async Task ExecuteAlgebraAsync(
        IRespireClient client, string operation, string destination, RespireKey[] keys)
    {
        if (operation.EndsWith("STORE", StringComparison.Ordinal))
        {
            var result = operation switch
            {
                "ZINTERSTORE" => await client.SortedSets.IntersectStoreAsync(destination, keys),
                "ZUNIONSTORE" => await client.SortedSets.UnionStoreAsync(destination, keys),
                "ZDIFFSTORE" => await client.SortedSets.DifferenceStoreAsync(destination, keys),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            await Assert.That(result).IsEqualTo(1);
        }
        else
        {
            var result = operation switch
            {
                "ZINTER" => await client.SortedSets.IntersectAsync(keys),
                "ZUNION" => await client.SortedSets.UnionAsync(keys),
                "ZDIFF" => await client.SortedSets.DifferenceAsync(keys),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            await Assert.That(result).IsEquivalentTo(["one"]);
        }
    }

    [Test]
    public async Task ImmediateCommands_EmitMultiScoreAndAlgebraFrames()
    {
        var membersReply = "*2\r\n$3\r\none\r\n$3\r\ntwo\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            "*3\r\n$3\r\n1.5\r\n$-1\r\n:2\r\n"u8.ToArray(),
            membersReply, membersReply, membersReply,
            ":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var scores = await client.SortedSets.ScoresManyAsync("scores", "one", "missing", "two");
        _ = await client.SortedSets.IntersectAsync("first", "second");
        _ = await client.SortedSets.UnionAsync("first", "second");
        _ = await client.SortedSets.DifferenceAsync("first", "second");
        _ = await client.SortedSets.IntersectStoreAsync("dest", "first", "second");
        _ = await client.SortedSets.UnionStoreAsync("dest", "first", "second");
        _ = await client.SortedSets.DifferenceStoreAsync("dest", "first", "second");

        await Assert.That(scores).IsEquivalentTo(new double?[] { 1.5, null, 2 });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZMSCORE scores one missing two",
            "ZINTER 2 first second",
            "ZUNION 2 first second",
            "ZDIFF 2 first second",
            "ZINTERSTORE dest 2 first second",
            "ZUNIONSTORE dest 2 first second",
            "ZDIFFSTORE dest 2 first second",
        });
    }

    [Test]
    public async Task DeferredCommands_QueueMultiScoreAndAlgebraFrames()
    {
        var membersReply = "*1\r\n$3\r\none\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            "*2\r\n$1\r\n1\r\n$-1\r\n"u8.ToArray(),
            membersReply, membersReply, membersReply,
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var scores = batch.SortedSets.ScoresMany("scores", "one", "missing");
        _ = batch.SortedSets.Intersect("first", "second");
        _ = batch.SortedSets.Union("first", "second");
        _ = batch.SortedSets.Difference("first", "second");
        _ = batch.SortedSets.IntersectStore("dest", "first", "second");
        _ = batch.SortedSets.UnionStore("dest", "first", "second");
        _ = batch.SortedSets.DifferenceStore("dest", "first", "second");

        await batch.ExecuteAsync();

        await Assert.That(scores.Result).IsEquivalentTo(new double?[] { 1, null });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZMSCORE scores one missing",
            "ZINTER 2 first second",
            "ZUNION 2 first second",
            "ZDIFF 2 first second",
            "ZINTERSTORE dest 2 first second",
            "ZUNIONSTORE dest 2 first second",
            "ZDIFFSTORE dest 2 first second",
        });
    }

    [Test]
    public async Task TypedReads_DeserializeMembersAndPreserveScores()
    {
        var membersReply = "*2\r\n$1\r\n7\r\n$1\r\n9\r\n"u8.ToArray();
        var entriesReply = "*4\r\n$1\r\n7\r\n$3\r\n1.5\r\n$1\r\n9\r\n$1\r\n2\r\n"u8.ToArray();
        var pairedEntriesReply = "*2\r\n*2\r\n$1\r\n7\r\n,1.5\r\n*2\r\n$1\r\n9\r\n,2\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, membersReply, pairedEntriesReply, entriesReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var byRank = await client.SortedSets.RangeAsync<int>("typed");
        var rankScores = await client.SortedSets.RangeWithScoresAsync<int>("typed");
        var byScore = await client.SortedSets.RangeByScoreAsync<int>("typed", 1, 2);
        var scoreEntries = await client.SortedSets.RangeByScoreWithScoresAsync<int>(
            "typed", new RespireScoreRange(1, 2));
        var popped = await client.SortedSets.PopManyAsync<int>("typed", 2);

        await Assert.That(byRank).IsEquivalentTo(new[] { 7, 9 });
        await Assert.That(byScore).IsEquivalentTo(new[] { 7, 9 });
        var expected = new[] { new RespireSortedSetEntry<int>(7, 1.5), new RespireSortedSetEntry<int>(9, 2) };
        await Assert.That(rankScores).IsEquivalentTo(expected);
        await Assert.That(scoreEntries).IsEquivalentTo(expected);
        await Assert.That(popped).IsEquivalentTo(expected);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE typed 0 -1",
            "ZRANGE typed 0 -1 WITHSCORES",
            "ZRANGE typed 1 2 BYSCORE",
            "ZRANGE typed 1 2 BYSCORE WITHSCORES",
            "ZPOPMIN typed 2",
        });
    }

    [Test]
    public async Task DeferredTypedReads_DeserializeMembersAndPreserveScores()
    {
        var membersReply = "*2\r\n$1\r\n7\r\n$1\r\n9\r\n"u8.ToArray();
        var entriesReply = "*4\r\n$1\r\n7\r\n$3\r\n1.5\r\n$1\r\n9\r\n$1\r\n2\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            membersReply, entriesReply, membersReply, entriesReply, entriesReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var batch = client.CreateBatch();

        var byRank = batch.SortedSets.Range<int>("typed");
        var rankScores = batch.SortedSets.RangeWithScores<int>("typed");
        var byScore = batch.SortedSets.RangeByScore<int>("typed", 1, 2);
        var scoreEntries = batch.SortedSets.RangeByScoreWithScores<int>(
            "typed", new RespireScoreRange(1, 2));
        var popped = batch.SortedSets.PopMany<int>("typed", 2);

        await batch.ExecuteAsync();

        await Assert.That(byRank.Result).IsEquivalentTo(new[] { 7, 9 });
        await Assert.That(byScore.Result).IsEquivalentTo(new[] { 7, 9 });
        var expected = new[] { new RespireSortedSetEntry<int>(7, 1.5), new RespireSortedSetEntry<int>(9, 2) };
        await Assert.That(rankScores.Result).IsEquivalentTo(expected);
        await Assert.That(scoreEntries.Result).IsEquivalentTo(expected);
        await Assert.That(popped.Result).IsEquivalentTo(expected);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "ZRANGE typed 0 -1",
            "ZRANGE typed 0 -1 WITHSCORES",
            "ZRANGE typed 1 2 BYSCORE",
            "ZRANGE typed 1 2 BYSCORE WITHSCORES",
            "ZPOPMIN typed 2",
        });
    }
}
