using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class MultiKeySortedSetPopIntegrationTests(RedisTestContainer fixture)
{
    public enum PopMode { Immediate, Batch, Transaction, Blocking }

    [Test]
    [Arguments(2, PopMode.Immediate, false)]
    [Arguments(2, PopMode.Immediate, true)]
    [Arguments(2, PopMode.Batch, false)]
    [Arguments(2, PopMode.Batch, true)]
    [Arguments(2, PopMode.Transaction, false)]
    [Arguments(2, PopMode.Transaction, true)]
    [Arguments(2, PopMode.Blocking, false)]
    [Arguments(2, PopMode.Blocking, true)]
    [Arguments(3, PopMode.Immediate, false)]
    [Arguments(3, PopMode.Immediate, true)]
    [Arguments(3, PopMode.Batch, false)]
    [Arguments(3, PopMode.Batch, true)]
    [Arguments(3, PopMode.Transaction, false)]
    [Arguments(3, PopMode.Transaction, true)]
    [Arguments(3, PopMode.Blocking, false)]
    [Arguments(3, PopMode.Blocking, true)]
    public async Task PopMany_SelectsFirstNonemptyKeyAndPreservesOrder(int protocol, PopMode mode, bool descending)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var view = client.WithKeyPrefix("Ã§Â§Å¸Ã¦Ë†Â·:");
        RespireKey selected = new byte[] { 0xff, 0, 0x42 };
        for (var i = 0; i < 4; i++)
            await view.SortedSets.AddAsync(selected, ((char)('a' + i)).ToString(), i + 1);
        await view.SortedSets.AddAsync("later", "untouched", 9);
        var popped = await PopMany(view, mode, ["missing", selected, "later"], 2, descending);
        popped.Should().NotBeNull();
        popped!.Value.Key.Should().Be(selected);
        popped.Value.Entries.Should().Equal(!descending ? [new SortedSetEntry("a", 1), new SortedSetEntry("b", 2)] : [new SortedSetEntry("d", 4), new SortedSetEntry("c", 3)]);
        (await view.SortedSets.CountAsync(popped.Value.Key)).Should().Be(2);
        (await view.SortedSets.CountAsync("later")).Should().Be(1);
        var remaining = await PopMany(view, mode, [selected], 20, descending);
        remaining!.Value.Entries.Should().Equal(!descending ? [new SortedSetEntry("c", 3), new SortedSetEntry("d", 4)] : [new SortedSetEntry("b", 2), new SortedSetEntry("a", 1)]);
        (await PopMany(view, mode, [selected], 1, descending)).Should().BeNull();
        (await client.Keys.ExistsAsync(selected)).Should().BeFalse();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task PopOne_ReturnsSelectedBinaryKeyAndTimeout(int protocol, bool descending)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var view = client.WithKeyPrefix("tenant:");
        RespireKey selected = new byte[] { 0xff, 0, 0x42 };
        await view.SortedSets.AddAsync(selected, "a", 1);
        await view.SortedSets.AddAsync(selected, "b", 2);
        await view.SortedSets.AddAsync("later", "untouched", 9);
        var result = await view.SortedSets.PopAsync(["missing", selected, "later"], TimeSpan.FromSeconds(1), descending);
        result.Should().NotBeNull();
        result!.Value.Key.Should().Be(selected);
        result.Value.Entry.Should().Be(!descending ? new SortedSetEntry("a", 1) : new SortedSetEntry("b", 2));
        (await view.SortedSets.CountAsync(result.Value.Key)).Should().Be(1);
        (await view.SortedSets.CountAsync("later")).Should().Be(1);
        (await view.SortedSets.PopAsync(["missing"], TimeSpan.FromMilliseconds(10), descending)).Should().BeNull();
        await view.SortedSets.AddAsync(RespireKey.Empty, "empty-key", 1);
        (await view.SortedSets.PopAsync([RespireKey.Empty], TimeSpan.FromSeconds(1), descending))!.Value.Key.Should().Be(RespireKey.Empty);
    }

    [Test]
    [Arguments(2, PopMode.Immediate, false)]
    [Arguments(2, PopMode.Immediate, true)]
    [Arguments(2, PopMode.Batch, false)]
    [Arguments(2, PopMode.Batch, true)]
    [Arguments(2, PopMode.Transaction, false)]
    [Arguments(2, PopMode.Transaction, true)]
    [Arguments(2, PopMode.Blocking, false)]
    [Arguments(2, PopMode.Blocking, true)]
    [Arguments(3, PopMode.Immediate, false)]
    [Arguments(3, PopMode.Immediate, true)]
    [Arguments(3, PopMode.Batch, false)]
    [Arguments(3, PopMode.Batch, true)]
    [Arguments(3, PopMode.Transaction, false)]
    [Arguments(3, PopMode.Transaction, true)]
    [Arguments(3, PopMode.Blocking, false)]
    [Arguments(3, PopMode.Blocking, true)]
    public async Task WrongType_IsPreserved(int protocol, PopMode mode, bool descending)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        await client.SetAsync("wrong", "text");
        Func<Task> many = async () => { await PopMany(client, mode, ["missing", "wrong"], 1, descending); };
        await many.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        if (mode == PopMode.Blocking)
        {
            Func<Task> one = async () => { await client.SortedSets.PopAsync(["missing", "wrong"], TimeSpan.FromSeconds(1), descending); };
            await one.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        }
    }

    [Test]
    [Arguments(2, PopMode.Immediate, false)]
    [Arguments(2, PopMode.Immediate, true)]
    [Arguments(2, PopMode.Batch, false)]
    [Arguments(2, PopMode.Batch, true)]
    [Arguments(2, PopMode.Transaction, false)]
    [Arguments(2, PopMode.Transaction, true)]
    [Arguments(2, PopMode.Blocking, false)]
    [Arguments(2, PopMode.Blocking, true)]
    [Arguments(3, PopMode.Immediate, false)]
    [Arguments(3, PopMode.Immediate, true)]
    [Arguments(3, PopMode.Batch, false)]
    [Arguments(3, PopMode.Batch, true)]
    [Arguments(3, PopMode.Transaction, false)]
    [Arguments(3, PopMode.Transaction, true)]
    [Arguments(3, PopMode.Blocking, false)]
    [Arguments(3, PopMode.Blocking, true)]
    public async Task TypedPops_PreserveMembersScoresAndNull(int protocol, PopMode mode, bool descending)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        var view = client.WithKeyPrefix("typed:");
        await view.SortedSets.AddAsync<int>("scores", 7, double.NegativeInfinity);
        await view.SortedSets.AddAsync<int>("scores", 9, double.PositiveInfinity);
        var result = await PopManyTyped(view, mode, ["missing", "scores"], descending);
        result!.Value.Key.Should().Be((RespireKey)"scores");
        result.Value.Entries.Should().Equal(descending
            ? [new SortedSetEntry<int>(9, double.PositiveInfinity), new SortedSetEntry<int>(7, double.NegativeInfinity)]
            : [new SortedSetEntry<int>(7, double.NegativeInfinity), new SortedSetEntry<int>(9, double.PositiveInfinity)]);
        (await PopManyTyped(view, mode, ["scores"], descending)).Should().BeNull();
        await view.SortedSets.AddAsync<int>("scores", 42, 1.5);
        var one = await view.SortedSets.PopAsync<int>(["missing", "scores"], TimeSpan.FromSeconds(1), descending);
        one!.Value.Key.Should().Be((RespireKey)"scores");
        one.Value.Entry.Should().Be(new SortedSetEntry<int>(42, 1.5));
        (await view.SortedSets.PopAsync<int>(["scores"], TimeSpan.Zero, descending)).Should().BeNull();
    }

    [Test]
    [Arguments(2, false, false)]
    [Arguments(2, false, true)]
    [Arguments(2, true, false)]
    [Arguments(2, true, true)]
    [Arguments(3, false, false)]
    [Arguments(3, false, true)]
    [Arguments(3, true, false)]
    [Arguments(3, true, true)]
    public async Task BlockingCancellation_PreservesOtherTraffic(int protocol, bool many, bool descending)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}&protocol={protocol}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Task pending = many
            ? client.SortedSets.PopManyAsync(["missing"], descending: descending,
                waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancellation.Token).AsTask()
            : client.SortedSets.PopAsync(["missing"], Timeout.InfiniteTimeSpan,
                descending, cancellation.Token).AsTask();
        await client.SetAsync("unrelated", "available");
        (await client.GetAsync<string>("unrelated")).Should().Be("available");
        Func<Task> wait = async () => await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await wait.Should().ThrowAsync<OperationCanceledException>();
        await client.SortedSets.AddAsync("ready", "member", 3);
        var next = await client.SortedSets.PopAsync(["ready"], TimeSpan.FromSeconds(1));
        next!.Value.Entry.Should().Be(new SortedSetEntry("member", 3));
    }

    private static async Task<RespireSortedSetPopManyResult<int>?> PopManyTyped(
        IRespireClient client, PopMode mode, RespireKey[] keys, bool descending)
    {
        if (mode is PopMode.Immediate or PopMode.Blocking)
            return await client.SortedSets.PopManyAsync<int>(keys, 2, descending, mode == PopMode.Blocking ? TimeSpan.Zero : null);
        if (mode == PopMode.Batch)
        {
            using var batch = client.CreateBatch();
            var pending = batch.SortedSets.PopMany<int>(keys, 2, descending);
            await batch.ExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.SortedSets.PopMany<int>(keys, 2, descending);
        await transaction.CommitAsync();
        return result.Result;
    }

    private static async Task<RespireSortedSetPopManyResult?> PopMany(
        IRespireClient client, PopMode mode, RespireKey[] keys, long count, bool descending)
    {
        if (mode is PopMode.Immediate or PopMode.Blocking)
            return await client.SortedSets.PopManyAsync(keys, count, descending, mode == PopMode.Blocking ? TimeSpan.FromMilliseconds(10) : null);
        if (mode == PopMode.Batch)
        {
            using var batch = client.CreateBatch();
            var pending = batch.SortedSets.PopMany(keys, count, descending);
            await batch.TryExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.SortedSets.PopMany(keys, count, descending);
        await transaction.CommitAsync();
        return result.Result;
    }
}
