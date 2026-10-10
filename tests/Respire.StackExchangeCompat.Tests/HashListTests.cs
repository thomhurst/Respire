using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class HashListTests(RedisTestContainer fixture)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(fixture.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HashCommandsPreserveBinaryEntriesCountsAndDatabaseIsolation(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        RedisKey key = new byte[] { 0xff, 0, (byte)fixture.Database };
        RedisValue field = new byte[] { 0x80, 0 };
        byte[] value = [0xfe, 0, 0xff];
        database.HashSet(key, [new(field, value), new("empty", "")]);
        Assert.Equal(2, database.HashLength(key));
        Assert.Equal(2, await database.HashLengthAsync(key));
        var entries = database.HashGetAll(key);
        Assert.Equal(2, entries.Length);
        Assert.Equal(value, (byte[]?)entries.Single(entry => entry.Name == field).Value);
        Assert.Equal(entries.OrderBy(entry => entry.Name), (await database.HashGetAllAsync(key)).OrderBy(entry => entry.Name));
        await using var control = await RespireClient.ConnectAsync(Options(protocol));
        using var reply = await control.ExecuteAsync(RespireCommands.Hash.HGET, (byte[]?)key!, (byte[]?)field!);
        Assert.Equal(value, reply.AsBytes());
        Assert.Empty(connection.GetDatabase(0).HashGetAll(key));
        Assert.False(database.HashDelete(key, "missing"));
        Assert.True(await database.HashDeleteAsync(key, "empty"));
        Assert.Equal(0, await database.HashDeleteAsync(key, Array.Empty<RedisValue>()));
        Assert.Equal(1, database.HashDelete(key, [field, "missing"]));
        Assert.Empty(await database.HashGetAllAsync(key));
        Assert.Equal(0, await database.HashLengthAsync(key));
        Assert.False(await database.HashDeleteAsync(key, field));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ListCommandsPreserveSignedIndexesCountsAndMoveResults(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        Assert.Equal(0, database.ListLength("source"));
        Assert.True((await database.ListGetByIndexAsync("source", -1)).IsNull);
        Assert.Equal(0, database.ListLeftPush("source", "ignored", When.Exists));
        Assert.Equal(0, await database.ListLeftPushAsync("source", Array.Empty<RedisValue>()));
        Assert.Equal(2, await database.ListLeftPushAsync("source", ["a", "b"]));
        Assert.Equal(3, database.ListLeftPush("source", "c", When.Exists));
        Assert.Equal(5, database.ListLeftPush("source", ["a", "a"], When.Exists));
        Assert.Equal(5, await database.ListLengthAsync("source"));
        Assert.Equal("a", (string?)database.ListGetByIndex("source", -1));
        Assert.Equal(1, await database.ListRemoveAsync("source", "a", -1));
        Assert.Equal(new RedisValue[] { "a", "a", "c", "b" }, database.ListRange("source"));
        Assert.Equal(1, database.ListRemove("source", "a", 1));
        Assert.Equal(1, await database.ListRemoveAsync("source", "a", 0));
        await database.ListTrimAsync("source", -1, -1);
        Assert.Equal(new RedisValue[] { "b" }, database.ListRange("source"));
        byte[] bytes = [0xff, 0, 0x80];
        Assert.Equal(2, await database.ListLeftPushAsync("source", bytes));
        Assert.Equal("b", (string?)database.ListRightPopLeftPush("source", "destination"));
        Assert.Equal(bytes, (byte[]?)await database.ListRightPopLeftPushAsync("source", "destination"));
        Assert.True(database.ListRightPopLeftPush("source", "destination").IsNull);
        database.ListTrim("destination", 0, 0);
        Assert.Equal(bytes, (byte[]?)database.ListGetByIndex("destination", 0));
        Assert.True(database.ListGetByIndex("destination", 20).IsNull);
        Assert.Equal(1, await database.ListLeftPushAsync("destination", Array.Empty<RedisValue>(), When.Exists));
        database.ListTrim("destination", 2, 1);
        Assert.Equal(0, database.ListLength("destination"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchDefersReadsWritesAndSnapshotsBinaryArguments(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        byte[] key = [0xff, 0];
        byte[] field = [0x80, 0];
        var set = batch.HashSetAsync(key, field, "value");
        var hash = batch.HashGetAllAsync(key);
        var hashLength = batch.HashLengthAsync(key);
        var delete = batch.HashDeleteAsync(key, field);
        var missing = batch.HashGetAsync(key, field);
        byte[] bytes = [0xfe, 0];
        var push = batch.ListLeftPushAsync("source", bytes);
        var listLength = batch.ListLengthAsync("source");
        var index = batch.ListGetByIndexAsync("source", -1);
        var move = batch.ListRightPopLeftPushAsync("source", "destination");
        var range = batch.ListRangeAsync("destination");
        var trim = batch.ListTrimAsync("destination", 0, 0);
        var remove = batch.ListRemoveAsync("destination", bytes);
        Task[] tasks = [set, hash, hashLength, delete, missing, push, listLength, index, move, range, trim, remove];
        Assert.All(tasks, task => Assert.False(task.IsCompleted));
        Assert.Empty(database.HashGetAll(key));
        Assert.Equal(0, database.ListLength("source"));
        key[0] = 0;
        field[0] = 0;
        bytes[0] = 0;
        batch.Execute();
        await Task.WhenAll(tasks);
        Assert.True(await set);
        Assert.Equal(new HashEntry(new byte[] { 0x80, 0 }, "value"), Assert.Single(await hash));
        Assert.Equal(1, await hashLength);
        Assert.True(await delete);
        Assert.True((await missing).IsNull);
        Assert.Equal(1, await push);
        Assert.Equal(1, await listLength);
        Assert.Equal(new byte[] { 0xfe, 0 }, (byte[]?)await index);
        Assert.Equal(new byte[] { 0xfe, 0 }, (byte[]?)await move);
        Assert.Equal(new RedisValue[] { new byte[] { 0xfe, 0 } }, await range);
        Assert.Equal(1, await remove);
        batch.Execute();
        Assert.Equal(0, database.ListLength("destination"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchErrorsDoNotHideLaterResultsAndCloseCancelsUnexecutedReads(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        await client.SetAsync("wrongtype", "string");
        var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        var wrongHash = batch.HashGetAllAsync("wrongtype");
        var wrongList = batch.ListLeftPushAsync("wrongtype", "value");
        var success = batch.ListLeftPushAsync("list", "value");
        var count = batch.ListLengthAsync("list");
        batch.Execute();
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => wrongHash)).Code);
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => wrongList)).Code);
        Assert.Equal(1, await success);
        Assert.Equal(1, await count);
        var unexecuted = database.CreateBatch();
        var hash = unexecuted.HashGetAllAsync("hash");
        var list = unexecuted.ListGetByIndexAsync("list", 0);
        await connection.CloseAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hash);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => list);
        using var reply = await client.ExecuteAsync(RespireCommands.List.LINDEX, "list", 0);
        Assert.Equal("value", reply.AsString());
        await connection.DisposeAsync();
    }

    [Test]
    public async Task FireAndForgetUsesEmptyArrayNullAndCountDefaults()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(3));
        var database = connection.GetDatabase();
        Assert.Equal(0, await database.ListLeftPushAsync("list", "value", flags: CommandFlags.FireAndForget));
        Assert.Equal(1, database.ListLength("list"));
        Assert.True((await database.ListGetByIndexAsync("list", 0, CommandFlags.FireAndForget)).IsNull);
        database.HashSet("hash", "field", "value");
        Assert.Empty(await database.HashGetAllAsync("hash", CommandFlags.FireAndForget));
        Assert.Single(database.HashGetAll("hash"));
    }

    [Test]
    public async Task UnsupportedConditionsFlagsAndWrongTypesRemainExplicit()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(3));
        var database = connection.GetDatabase();
        Assert.Throws<NotSupportedException>(() => database.ListLeftPush("list", "value", When.NotExists));
        Assert.Throws<NotSupportedException>(() => database.ListLength("list", (CommandFlags)1));
        Assert.Throws<NotSupportedException>(() => database.ListLeftPush("list", "value", flags: CommandFlags.DemandReplica));
        Assert.Throws<ArgumentNullException>(() => database.HashDelete("hash", (RedisValue[])null!));
        Assert.Throws<ArgumentNullException>(() => database.ListLeftPush("list", (RedisValue[])null!));
        Assert.Throws<ArgumentException>(() => database.ListRemove("list", RedisValue.Null));
        var batch = database.CreateBatch();
        Assert.Throws<NotSupportedException>(() => { _ = batch.ListLengthAsync("list", CommandFlags.FireAndForget); });
        Assert.Throws<NotSupportedException>(() => { _ = batch.ListLeftPushAsync("list", "value", When.NotExists); });
        database.HashSet("hash", "field", "value");
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => database.ListLengthAsync("hash"))).Code);
    }
}
