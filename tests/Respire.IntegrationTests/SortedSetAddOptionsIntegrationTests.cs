using FluentAssertions;
using TUnit.Core;
using O = Respire.RespireSortedSetAddOptions;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SortedSetAddOptionsIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, "immediate")]
    [Arguments(2, "batch")]
    [Arguments(2, "transaction")]
    [Arguments(3, "immediate")]
    [Arguments(3, "batch")]
    [Arguments(3, "transaction")]
    public async Task ConditionsChangeCountsAndNullableIncrements(int protocol, string mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"zadd:{Guid.NewGuid():N}:");
        async Task<bool> Add(O options, double score, string member = "a")
            => await Run(view, mode, s => s.AddAsync("key", options, (RespireValue)member, score),
                s => s.Add("key", options, (RespireValue)member, score));
        async Task<double?> Increment(O options, double by, string member = "a")
            => await Run(view, mode, s => s.IncrementAsync("key", options, member, by),
                s => s.Increment("key", options, member, by));

        (await Add(O.Xx, 1)).Should().BeFalse();
        (await Add(O.Nx, 1)).Should().BeTrue();
        (await Add(O.Nx | O.Ch, 2)).Should().BeFalse();
        (await Add(O.Xx, 2)).Should().BeFalse(); // Updated, but not newly inserted.
        (await view.SortedSets.ScoreAsync("key", "a")).Should().Be(2);
        (await Add(O.Xx | O.Ch, 2)).Should().BeFalse();
        (await Add(O.Xx | O.Gt | O.Ch, 1)).Should().BeFalse();
        (await Add(O.Xx | O.Gt | O.Ch, 3)).Should().BeTrue();
        (await Add(O.Xx | O.Lt | O.Ch, 4)).Should().BeFalse();
        (await Add(O.Xx | O.Lt | O.Ch, 2)).Should().BeTrue();
        (await Add(O.Gt, 7, "b")).Should().BeTrue(); // GT/LT allow new members.
        (await Add(O.Lt, 8, "c")).Should().BeTrue();
        (await Run(view, mode, s => s.AddAsync("key", O.Ch, ("a", 4), ("b", 7), ("d", 9)),
            s => s.Add("key", O.Ch, ("a", 4), ("b", 7), ("d", 9)))).Should().Be(2);
        (await Increment(O.Nx, 10)).Should().BeNull();
        (await Increment(O.Xx, 1, "missing")).Should().BeNull();
        (await Increment(O.Xx | O.Gt, -1)).Should().BeNull();
        (await Increment(O.Xx | O.Lt, 1)).Should().BeNull();
        (await Increment(O.Xx | O.Gt | O.Ch, 1.5)).Should().Be(5.5);
        (await Increment(O.Xx | O.Lt, -0.5)).Should().Be(5);
        (await Increment(O.Nx, 2, "new")).Should().Be(2);
        (await view.SortedSets.ScoreAsync("key", "a")).Should().Be(5);
        await view.DeleteAsync("key");
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task WritesInvalidateCachedScores_AndPreserveTypedMembers(string mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var view = client.WithKeyPrefix($"zadd-cache:{Guid.NewGuid():N}:");
        (await Run(view, mode, s => s.AddAsync<bool>("key", O.Nx, true, 1), s => s.Add<bool>("key", O.Nx, true, 1)))
            .Should().BeTrue();
        (await view.SortedSets.ScoreAsync("key", "1")).Should().Be(1);
        (await view.SortedSets.ScoreAsync("key", "1")).Should().Be(1);
        (await Run(view, mode, s => s.AddAsync("key", O.Xx | O.Ch, ("1", 2)), s => s.Add("key", O.Xx | O.Ch, ("1", 2))))
            .Should().Be(1);
        (await view.SortedSets.ScoreAsync("key", "1")).Should().Be(2);
        (await Run(view, mode, s => s.IncrementAsync("key", O.Xx, "1", 1), s => s.Increment("key", O.Xx, "1", 1)))
            .Should().Be(3);
        (await view.SortedSets.ScoreAsync("key", "1")).Should().Be(3);
        await view.DeleteAsync("key");
    }

    private static async Task<T> Run<T>(IRespireClient client, string mode,
        Func<ISortedSetCommands, ValueTask<T>> immediate, Func<IBatchSortedSetCommands, RespirePending<T>> deferred)
    {
        if (mode == "immediate") return await immediate(client.SortedSets);
        if (mode == "batch")
        {
            using var batch = client.CreateBatch();
            var pending = deferred(batch.SortedSets);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = deferred(transaction.SortedSets);
        await transaction.CommitAsync();
        return result.Result;
    }
}
