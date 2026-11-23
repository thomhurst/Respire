using Respire.Protocol;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeSortedSetServerTests
{
    [Test]
    [Arguments(RespireProtocol.Resp2, "ZRANK", 0L)]
    [Arguments(RespireProtocol.Resp2, "ZREVRANK", 1L)]
    [Arguments(RespireProtocol.Resp3, "ZRANK", 0L)]
    [Arguments(RespireProtocol.Resp3, "ZREVRANK", 1L)]
    public async Task ScoredRanksUseNullArrayInResp2Async(RespireProtocol protocol, string command, long expectedRank)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = protocol });
#pragma warning restore RESP010
        await client.SortedSets.AddAsync("scores", ("present", 2.5), ("higher", 4));

        using RespireResult present = await client.ExecuteAsync(command, "scores", "present", "WITHSCORE");
        await Assert.That(present.Count).IsEqualTo(2);
        await Assert.That(present[0].AsInteger()).IsEqualTo(expectedRank);
        await Assert.That(present[1].AsDouble()).IsEqualTo(2.5);

        RespireDataType scoredNull = protocol == RespireProtocol.Resp2 ? RespireDataType.Array : RespireDataType.Null;
        RespireDataType plainNull = protocol == RespireProtocol.Resp2 ? RespireDataType.BulkString : RespireDataType.Null;

        foreach (string key in new[] { "scores", "absent" })
        {
            using RespireResult scored = await client.ExecuteAsync(command, key, "missing", "WITHSCORE");
            using RespireResult plain = await client.ExecuteAsync(command, key, "missing");
            await Assert.That(scored.NullWireType).IsEqualTo(scoredNull);
            await Assert.That(plain.NullWireType).IsEqualTo(plainNull);
        }
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task StoredSortedSetScoresReadBackAsPositiveZeroAsync(RespireProtocol protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using RespireClient client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = protocol });
#pragma warning restore RESP010
        using RespireResult added = await client.ExecuteAsync("ZADD", "scores", "-0", "negative", "0", "positive");
        await Assert.That(added.AsInteger()).IsEqualTo(2L);

        using RespireResult incremented = await client.ExecuteAsync("ZINCRBY", "scores", "-0", "incremented");
        await Assert.That(BitConverter.DoubleToInt64Bits(incremented.AsDouble())).IsEqualTo(long.MinValue);

        foreach (string member in new[] { "negative", "positive", "incremented" })
        {
            double? score = await client.SortedSets.ScoreAsync("scores", member);
            await Assert.That(score.HasValue).IsTrue();
            await Assert.That(BitConverter.DoubleToInt64Bits(score.GetValueOrDefault())).IsEqualTo(0L);
        }

        using RespireResult rank = await client.ExecuteAsync("ZRANK", "scores", "negative", "WITHSCORE");
        await Assert.That(BitConverter.DoubleToInt64Bits(rank[1].AsDouble())).IsEqualTo(0L);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MutationsKeepExpiryUntilTheLastMemberIsRemoved(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await client.SortedSets.AddAsync("key", ("a", 1), ("b", 2));

        using (var expiry = await client.ExecuteAsync("PEXPIRE", "key", 1000))
        {
        }

        await client.SortedSets.AddAsync("key", "a", 3);
        await client.SortedSets.IncrementAsync("key", "b", 2);
        await client.SortedSets.RemoveAsync("key", "a");
        await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.SortedSets.CountAsync("key")).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.SortedSets.CountAsync("key")).IsEqualTo(0);

        foreach (var command in new[] { "ZREM", "ZPOPMIN", "ZPOPMAX", "ZREMRANGEBYRANK", "ZREMRANGEBYSCORE", "ZREMRANGEBYLEX" })
        {
            await client.SortedSets.AddAsync("key", "a", 1);

            using (var expiry = await client.ExecuteAsync("PEXPIRE", "key", 1000))
            {
            }

            RespireValue[] args = command switch
            {
                "ZREM" => ["key", "a"],
                "ZPOPMIN" or "ZPOPMAX" => ["key"],
                "ZREMRANGEBYRANK" => ["key", 0, -1],
                "ZREMRANGEBYSCORE" => ["key", "-inf", "+inf"],
                _ => ["key", "-", "+"],
            };

            using (var removed = await client.ExecuteAsync(command, args))
            {
            }

            await Assert.That(await client.ExistsAsync("key")).IsFalse();
            await client.SortedSets.AddAsync("key", "a", 1);
            await Assert.That(await Number(client, "PTTL", "key")).IsEqualTo(-1);
            await client.Keys.DeleteAsync("key");
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InferredScalarAddsMatchLookupsAndQueuedUpdates(int protocol)
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        var id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var instant = new DateTimeOffset(2026, 9, 30, 12, 34, 56, TimeSpan.FromHours(2));
        var duration = TimeSpan.FromSeconds(42);

        await client.SortedSets.AddAsync("scores", id, 1);
        await client.SortedSets.AddAsync("scores", instant, 2);
        await client.SortedSets.AddAsync("scores", duration, 3);

        await Assert.That(await client.SortedSets.ScoreAsync("scores", id)).IsEqualTo(1d);
        await Assert.That(await client.SortedSets.ScoreAsync("scores", instant)).IsEqualTo(2d);
        await Assert.That(await client.SortedSets.ScoreAsync("scores", duration)).IsEqualTo(3d);
        await Assert.That(await client.SortedSets.RankAsync("scores", id)).IsEqualTo(0L);

        using var batch = client.CreateBatch();
        var idAdded = batch.SortedSets.Add("scores", id, 4);
        var instantAdded = batch.SortedSets.Add("scores", instant, 5);
        var durationAdded = batch.SortedSets.Add("scores", duration, 6);
        (await batch.ExecuteAsync()).ThrowIfAnyFailed();

        await Assert.That(idAdded.Result).IsFalse();
        await Assert.That(instantAdded.Result).IsFalse();
        await Assert.That(durationAdded.Result).IsFalse();
        await Assert.That(await client.SortedSets.CountAsync("scores")).IsEqualTo(3L);
        await Assert.That(await client.SortedSets.ScoreAsync("scores", id)).IsEqualTo(4d);

        await client.SortedSets.AddAsync<Guid>("serialized", id, 1);

        await Assert.That(await client.SortedSets.ScoreAsync("serialized", $"\"{id:D}\"")).IsEqualTo(1d);
    }

    [Test]
    public async Task ConcurrentIncrementsAndPopsHaveSingleOwners()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var first = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var second = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        var increments = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).SortedSets.IncrementAsync("counter", "member", 1).AsTask()));
        await Assert.That(increments.Order()).IsEquivalentTo(Enumerable.Range(1, 100).Select(value => (double)value));
        await first.SortedSets.AddAsync("pops", Enumerable.Range(0, 100).Select(value => ((RespireValue)value, (double)value)).ToArray());
        var pops = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).SortedSets.PopAsync<int>("pops").AsTask()));
        await Assert.That(pops.Select(entry => entry!.Value.Member).Order()).IsEquivalentTo(Enumerable.Range(0, 100));
        await Assert.That(await first.ExistsAsync("pops")).IsFalse();
    }

    [Test]
    public async Task UnsupportedSortedSetVariantsFailWithoutChangingMembers()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This client belongs to the scenario's isolated fake server and is disposed with the test.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await client.SortedSets.AddAsync("key", "member", 1);

        foreach (var (command, args) in new (string, RespireValue[])[]
        {
            ("ZRANDMEMBER", ["key"]), ("ZMPOP", [1, "key", "MIN"]),
            ("BZMPOP", [0, 1, "key", "MIN"]), ("BZPOPMIN", ["key", 0]), ("BZPOPMAX", ["key", 0]),
            ("ZUNION", [1, "key"]), ("ZINTER", [1, "key"]), ("ZDIFF", [1, "key"]),
            ("ZUNIONSTORE", ["destination", 1, "key"]), ("ZINTERSTORE", ["destination", 1, "key"]),
            ("ZDIFFSTORE", ["destination", 1, "key"]), ("ZRANGESTORE", ["destination", "key", 0, -1]),
        })
        {
            var error = await Assert.That(async () =>
            {
                using var ignored = await client.ExecuteAsync(command, args);
            }).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
        }

        await Assert.That(async () =>
        {
            using var ignored = await client.ExecuteAsync("ZRANK", "key", "member", "INVALID");
        }).Throws<RespireServerException>();
        await Assert.That(await client.SortedSets.ScoreAsync("key", "member")).IsEqualTo(1);
        await Assert.That(await client.ExistsAsync("destination")).IsFalse();
    }

    private static async Task<long> Number(IRespireClient client, string command, params RespireValue[] args)
    {
        using var result = await client.ExecuteAsync(command, args);
        return result.AsInteger();
    }
}
