using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class MultiKeyListPopIntegrationTests(RedisTestContainer fixture)
{
    public enum PopMode { Immediate, Batch, Transaction, Blocking }

    [Test]
    [Arguments(2, PopMode.Immediate, RespireListSide.Left)]
    [Arguments(2, PopMode.Immediate, RespireListSide.Right)]
    [Arguments(2, PopMode.Batch, RespireListSide.Left)]
    [Arguments(2, PopMode.Batch, RespireListSide.Right)]
    [Arguments(2, PopMode.Transaction, RespireListSide.Left)]
    [Arguments(2, PopMode.Transaction, RespireListSide.Right)]
    [Arguments(2, PopMode.Blocking, RespireListSide.Left)]
    [Arguments(2, PopMode.Blocking, RespireListSide.Right)]
    [Arguments(3, PopMode.Immediate, RespireListSide.Left)]
    [Arguments(3, PopMode.Immediate, RespireListSide.Right)]
    [Arguments(3, PopMode.Batch, RespireListSide.Left)]
    [Arguments(3, PopMode.Batch, RespireListSide.Right)]
    [Arguments(3, PopMode.Transaction, RespireListSide.Left)]
    [Arguments(3, PopMode.Transaction, RespireListSide.Right)]
    [Arguments(3, PopMode.Blocking, RespireListSide.Left)]
    [Arguments(3, PopMode.Blocking, RespireListSide.Right)]
    public async Task PopMany_SelectsFirstNonemptyKeyAndPreservesOrder(int protocol, PopMode mode, RespireListSide side)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("租户:");
        RespireKey selected = new byte[] { 0xff, 0, 0x42 };
        await view.Lists.RightPushAsync(selected, "a", "b", "c", "d");
        await view.Lists.RightPushAsync("later", "untouched");
        var popped = await PopMany(view, mode, ["missing", selected, "later"], 2, side);
        popped.Should().NotBeNull();
        popped!.Value.Key.Should().Be(selected);
        popped.Value.Values.Should().Equal(side == RespireListSide.Left ? ["a", "b"] : ["d", "c"]);
        (await view.Lists.CountAsync(popped.Value.Key)).Should().Be(2);
        (await view.Lists.RangeAsync("later")).Should().Equal("untouched");
        var remaining = await PopMany(view, mode, [selected], 20, side);
        remaining!.Value.Values.Should().Equal(side == RespireListSide.Left ? ["c", "d"] : ["b", "a"]);
        (await PopMany(view, mode, [selected], 1, side)).Should().BeNull();
        (await client.Keys.ExistsAsync(selected)).Should().BeFalse();
    }

    [Test]
    [Arguments(2, RespireListSide.Left)]
    [Arguments(2, RespireListSide.Right)]
    [Arguments(3, RespireListSide.Left)]
    [Arguments(3, RespireListSide.Right)]
    public async Task PopOne_ReturnsSelectedBinaryKeyAndTimeout(int protocol, RespireListSide side)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        RespireKey selected = new byte[] { 0xff, 0, 0x42 };
        await view.Lists.RightPushAsync(selected, "a", "b");
        await view.Lists.RightPushAsync("later", "untouched");
        var result = await view.Lists.PopAsync(["missing", selected, "later"], TimeSpan.FromSeconds(1), side);
        result.Should().NotBeNull();
        result!.Value.Key.Should().Be(selected);
        result.Value.Value.Should().Be(side == RespireListSide.Left ? "a" : "b");
        (await view.Lists.CountAsync(result.Value.Key)).Should().Be(1);
        (await view.Lists.RangeAsync("later")).Should().Equal("untouched");
        (await view.Lists.PopAsync(["missing"], TimeSpan.FromMilliseconds(10), side)).Should().BeNull();
        await view.Lists.RightPushAsync(RespireKey.Empty, "empty-key");
        (await view.Lists.PopAsync([RespireKey.Empty], TimeSpan.FromSeconds(1), side))!.Value.Key.Should().Be(RespireKey.Empty);
    }

    [Test]
    [Arguments(2, PopMode.Immediate, RespireListSide.Left)]
    [Arguments(2, PopMode.Immediate, RespireListSide.Right)]
    [Arguments(2, PopMode.Batch, RespireListSide.Left)]
    [Arguments(2, PopMode.Batch, RespireListSide.Right)]
    [Arguments(2, PopMode.Transaction, RespireListSide.Left)]
    [Arguments(2, PopMode.Transaction, RespireListSide.Right)]
    [Arguments(2, PopMode.Blocking, RespireListSide.Left)]
    [Arguments(2, PopMode.Blocking, RespireListSide.Right)]
    [Arguments(3, PopMode.Immediate, RespireListSide.Left)]
    [Arguments(3, PopMode.Immediate, RespireListSide.Right)]
    [Arguments(3, PopMode.Batch, RespireListSide.Left)]
    [Arguments(3, PopMode.Batch, RespireListSide.Right)]
    [Arguments(3, PopMode.Transaction, RespireListSide.Left)]
    [Arguments(3, PopMode.Transaction, RespireListSide.Right)]
    [Arguments(3, PopMode.Blocking, RespireListSide.Left)]
    [Arguments(3, PopMode.Blocking, RespireListSide.Right)]
    public async Task WrongType_IsPreserved(int protocol, PopMode mode, RespireListSide side)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        await client.SetAsync("wrong", "text");
        Func<Task> many = async () => { await PopMany(client, mode, ["missing", "wrong"], 1, side); };
        await many.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        if (mode == PopMode.Blocking)
        {
            Func<Task> one = async () => { await client.Lists.PopAsync(["missing", "wrong"], TimeSpan.FromSeconds(1), side); };
            await one.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        }
    }

    private static async Task<RespireListPopManyResult?> PopMany(
        IRespireClient client, PopMode mode, RespireKey[] keys, long count, RespireListSide side)
    {
        if (mode is PopMode.Immediate or PopMode.Blocking)
            return await (mode == PopMode.Blocking
                ? client.Lists.PopManyAsync(keys, TimeSpan.FromMilliseconds(10), count, side)
                : client.Lists.PopManyAsync(keys, count, side));
        if (mode == PopMode.Batch)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Lists.PopMany(keys, count, side);
            await batch.TryExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = transaction.Lists.PopMany(keys, count, side);
        await transaction.CommitAsync();
        return result.Result;
    }
}
