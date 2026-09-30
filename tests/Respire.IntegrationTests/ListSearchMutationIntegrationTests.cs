using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ListSearchMutationIntegrationTests(RedisTestContainer fixture)
{
    public enum ExecutionMode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task Search_HandlesRankCountLimitsAndBinaryValues(int protocol, ExecutionMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix("tenant:");
        byte[] binary = [0xff, 0, 0x42];
        await view.Lists.RightPushAsync("list", binary, "gap", binary, binary);
        (await Run(view, mode, l => l.PositionAsync("list", binary), l => l.Position("list", binary))).Should().Be(0);
        (await Run(view, mode, l => l.PositionAsync("list", binary, rank: 2), l => l.Position("list", binary, rank: 2))).Should().Be(2);
        (await Run(view, mode, l => l.PositionAsync("list", binary, rank: -1), l => l.Position("list", binary, rank: -1))).Should().Be(3);
        (await Run(view, mode, l => l.PositionAsync("list", binary, rank: 2, maxLength: 2), l => l.Position("list", binary, rank: 2, maxLength: 2))).Should().BeNull();
        (await Run(view, mode, l => l.PositionsAsync("list", binary), l => l.Positions("list", binary))).Should().Equal(0L, 2L, 3L);
        (await Run(view, mode, l => l.PositionsAsync("list", binary, count: 2, rank: -1, maxLength: 3), l => l.Positions("list", binary, count: 2, rank: -1, maxLength: 3))).Should().Equal(3L, 2L);
        (await Run(view, mode, l => l.PositionsAsync("list", binary, maxLength: 2), l => l.Positions("list", binary, maxLength: 2))).Should().Equal(0L);
        foreach (var key in new[] { "list", "missing" })
        {
            (await Run(view, mode, l => l.PositionAsync(key, "absent"), l => l.Position(key, "absent"))).Should().BeNull();
            (await Run(view, mode, l => l.PositionsAsync(key, "absent"), l => l.Positions(key, "absent"))).Should().BeEmpty();
        }
    }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task Mutations_PreserveBinaryValuesNegativeIndexesAndSentinels(int protocol, ExecutionMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        var view = client.WithKeyPrefix("tenant:");
        byte[] binary = [0xff, 0, 0x42];
        (await Run(view, mode, l => l.InsertBeforeAsync("missing", binary, "before"), l => l.InsertBefore("missing", binary, "before"))).Should().Be(0);
        (await Run(view, mode, l => l.InsertAfterAsync("missing", binary, "after"), l => l.InsertAfter("missing", binary, "after"))).Should().Be(0);
        (await Run(view, mode, l => l.LeftPushIfExistsAsync("missing", "a", "b"), l => l.LeftPushIfExists("missing", "a", "b"))).Should().Be(0);
        (await Run(view, mode, l => l.RightPushIfExistsAsync("missing", "a", "b"), l => l.RightPushIfExists("missing", "a", "b"))).Should().Be(0);
        (await view.Keys.ExistsAsync("missing")).Should().BeFalse();
        await view.Lists.RightPushAsync("list", binary, "tail");
        (await Run(view, mode, l => l.InsertBeforeAsync("list", binary, "before"), l => l.InsertBefore("list", binary, "before"))).Should().Be(3);
        (await Run(view, mode, l => l.InsertAfterAsync("list", binary, "after"), l => l.InsertAfter("list", binary, "after"))).Should().Be(4);
        (await Run(view, mode, l => l.InsertBeforeAsync("list", "absent", "x"), l => l.InsertBefore("list", "absent", "x"))).Should().Be(-1);
        (await Run(view, mode, l => l.InsertAfterAsync("list", "absent", "x"), l => l.InsertAfter("list", "absent", "x"))).Should().Be(-1);
        (await Run(view, mode, l => l.SetAsync("list", -1, binary), l => l.Set("list", -1, binary))).Should().BeTrue();
        (await view.Lists.PositionsAsync("list", binary)).Should().Equal(1L, 3L);
        (await Run(view, mode, l => l.LeftPushIfExistsAsync("list", binary, "left"), l => l.LeftPushIfExists("list", binary, "left"))).Should().Be(6);
        (await Run(view, mode, l => l.RightPushIfExistsAsync("list", "right", binary), l => l.RightPushIfExists("list", "right", binary))).Should().Be(8);
        (await view.Lists.PositionsAsync("list", binary)).Should().Equal(1L, 3L, 5L, 7L);
        (await view.Lists.IndexAsync("list", 0)).Should().Be("left");
        (await view.Lists.IndexAsync("list", 6)).Should().Be("right");
        (await client.Keys.ExistsAsync("list")).Should().BeFalse();
    }

    [Test]
    [Arguments(2, ExecutionMode.Immediate)]
    [Arguments(2, ExecutionMode.Batch)]
    [Arguments(2, ExecutionMode.Transaction)]
    [Arguments(3, ExecutionMode.Immediate)]
    [Arguments(3, ExecutionMode.Batch)]
    [Arguments(3, ExecutionMode.Transaction)]
    public async Task ServerErrors_ArePreserved(int protocol, ExecutionMode mode)
    {
        await using var client = await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");
        await client.SetAsync("wrong", "string");
        await client.Lists.RightPushAsync("list", "value");
        Func<Task>[] wrongType =
        [
            async () => { await Run(client, mode, l => l.PositionAsync("wrong", "x"), l => l.Position("wrong", "x")); },
            async () => { await Run(client, mode, l => l.PositionsAsync("wrong", "x"), l => l.Positions("wrong", "x")); },
            async () => { await Run(client, mode, l => l.InsertBeforeAsync("wrong", "x", "y"), l => l.InsertBefore("wrong", "x", "y")); },
            async () => { await Run(client, mode, l => l.InsertAfterAsync("wrong", "x", "y"), l => l.InsertAfter("wrong", "x", "y")); },
            async () => { await Run(client, mode, l => l.SetAsync("wrong", 0, "x"), l => l.Set("wrong", 0, "x")); },
            async () => { await Run(client, mode, l => l.LeftPushIfExistsAsync("wrong", "x"), l => l.LeftPushIfExists("wrong", "x")); },
            async () => { await Run(client, mode, l => l.RightPushIfExistsAsync("wrong", "x"), l => l.RightPushIfExists("wrong", "x")); },
        ];
        foreach (var command in wrongType)
        {
            var error = await command.Should().ThrowAsync<RespireServerException>();
            error.Which.Code.Should().Be("WRONGTYPE");
        }
        Func<Task> missing = async () => { await Run(client, mode, l => l.SetAsync("missing", 0, "x"), l => l.Set("missing", 0, "x")); };
        Func<Task> outOfRange = async () => { await Run(client, mode, l => l.SetAsync("list", 2, "x"), l => l.Set("list", 2, "x")); };
        await missing.Should().ThrowAsync<RespireServerException>();
        await outOfRange.Should().ThrowAsync<RespireServerException>();
    }

    private static async Task<T> Run<T>(IRespireClient client, ExecutionMode mode, Func<IListCommands, ValueTask<T>> immediate, Func<IBatchListCommands, RespirePending<T>> deferred)
    {
        if (mode == ExecutionMode.Immediate)
            return await immediate(client.Lists);
        using var batch = mode == ExecutionMode.Batch ? client.CreateBatch() : null;
        await using var transaction = mode == ExecutionMode.Transaction ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = deferred(queue.Lists);
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.TryExecuteAsync();
        return pending.Result;
    }
}
