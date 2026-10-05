using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class KeySortIntegrationTests(RedisTestContainer fixture)
{
    public enum Mode { Immediate, Batch, Transaction }

    [Test]
    [Arguments(2, Mode.Immediate)]
    [Arguments(2, Mode.Batch)]
    [Arguments(2, Mode.Transaction)]
    [Arguments(3, Mode.Immediate)]
    [Arguments(3, Mode.Batch)]
    [Arguments(3, Mode.Transaction)]
    public async Task ArrowAcrossPrefixBoundaryBeforeWildcardRemainsPartOfKey(int protocol, Mode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant-");
        await view.Lists.RightPushAsync("items", "2", "1");
        await view.SetAsync(">weight:1", "1");
        await view.SetAsync(">weight:2", "2");
        await view.Hashes.SetAsync(">object:1", "name", "one");
        await view.Hashes.SetAsync(">object:2", "name", "two");
        (await Sort(view, mode, "items", new RespireSortOptions
        {
            By = ">weight:*", Get = new RespireKey[] { ">object:*->name" },
        })).Should().Equal("one", "two");
    }

    [Test]
    [Arguments(2, Mode.Immediate, false)]
    [Arguments(2, Mode.Immediate, true)]
    [Arguments(2, Mode.Batch, false)]
    [Arguments(2, Mode.Batch, true)]
    [Arguments(2, Mode.Transaction, false)]
    [Arguments(2, Mode.Transaction, true)]
    [Arguments(3, Mode.Immediate, false)]
    [Arguments(3, Mode.Immediate, true)]
    [Arguments(3, Mode.Batch, false)]
    [Arguments(3, Mode.Batch, true)]
    [Arguments(3, Mode.Transaction, false)]
    [Arguments(3, Mode.Transaction, true)]
    public async Task Sort_OrdersSlicesAndPreservesExternalNulls(int protocol, Mode mode, bool readOnly)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("tenant:");
        await view.Lists.RightPushAsync("numbers", "10", "2", "1");
        var options = new RespireSortOptions { ReadOnly = readOnly };
        (await Sort(view, mode, "numbers", options)).Should().Equal("1", "2", "10");
        (await Sort(view, mode, "numbers", options with { Descending = true, Limit = new(1, 2) })).Should().Equal("2", "1");
        (await Sort(view, mode, "numbers", options with { Alpha = true })).Should().Equal("1", "10", "2");
        (await Sort(view, mode, "numbers", options with { Limit = new(0, 0) })).Should().BeEmpty();
        (await Sort(view, mode, "numbers", options with { Limit = new(1, -1) })).Should().Equal("2", "10");
        (await Sort(view, mode, "numbers", options with { Limit = new(1, -5) })).Should().Equal("2", "10");
        (await Sort(view, mode, "numbers", options with { Limit = new(10, -1) })).Should().BeEmpty();
        (await Sort(view, mode, "absent", options)).Should().BeEmpty();
        await view.Hashes.SetAsync("weight:1", "rank", "30");
        await view.Hashes.SetAsync("weight:2", "rank", "10");
        await view.Hashes.SetAsync("weight:10", "rank", "20");
        await view.SetAsync("name:2", "two");
        await view.SetAsync("name:1", "one");
        await view.Hashes.SetAsync("object:2", "name", "two-hash");
        var external = options with { By = "weight:*->rank", Get = new RespireKey[] { "#", "name:*", "object:*->name" } };
        (await Sort(view, mode, "numbers", external)).Should().Equal("2", "two", "two-hash", "10", null, null, "1", "one", null);
        (await Sort(view, mode, "numbers", options with { By = "nosort", Get = new RespireKey[] { "#" } }))
            .Should().Equal("10", "2", "1");
    }

    [Test]
    [Arguments(2, Mode.Immediate, false)]
    [Arguments(2, Mode.Immediate, true)]
    [Arguments(2, Mode.Batch, false)]
    [Arguments(2, Mode.Batch, true)]
    [Arguments(2, Mode.Transaction, false)]
    [Arguments(2, Mode.Transaction, true)]
    [Arguments(3, Mode.Immediate, false)]
    [Arguments(3, Mode.Immediate, true)]
    [Arguments(3, Mode.Batch, false)]
    [Arguments(3, Mode.Batch, true)]
    [Arguments(3, Mode.Transaction, false)]
    [Arguments(3, Mode.Transaction, true)]
    public async Task TypedSort_ReturnsOwnedBinaryAndNullableMembers(int protocol, Mode mode, bool readOnly)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("binary:");
        byte[] first = [0, 0xfe];
        byte[] second = [0xff, 0];
        await view.Lists.RightPushAsync("values", second, first);
        var values = await SortBytes(view, mode, "values", new RespireSortOptions { Alpha = true, ReadOnly = readOnly });
        values.Should().HaveCount(2);
        values[0].Should().Equal(first);
        values[1].Should().Equal(second);
        Array.Clear(first);
        Array.Clear(second);
        values[0].Should().Equal(0, 0xfe);
        values[1].Should().Equal(0xff, 0);
        var missing = await SortBytes(view, mode, "values", new RespireSortOptions
        {
            Alpha = true, ReadOnly = readOnly, Get = new RespireKey[] { "missing:*" },
        });
        missing.Should().Equal(new byte[]?[] { null, null });
    }

    [Test]
    [Arguments(2, Mode.Immediate)]
    [Arguments(2, Mode.Batch)]
    [Arguments(2, Mode.Transaction)]
    [Arguments(3, Mode.Immediate)]
    [Arguments(3, Mode.Batch)]
    [Arguments(3, Mode.Transaction)]
    public async Task StoreRandomAndMove_PreserveResultShapes(int protocol, Mode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        (await Random(client, mode)).Should().BeNull();
        RespireKey binaryKey = new byte[] { 0xff, 0, 0x42 };
        await client.SetAsync(binaryKey, "value");
        (await Random(client, mode)).Should().Be(binaryKey);
        await client.Keys.DeleteAsync(binaryKey);
        var prefix = Guid.NewGuid().ToString("N") + ":";
        var view = client.WithKeyPrefix(prefix);
        await view.Lists.RightPushAsync("source", "3", "1", "2");
        (await Store(view, mode, "source", "result", new RespireSortOptions { Descending = true })).Should().Be(3);
        (await view.Lists.RangeAsync("result")).Should().Equal("3", "2", "1");
        (await Store(view, mode, "source", "result", new RespireSortOptions { Limit = new(1, -1) })).Should().Be(2);
        (await view.Lists.RangeAsync("result")).Should().Equal("2", "3");
        (await Store(view, mode, "missing", "result", null)).Should().Be(0);
        (await view.Keys.ExistsAsync("result")).Should().BeFalse();
        // No test owns the scratch database; the Guid prefix keeps this row's keys apart there.
        const int scratch = RedisTestContainer.ScratchDatabase;
        await using var other = await RespireClient.ConnectAsync($"redis://{fixture.Host}:{fixture.Port}/{scratch}?protocol={protocol}");
        var target = other.WithKeyPrefix(prefix);
        try
        {
            (await Move(view, mode, "source", scratch)).Should().BeTrue();
            (await view.Keys.ExistsAsync("source")).Should().BeFalse();
            (await target.Lists.RangeAsync("source")).Should().Equal("3", "1", "2");
            (await Move(view, mode, "source", scratch)).Should().BeFalse();
            await view.SetAsync("source", "replacement");
            (await Move(view, mode, "source", scratch)).Should().BeFalse();
            (await view.GetAsync<string>("source")).Should().Be("replacement");
        }
        finally { await target.Keys.DeleteAsync("source"); }
    }

    [Test]
    [Arguments(2, Mode.Immediate, false)]
    [Arguments(2, Mode.Immediate, true)]
    [Arguments(2, Mode.Batch, false)]
    [Arguments(2, Mode.Batch, true)]
    [Arguments(2, Mode.Transaction, false)]
    [Arguments(2, Mode.Transaction, true)]
    [Arguments(3, Mode.Immediate, false)]
    [Arguments(3, Mode.Immediate, true)]
    [Arguments(3, Mode.Batch, false)]
    [Arguments(3, Mode.Batch, true)]
    [Arguments(3, Mode.Transaction, false)]
    [Arguments(3, Mode.Transaction, true)]
    public async Task WrongTypeAndInvalidNumbers_RemainServerErrors(int protocol, Mode mode, bool readOnly)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        await client.SetAsync("wrong", "string");
        Func<Task> wrong = async () => { await Sort(client, mode, "wrong", new RespireSortOptions { ReadOnly = readOnly }); };
        await wrong.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        await client.Lists.RightPushAsync("words", "word");
        Func<Task> numeric = async () => { await Sort(client, mode, "words", new RespireSortOptions { ReadOnly = readOnly }); };
        await numeric.Should().ThrowAsync<RespireServerException>().WithMessage("*convert*double*");
        Func<Task> database = async () => { await Move(client, mode, "wrong", int.MaxValue); };
        await database.Should().ThrowAsync<RespireServerException>();
    }

    private static Task<string?[]> Sort(IRespireClient client, Mode mode, RespireKey key, RespireSortOptions options)
        => Execute(client, mode, () => client.Keys.SortAsync(key, options), queue => queue.Keys.Sort(key, options));
    private static Task<byte[]?[]> SortBytes(IRespireClient client, Mode mode, RespireKey key, RespireSortOptions options)
        => Execute(client, mode, () => client.Keys.SortAsync<byte[]>(key, options), queue => queue.Keys.Sort<byte[]>(key, options));
    private static Task<long> Store(IRespireClient client, Mode mode, RespireKey key, RespireKey destination, RespireSortOptions? options)
        => Execute(client, mode, () => client.Keys.SortStoreAsync(key, destination, options), queue => queue.Keys.SortStore(key, destination, options));
    private static Task<RespireKey?> Random(IRespireClient client, Mode mode)
        => Execute(client, mode, () => client.Keys.RandomAsync(), queue => queue.Keys.Random());
    private static Task<bool> Move(IRespireClient client, Mode mode, RespireKey key, int database)
        => Execute(client, mode, () => client.Keys.MoveAsync(key, database), queue => queue.Keys.Move(key, database));

    private static async Task<T> Execute<T>(IRespireClient client, Mode mode, Func<ValueTask<T>> immediate, Func<IRespireCommandQueue, RespirePending<T>> enqueue)
    {
        if (mode == Mode.Immediate) return await immediate();
        if (mode == Mode.Batch)
        {
            using var batch = client.CreateBatch();
            var pending = enqueue(batch);
            await batch.TryExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = enqueue(transaction);
        await transaction.CommitAsync();
        return result.Result;
    }
}
