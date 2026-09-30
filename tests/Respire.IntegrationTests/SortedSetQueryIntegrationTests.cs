using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SortedSetQueryIntegrationTests(RedisTestContainer fixture)
{
    public enum QueryMode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, QueryMode.Immediate)]
    [Arguments(2, QueryMode.Batch)]
    [Arguments(2, QueryMode.Transaction)]
    [Arguments(3, QueryMode.Immediate)]
    [Arguments(3, QueryMode.Batch)]
    [Arguments(3, QueryMode.Transaction)]
    public async Task RandomMembers_PreserveCountsScoresAndMissingResults(int protocol, QueryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"query:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("key", ("a", 1), ("b", 2));
        (await Run(view, mode, s => s.RandomMemberAsync("missing"), s => s.RandomMember("missing"))).Should().BeNull();
        (await Run(view, mode, s => s.RandomMemberAsync("key"), s => s.RandomMember("key"))).Should().BeOneOf("a", "b");
        (await Run(view, mode, s => s.RandomMembersAsync("key", 20), s => s.RandomMembers("key", 20))).Should().BeEquivalentTo(new[] { "a", "b" });
        (await Run(view, mode, s => s.RandomMembersAsync("key", 0), s => s.RandomMembers("key", 0))).Should().BeEmpty();
        (await Run(view, mode, s => s.RandomMembersAsync("missing", -5), s => s.RandomMembers("missing", -5))).Should().BeEmpty();
        var repeated = await Run(view, mode, s => s.RandomMembersAsync("key", -5), s => s.RandomMembers("key", -5));
        repeated.Should().HaveCount(5).And.OnlyContain(member => member == "a" || member == "b");
        repeated.Distinct().Count().Should().BeLessThan(repeated.Length);
        var scores = await Run(view, mode, s => s.RandomMembersWithScoresAsync("key", -5), s => s.RandomMembersWithScores("key", -5));
        scores.Should().HaveCount(5).And.OnlyContain(entry => entry == new SortedSetEntry("a", 1) || entry == new SortedSetEntry("b", 2));
        (await Run(view, mode, s => s.RandomMembersWithScoresAsync("key", 20), s => s.RandomMembersWithScores("key", 20)))
            .Should().BeEquivalentTo(new[] { new SortedSetEntry("a", 1), new SortedSetEntry("b", 2) });
        (await Run(view, mode, s => s.RandomMembersWithScoresAsync("missing", 2), s => s.RandomMembersWithScores("missing", 2))).Should().BeEmpty();
        (await view.SortedSets.CountAsync("key")).Should().Be(2);
    }

    [Test]
    [Arguments(2, QueryMode.Immediate)]
    [Arguments(2, QueryMode.Batch)]
    [Arguments(2, QueryMode.Transaction)]
    [Arguments(3, QueryMode.Immediate)]
    [Arguments(3, QueryMode.Batch)]
    [Arguments(3, QueryMode.Transaction)]
    public async Task TypedRandomMembers_PreserveBinaryMembersAndDeserializeValues(int protocol, QueryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"typed:{Guid.NewGuid():N}:");
        byte[] bytes = [0xff, 0, 0x80];
        await view.SortedSets.AddAsync("binary", (RespireValue)bytes, 1.5);
        (await Run(view, mode, s => s.RandomMemberAsync<byte[]>("binary"), s => s.RandomMember<byte[]>("binary"))).Should().Equal(bytes);
        var members = await Run(view, mode, s => s.RandomMembersAsync<byte[]>("binary", -3), s => s.RandomMembers<byte[]>("binary", -3));
        members.Should().HaveCount(3);
        foreach (var member in members) member.Should().Equal(bytes);
        var scored = await Run(view, mode, s => s.RandomMembersWithScoresAsync<byte[]>("binary", -3), s => s.RandomMembersWithScores<byte[]>("binary", -3));
        scored.Should().HaveCount(3);
        foreach (var entry in scored) { entry.Member.Should().Equal(bytes); entry.Score.Should().Be(1.5); }
        members[0][0] = 0;
        members[1].Should().Equal(bytes);
        await view.SortedSets.AddAsync<int>("number", 42, 9);
        (await Run(view, mode, s => s.RandomMemberAsync<int>("number"), s => s.RandomMember<int>("number"))).Should().Be(42);
        (await Run(view, mode, s => s.RandomMemberAsync<byte[]>("missing"), s => s.RandomMember<byte[]>("missing"))).Should().BeNull();
    }

    [Test]
    [Arguments(2, QueryMode.Immediate)]
    [Arguments(2, QueryMode.Batch)]
    [Arguments(2, QueryMode.Transaction)]
    [Arguments(3, QueryMode.Immediate)]
    [Arguments(3, QueryMode.Batch)]
    [Arguments(3, QueryMode.Transaction)]
    public async Task LexRanges_RespectInclusiveExclusiveInfiniteAndEmptyBounds(int protocol, QueryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"lex:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("key", ("", 0), ("a", 0), ("b", 0), ("c", 0));
        var range = new RespireLexRange(RespireLexBound.Exclusive("a"), RespireLexBound.Inclusive("c"));
        (await Run(view, mode, s => s.CountByLexAsync("key", range), s => s.CountByLex("key", range))).Should().Be(2);
        (await Run(view, mode, s => s.CountByLexAsync("missing", RespireLexRange.All), s => s.CountByLex("missing", RespireLexRange.All))).Should().Be(0);
        var empty = new RespireLexRange("z", "a");
        (await Run(view, mode, s => s.CountByLexAsync("key", empty), s => s.CountByLex("key", empty))).Should().Be(0);
        (await Run(view, mode, s => s.RemoveRangeByLexAsync("key", range), s => s.RemoveRangeByLex("key", range))).Should().Be(2);
        (await view.SortedSets.RangeAsync("key")).Should().Equal("", "a");
        var emptyMember = new RespireLexRange("", "");
        (await Run(view, mode, s => s.RemoveRangeByLexAsync("key", emptyMember), s => s.RemoveRangeByLex("key", emptyMember))).Should().Be(1);
        (await Run(view, mode, s => s.RemoveRangeByLexAsync("key", RespireLexRange.All), s => s.RemoveRangeByLex("key", RespireLexRange.All))).Should().Be(1);
        (await Run(view, mode, s => s.RemoveRangeByLexAsync("key", RespireLexRange.All), s => s.RemoveRangeByLex("key", RespireLexRange.All))).Should().Be(0);
    }

    [Test]
    [Arguments(2, QueryMode.Immediate)]
    [Arguments(2, QueryMode.Batch)]
    [Arguments(2, QueryMode.Transaction)]
    [Arguments(3, QueryMode.Immediate)]
    [Arguments(3, QueryMode.Batch)]
    [Arguments(3, QueryMode.Transaction)]
    public async Task Intersection_CountsSharedMembersAndHonorsLimit(int protocol, QueryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"intersection:{Guid.NewGuid():N}:");
        await view.SortedSets.AddAsync("first", ("a", 1), ("b", 2));
        await view.SortedSets.AddAsync("second", ("a", 50), ("b", 60), ("c", 70));
        (await Run(view, mode, s => s.IntersectCountAsync("first", "second"), s => s.IntersectCount("first", "second"))).Should().Be(2);
        (await Run(view, mode, s => s.IntersectCountAsync(1, "first", "second"), s => s.IntersectCount(1, "first", "second"))).Should().Be(1);
        (await Run(view, mode, s => s.IntersectCountAsync(20, "first", "second"), s => s.IntersectCount(20, "first", "second"))).Should().Be(2);
        (await Run(view, mode, s => s.IntersectCountAsync("first", "missing"), s => s.IntersectCount("first", "missing"))).Should().Be(0);
        (await Run(view, mode, s => s.IntersectCountAsync("first", "first"), s => s.IntersectCount("first", "first"))).Should().Be(2);
    }

    [Test]
    [Arguments(2, QueryMode.Immediate)]
    [Arguments(2, QueryMode.Batch)]
    [Arguments(2, QueryMode.Transaction)]
    [Arguments(3, QueryMode.Immediate)]
    [Arguments(3, QueryMode.Batch)]
    [Arguments(3, QueryMode.Transaction)]
    public async Task WrongType_RemainsAServerError(int protocol, QueryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix($"wrong:{Guid.NewGuid():N}:");
        await view.SetAsync("key", "string");
        Func<Task>[] operations = [
            async () => { await Run(view, mode, s => s.RandomMemberAsync("key"), s => s.RandomMember("key")); },
            async () => { await Run(view, mode, s => s.RandomMembersAsync("key", 1), s => s.RandomMembers("key", 1)); },
            async () => { await Run(view, mode, s => s.RandomMembersWithScoresAsync("key", 1), s => s.RandomMembersWithScores("key", 1)); },
            async () => { await Run(view, mode, s => s.CountByLexAsync("key", RespireLexRange.All), s => s.CountByLex("key", RespireLexRange.All)); },
            async () => { await Run(view, mode, s => s.RemoveRangeByLexAsync("key", RespireLexRange.All), s => s.RemoveRangeByLex("key", RespireLexRange.All)); },
            async () => { await Run(view, mode, s => s.IntersectCountAsync("key"), s => s.IntersectCount("key")); },
        ];
        foreach (var operation in operations)
            (await operation.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("WRONGTYPE");
    }

    private static async Task<T> Run<T>(IRespireClient client, QueryMode mode,
        Func<ISortedSetCommands, ValueTask<T>> immediate, Func<IBatchSortedSetCommands, RespirePending<T>> deferred)
    {
        if (mode == QueryMode.Immediate) return await immediate(client.SortedSets);
        if (mode == QueryMode.Batch)
        {
            using var batch = client.CreateBatch();
            var result = deferred(batch.SortedSets);
            await batch.TryExecuteAsync();
            return result.Result;
        }
        await using var transaction = client.CreateTransaction();
        var pending = deferred(transaction.SortedSets);
        await transaction.CommitAsync();
        return pending.Result;
    }
}
