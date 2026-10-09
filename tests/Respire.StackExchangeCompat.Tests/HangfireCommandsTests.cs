using Respire.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;
using RedisSortedSetEntry = StackExchange.Redis.SortedSetEntry;
using Respire.Tests.Networking;
using Respire.Internal;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
// Exercise the synchronous interface without starving the shared async transport during parallel suites.
[NotInParallel]
public class HangfireCommandsTests(RedisTestContainer redis)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(redis.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnboundedSortedSetLengthIncludesInfiniteScoresAcrossAllDispatchPaths(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        await expected.SortedSetAddAsync("length", [new("negative", double.NegativeInfinity), new("zero", 0), new("positive", double.PositiveInfinity)]);
        foreach (var exclude in new[] { Exclude.None, Exclude.Start, Exclude.Stop, Exclude.Both })
        foreach (var (min, max) in new[] { (double.NegativeInfinity, double.PositiveInfinity), (double.NegativeInfinity, 0d), (0d, double.PositiveInfinity), (double.PositiveInfinity, double.NegativeInfinity) })
        {
            var count = await expected.SortedSetLengthAsync("length", min, max, exclude);
            Assert.Equal(count, database.SortedSetLength("length", min, max, exclude));
            Assert.Equal(count, await database.SortedSetLengthAsync("length", min, max, exclude));
            var batch = database.CreateBatch();
            var pending = batch.SortedSetLengthAsync("length", min, max, exclude);
            Assert.False(pending.IsCompleted);
            batch.Execute();
            Assert.Equal(count, await pending);
        }
    }

    [Test]
    public async Task IntegerCountersPreserveUpstreamWireCommandsAcrossAllDispatchPaths()
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var commands = new List<string>();
        foreach (var (value, increment, decrement) in new[]
        {
            (1L, "INCR key", "DECR key"),
            (-1L, "DECR key", "INCR key"),
            (0L, "INCRBY key 0", "INCRBY key 0"),
            (2L, "INCRBY key 2", "DECRBY key 2"),
            (-2L, "DECRBY key 2", "INCRBY key 2"),
            (long.MaxValue, "INCRBY key 9223372036854775807", "DECRBY key 9223372036854775807"),
            (long.MinValue, "DECRBY key -9223372036854775808", "DECRBY key -9223372036854775808"),
        })
        {
            Assert.Equal(42, database.StringIncrement("key", value));
            Assert.Equal(42, database.StringDecrement("key", value));
            Assert.Equal(42, await database.StringIncrementAsync("key", value));
            Assert.Equal(42, await database.StringDecrementAsync("key", value));
            var batch = database.CreateBatch();
            var increase = batch.StringIncrementAsync("key", value);
            var decrease = batch.StringDecrementAsync("key", value);
            Assert.False(increase.IsCompleted);
            Assert.False(decrease.IsCompleted);
            batch.Execute();
            Assert.Equal(42, await increase);
            Assert.Equal(42, await decrease);
            commands.AddRange([increment, decrement, increment, decrement, increment, decrement]);
        }
        Assert.Equal(42, await database.StringIncrementAsync("key"));
        Assert.Equal(42, await database.StringDecrementAsync("key"));
        commands.AddRange(["INCR key", "DECR key"]);
        Assert.Equal(0, await database.StringIncrementAsync("key", flags: CommandFlags.FireAndForget));
        Assert.Equal(0, database.StringDecrement("key", flags: CommandFlags.FireAndForget));
        Assert.Equal(0, await database.StringIncrementAsync("key", 0, CommandFlags.FireAndForget));
        Assert.Equal(0, database.StringDecrement("key", 0, CommandFlags.FireAndForget));
        Assert.Equal(42, await database.StringIncrementAsync("barrier"));
        commands.AddRange(["INCR key", "DECR key", "INCR barrier"]);
        Assert.Equal(commands, server.ReceivedCommands);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task KeyStringAndSetCommandsPreserveBinaryValuesAndRedisOutcomes(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        byte[] key = [0xff, 0, 0x80];
        byte[] bytes = [0xfe, 0, 0x81];
        await client.SetAsync(key, bytes);
        Assert.True(database.KeyExists(key));
        Assert.False(await database.KeyExistsAsync("missing"));
        Assert.Equal(bytes, (byte[]?)database.StringGet(key));
        Assert.Equal(new RedisValue[] { bytes, RedisValue.Null, bytes }, await database.StringGetAsync([key, "missing", key]));
        Assert.Null(database.KeyTimeToLive(key));
        Assert.Null(await database.KeyTimeToLiveAsync("missing"));
        Assert.False(database.KeyPersist(key));
        Assert.True(database.KeyExpire(key, TimeSpan.FromSeconds(30)));
        Assert.InRange((await database.KeyTimeToLiveAsync(key))!.Value.TotalSeconds, 20, 30);
        Assert.True(await database.KeyPersistAsync(key));
        Assert.Null(database.KeyTimeToLive(key));
        Assert.Equal(3, database.StringIncrement("counter", 3));
        Assert.Equal(1, await database.StringDecrementAsync("counter", 2));
        Assert.Equal(-3, database.StringDecrement("counter", 4));
        Assert.Equal(-2, await database.StringIncrementAsync("counter"));
        Assert.True(database.SetAdd("set", bytes));
        Assert.False(await database.SetAddAsync("set", bytes));
        Assert.Equal(2, await database.SetAddAsync("set", ["a", "b", bytes]));
        Assert.Equal(3, database.SetLength("set"));
        Assert.Contains((RedisValue)bytes, await database.SetMembersAsync("set"));
        Assert.True(database.SetRemove("set", bytes));
        Assert.Equal(2, await database.SetRemoveAsync("set", ["a", "b", "missing"]));
        Assert.Equal(0, await database.SetLengthAsync("set"));
        Assert.Empty(database.SetMembers("set"));
        Assert.Empty(database.StringGet(Array.Empty<RedisKey>()));
        Assert.Equal("ERR", (await Assert.ThrowsAsync<RespireServerException>(() => database.StringIncrementAsync(key))).Code);
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => database.SetMembersAsync(key))).Code);
        await using var isolated = RespireConnectionMultiplexer.Create(Options(protocol));
        Assert.True(isolated.GetDatabase(0).StringGet(key).IsNull);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SortedSetBoundsOrderingScoresAndPaginationMatchStackExchangeRedis(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        byte[] binary = [0xff, 0, 0x80];
        Assert.True(database.SortedSetAdd("sorted", binary, 1));
        Assert.False(await database.SortedSetAddAsync("sorted", binary, 2));
        RedisSortedSetEntry[] entries = [new("negative", -2), new("zero", 0), new("a", 1), new("b", 1), new("positive", 3), new("infinity", double.PositiveInfinity)];
        Assert.Equal(6, await database.SortedSetAddAsync("sorted", entries));
        Assert.Equal(0, database.SortedSetAdd("sorted", entries));
        foreach (var order in new[] { Order.Ascending, Order.Descending })
        {
            Assert.Equal(expected.SortedSetRangeByRank("sorted", -4, -1, order), database.SortedSetRangeByRank("sorted", -4, -1, order));
            Assert.Equal(await expected.SortedSetRangeByRankWithScoresAsync("sorted", 0, -1, order), await database.SortedSetRangeByRankWithScoresAsync("sorted", 0, -1, order));
            foreach (var exclude in new[] { Exclude.None, Exclude.Start, Exclude.Stop, Exclude.Both })
            {
                Assert.Equal(expected.SortedSetLength("sorted", 1, 2, exclude), await database.SortedSetLengthAsync("sorted", 1, 2, exclude));
                Assert.Equal(expected.SortedSetRangeByScore("sorted", 1, 2, exclude, order), await database.SortedSetRangeByScoreAsync("sorted", 1, 2, exclude, order));
                Assert.Equal(expected.SortedSetRangeByScoreWithScores("sorted", 0, 3, exclude, order, 1, 2), database.SortedSetRangeByScoreWithScores("sorted", 0, 3, exclude, order, 1, 2));
            }
            Assert.Equal(expected.SortedSetRangeByScore("sorted", order: order, skip: 2, take: -1), database.SortedSetRangeByScore("sorted", order: order, skip: 2, take: -1));
        }
        Assert.Equal(7, database.SortedSetLength("sorted"));
        Assert.Equal(expected.SortedSetRangeByScore("sorted", 2, 1), database.SortedSetRangeByScore("sorted", 2, 1));
        Assert.Empty(await database.SortedSetRangeByScoreWithScoresAsync("sorted", take: 0));
        Assert.Empty(database.SortedSetRangeByRankWithScores("missing"));
        Assert.True(await database.SortedSetRemoveAsync("sorted", binary));
        Assert.False(database.SortedSetRemove("sorted", binary));
        Assert.Equal(2, database.SortedSetRemove("sorted", ["a", "b", "missing"]));
        Assert.Equal(1, await database.SortedSetRemoveAsync("sorted", ["zero"]));
        Assert.Equal("ERR", (await Assert.ThrowsAsync<RespireServerException>(() => database.SortedSetAddAsync("sorted", "nan", double.NaN))).Code);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReversedScoreBoundsPreserveEndpointExclusionsAcrossAllDispatchPaths(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        RedisSortedSetEntry[] entries = [new("negative", -1), new("zero", 0), new("one", 1), new("two", 2), new("three", 3)];
        await expected.SortedSetAddAsync("reversed", entries);
        foreach (var order in new[] { Order.Ascending, Order.Descending })
        foreach (var exclude in new[] { Exclude.None, Exclude.Start, Exclude.Stop, Exclude.Both })
        foreach (var (start, stop) in new[] { (2d, 1d), (1d, 2d), (1d, 1d), (double.PositiveInfinity, double.NegativeInfinity) })
        foreach (var (skip, take) in new[] { (0L, -1L), (1L, 1L) })
        {
            var values = await expected.SortedSetRangeByScoreAsync("reversed", start, stop, exclude, order, skip, take);
            var scores = await expected.SortedSetRangeByScoreWithScoresAsync("reversed", start, stop, exclude, order, skip, take);
            Assert.Equal(values, database.SortedSetRangeByScore("reversed", start, stop, exclude, order, skip, take));
            Assert.Equal(values, await database.SortedSetRangeByScoreAsync("reversed", start, stop, exclude, order, skip, take));
            Assert.Equal(scores, database.SortedSetRangeByScoreWithScores("reversed", start, stop, exclude, order, skip, take));
            Assert.Equal(scores, await database.SortedSetRangeByScoreWithScoresAsync("reversed", start, stop, exclude, order, skip, take));
            var batch = database.CreateBatch();
            var batchValues = batch.SortedSetRangeByScoreAsync("reversed", start, stop, exclude, order, skip, take);
            var batchScores = batch.SortedSetRangeByScoreWithScoresAsync("reversed", start, stop, exclude, order, skip, take);
            Assert.False(batchValues.IsCompleted);
            Assert.False(batchScores.IsCompleted);
            batch.Execute();
            Assert.Equal(values, await batchValues);
            Assert.Equal(scores, await batchScores);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScanPreservesBinaryPatternsScoresAndCursorResume(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        var values = Enumerable.Range(0, 400).Select(index => new RedisSortedSetEntry($"member:{index}", index)).ToArray();
        database.SortedSetAdd("scan", values);
        var scan = database.SortedSetScan("scan", pageSize: 7);
        var cursor = Assert.IsAssignableFrom<IScanningCursor>(scan);
        Assert.Equal(7, cursor.PageSize);
        using var enumerator = scan.GetEnumerator();
        var position = Assert.IsAssignableFrom<IScanningCursor>(enumerator);
        for (var index = 0; index < 20; index++) Assert.True(enumerator.MoveNext());
        Assert.Equal(position.Cursor, cursor.Cursor);
        Assert.Equal(position.PageOffset, cursor.PageOffset);
        var current = enumerator.Current;
        var remaining = new List<RedisSortedSetEntry> { current };
        while (enumerator.MoveNext()) remaining.Add(enumerator.Current);
        // Cursor addresses the current server page; PageOffset addresses the current entry, inclusively.
        using var start = scan.GetEnumerator();
        for (var index = 0; index < 20; index++) Assert.True(start.MoveNext());
        var saved = (IScanningCursor)start;
        Assert.Equal(remaining, database.SortedSetScan("scan", pageSize: 7, cursor: saved.Cursor, pageOffset: saved.PageOffset));
        var asyncScan = database.SortedSetScanAsync("scan", "member:1*", pageSize: 9);
        Assert.IsAssignableFrom<IScanningCursor>(asyncScan);
        var matches = new List<RedisSortedSetEntry>();
        await foreach (var entry in asyncScan) matches.Add(entry);
        Assert.Equal(values.Where(entry => ((string)entry.Element!).StartsWith("member:1", StringComparison.Ordinal)).OrderBy(entry => entry.Score), matches.OrderBy(entry => entry.Score));
        byte[] member = [0xff, 0, 0x80];
        byte[] pattern = [0xff, (byte)'*'];
        database.SortedSetAdd("binaryscan", member, -3);
        var binaryScan = database.SortedSetScan("binaryscan", pattern);
        pattern[0] = 0;
        Assert.Equal(new RedisSortedSetEntry(member, -3), Assert.Single(binaryScan));
        Assert.Empty(database.SortedSetScan("missing"));
        Assert.Equal(values.OrderBy(entry => entry.Score), database.SortedSetScan("scan", RedisValue.EmptyString).OrderBy(entry => entry.Score));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var cancelledScan = database.SortedSetScanAsync("scan").GetAsyncEnumerator(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledScan.MoveNextAsync().AsTask());
        Assert.True(start.MoveNext());
        start.Reset();
        Assert.True(start.MoveNext());
        start.Dispose();
        Assert.False(start.MoveNext());
    }

    [Test]
    [Arguments(false, 0L)]
    [Arguments(true, 0L)]
    [Arguments(false, 7L)]
    [Arguments(true, 7L)]
    public async Task ScanRetainsResumePositionAfterForeachBreak(bool asynchronous, long pageCursor)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("ZSCAN key 0", StringComparison.Ordinal))
                    return "*2\r\n$1\r\n7\r\n*6\r\n$1\r\na\r\n$1\r\n1\r\n$1\r\nb\r\n$1\r\n2\r\n$1\r\nc\r\n$1\r\n3\r\n"u8.ToArray();
                if (command.StartsWith("ZSCAN key 7", StringComparison.Ordinal))
                    return "*2\r\n$1\r\n0\r\n*6\r\n$1\r\nd\r\n$1\r\n4\r\n$1\r\ne\r\n$1\r\n5\r\n$1\r\nf\r\n$1\r\n6\r\n"u8.ToArray();
                return null;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var consumed = 0;
        var stopAfter = pageCursor == 0 ? 2 : 5;
        IScanningCursor position;
        if (asynchronous)
        {
            var scan = database.SortedSetScanAsync("key", pageSize: 3);
            position = Assert.IsAssignableFrom<IScanningCursor>(scan);
            await foreach (var entry in scan)
            {
                if (++consumed == stopAfter) break;
            }
        }
        else
        {
            var scan = database.SortedSetScan("key", pageSize: 3);
            position = Assert.IsAssignableFrom<IScanningCursor>(scan);
            foreach (var entry in scan)
            {
                if (++consumed == stopAfter) break;
            }
        }

        Assert.Equal(pageCursor, position.Cursor);
        Assert.Equal(1, position.PageOffset);
        Assert.Equal(3, position.PageSize);
        // StackExchange.Redis resumes inclusively at the last returned entry.
        var remaining = database.SortedSetScan("key", pageSize: position.PageSize,
            cursor: position.Cursor, pageOffset: position.PageOffset).Select(entry => (string)entry.Element!);
        Assert.Equal(new[] { "a", "b", "c", "d", "e", "f" }.Skip(stopAfter - 1), remaining);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchDefersAllCommandFamiliesAndSnapshotsBinaryArguments(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        await client.SetAsync("text", "value");
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        byte[] member = [0xff, 0, 0x80];
        var increment = batch.StringIncrementAsync("counter", 4);
        var decrement = batch.StringDecrementAsync("counter", 1);
        var get = batch.StringGetAsync("counter");
        RedisKey[] keys = ["counter", "text", "missing"];
        var gets = batch.StringGetAsync(keys);
        var exists = batch.KeyExistsAsync("counter");
        var expiry = batch.KeyExpireAsync("counter", TimeSpan.FromSeconds(30));
        var ttl = batch.KeyTimeToLiveAsync("counter");
        var persist = batch.KeyPersistAsync("counter");
        var add = batch.SetAddAsync("set", member);
        var addMany = batch.SetAddAsync("set", ["a", "b"]);
        var members = batch.SetMembersAsync("set");
        var length = batch.SetLengthAsync("set");
        var remove = batch.SetRemoveAsync("set", member);
        var removeMany = batch.SetRemoveAsync("set", ["a", "b"]);
        var sortedAdd = batch.SortedSetAddAsync("sorted", member, 1);
        RedisSortedSetEntry[] entries = [new("a", 0), new("b", 2)];
        var sortedAdds = batch.SortedSetAddAsync("sorted", entries);
        var sortedLength = batch.SortedSetLengthAsync("sorted", 0, 1);
        var ranks = batch.SortedSetRangeByRankAsync("sorted");
        var rankScores = batch.SortedSetRangeByRankWithScoresAsync("sorted");
        var scores = batch.SortedSetRangeByScoreAsync("sorted", 0, 1);
        var scoreScores = batch.SortedSetRangeByScoreWithScoresAsync("sorted", order: Order.Descending);
        var sortedRemove = batch.SortedSetRemoveAsync("sorted", member);
        var sortedRemoves = batch.SortedSetRemoveAsync("sorted", ["a", "b"]);
        Task[] pending = [increment, decrement, get, gets, exists, expiry, ttl, persist, add, addMany, members, length, remove, removeMany, sortedAdd, sortedAdds, sortedLength, ranks, rankScores, scores, scoreScores, sortedRemove, sortedRemoves];
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        Assert.False(database.KeyExists("counter"));
        member[0] = 0;
        keys[0] = "changed";
        entries[0] = new("changed", 99);
        batch.Execute();
        await Task.WhenAll(pending);
        Assert.Equal(4, await increment);
        Assert.Equal(3, await decrement);
        Assert.Equal("3", (string?)await get);
        Assert.Equal(new RedisValue[] { "3", "value", RedisValue.Null }, await gets);
        Assert.True(await exists);
        Assert.True(await expiry);
        Assert.InRange((await ttl)!.Value.TotalSeconds, 20, 30);
        Assert.True(await persist);
        Assert.True(await add);
        Assert.Equal(2, await addMany);
        Assert.Contains((RedisValue)new byte[] { 0xff, 0, 0x80 }, await members);
        Assert.Equal(3, await length);
        Assert.True(await remove);
        Assert.Equal(2, await removeMany);
        Assert.True(await sortedAdd);
        Assert.Equal(2, await sortedAdds);
        Assert.Equal(2, await sortedLength);
        Assert.Equal(new RedisValue[] { "a", new byte[] { 0xff, 0, 0x80 }, "b" }, await ranks);
        Assert.Equal(new double[] { 0, 1, 2 }, (await rankScores).Select(entry => entry.Score));
        Assert.Equal(new RedisValue[] { "a", new byte[] { 0xff, 0, 0x80 } }, await scores);
        Assert.Equal(new double[] { 2, 1, 0 }, (await scoreScores).Select(entry => entry.Score));
        Assert.True(await sortedRemove);
        Assert.Equal(2, await sortedRemoves);
        batch.Execute();
        Assert.Equal(3, database.StringGet("counter"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchErrorsShutdownAndBorrowedOwnershipRemainObservable(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        await client.SetAsync("wrongtype", "value");
        var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        var batch = database.CreateBatch();
        var set = batch.SetLengthAsync("wrongtype");
        var sorted = batch.SortedSetRangeByRankWithScoresAsync("wrongtype");
        var increment = batch.StringIncrementAsync("wrongtype");
        var success = batch.StringIncrementAsync("counter");
        batch.Execute();
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => set)).Code);
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => sorted)).Code);
        Assert.Equal("ERR", (await Assert.ThrowsAsync<RespireServerException>(() => increment)).Code);
        Assert.Equal(1, await success);
        var pending = database.CreateBatch();
        var abandoned = pending.SetMembersAsync("set");
        var read = pending.StringGetAsync("counter");
        await connection.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Throws<ObjectDisposedException>(() => database.KeyExists("counter"));
        Assert.Equal("1", await client.GetAsync<string>("counter"));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BatchScanQueuesEachPageUntilExecuteAndObservesServerErrors(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        var values = Enumerable.Range(0, 150).Select(index => new RedisSortedSetEntry($"member:{index}", index)).ToArray();
        database.SortedSetAdd("scan", values);
        var batch = database.CreateBatch();
        await using var scan = batch.SortedSetScanAsync("scan", pageSize: 5).GetAsyncEnumerator();
        Assert.IsAssignableFrom<IScanningCursor>(scan);
        var found = new List<RedisSortedSetEntry>();
        var move = scan.MoveNextAsync().AsTask();
        Assert.False(move.IsCompleted);
        batch.Execute();
        while (await move)
        {
            found.Add(scan.Current);
            move = scan.MoveNextAsync().AsTask();
            batch.Execute();
        }
        Assert.Equal(values.OrderBy(entry => entry.Score), found.OrderBy(entry => entry.Score));
        database.SetAdd("wrongtype", "value");
        await using var wrong = batch.SortedSetScanAsync("wrongtype").GetAsyncEnumerator();
        var error = wrong.MoveNextAsync().AsTask();
        Assert.False(error.IsCompleted);
        batch.Execute();
        Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(() => error)).Code);
    }

    [Test]
    public async Task FireAndForgetEmptyInputsAndUnsupportedOverloadsHaveExplicitContracts()
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(3));
        var database = connection.GetDatabase();
        Assert.Equal(0, await database.StringIncrementAsync("counter", flags: CommandFlags.FireAndForget));
        Assert.Equal("1", (string?)database.StringGet("counter"));
        Assert.Equal(0, database.StringIncrement("zero-increment", 0, CommandFlags.FireAndForget));
        Assert.Equal(0, await database.StringDecrementAsync("zero-decrement", 0, CommandFlags.FireAndForget));
        Assert.False(database.KeyExists("zero-increment"));
        Assert.False(database.KeyExists("zero-decrement"));
        Assert.Empty(database.SetMembers("set", CommandFlags.FireAndForget));
        Assert.Empty(database.SortedSetRangeByRankWithScores("sorted", flags: CommandFlags.FireAndForget));
        Assert.Null(database.KeyTimeToLive("counter", CommandFlags.FireAndForget));
        Assert.Equal(0, database.SetAdd("set", Array.Empty<RedisValue>()));
        Assert.Equal(0, database.SetRemove("set", Array.Empty<RedisValue>()));
        Assert.Equal(0, database.SortedSetAdd("sorted", Array.Empty<RedisSortedSetEntry>()));
        Assert.Equal(0, database.SortedSetRemove("sorted", Array.Empty<RedisValue>()));
        Assert.Throws<NotSupportedException>(() => database.StringIncrement("counter", 1.5));
        Assert.Throws<NotSupportedException>(() => { _ = database.StringDecrementAsync("counter", 1.5); });
        Assert.Throws<NotSupportedException>(() => database.SortedSetAdd("sorted", "member", 0, When.NotExists));
        Assert.Throws<NotSupportedException>(() => { _ = database.SortedSetAddAsync("sorted", [new RedisSortedSetEntry("member", 0)], SortedSetWhen.Exists); });
        Assert.Throws<NotSupportedException>(() => database.SetLength("set", (CommandFlags)1));
        Assert.Throws<NotSupportedException>(() => { _ = database.SetAddAsync("set", "value", CommandFlags.DemandReplica); });
        Assert.Throws<NotSupportedException>(() => { _ = database.CreateBatch().StringGetAsync("counter", CommandFlags.FireAndForget); });
        Assert.Throws<NotSupportedException>(() => database.SortedSetScan("sorted", flags: CommandFlags.FireAndForget));
        Assert.Throws<ArgumentOutOfRangeException>(() => database.SortedSetScan("sorted", pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => database.SortedSetScanAsync("sorted", pageOffset: -1));
        Assert.Throws<ArgumentNullException>(() => database.StringGet((RedisKey[])null!));
        Assert.Throws<ArgumentNullException>(() => database.SetAdd("set", (RedisValue[])null!));
        Assert.Throws<ArgumentException>(() => database.SetRemove("set", RedisValue.Null));
        Assert.Equal(long.MaxValue, database.StringIncrement("maximum", long.MaxValue));
        Assert.Equal("ERR", (await Assert.ThrowsAsync<RespireServerException>(() => database.StringIncrementAsync("maximum"))).Code);
        Assert.Equal("ERR", (await Assert.ThrowsAsync<RespireServerException>(() => database.StringDecrementAsync("minimum", long.MinValue))).Code);
    }

    [Test]
    public async Task SynchronousScanTimeoutStopsEnumeratorBeforeLatePageCompletes()
    {
        var secondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("ZSCAN key 0", StringComparison.Ordinal)) return "*2\r\n$1\r\n1\r\n*0\r\n"u8.ToArray();
                if (command.StartsWith("EXISTS ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
                return null;
            },
            SuppressReply = command =>
            {
                if (!command.StartsWith("ZSCAN key 1", StringComparison.Ordinal)) return false;
                secondPage.TrySetResult();
                return true;
            },
        };
        server.DelayCommand("ZSCAN key 0", 750);
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
            CommandTimeout = TimeSpan.FromSeconds(2),
        });
        var database = connection.GetDatabase();
        await database.KeyExistsAsync("warm");
        using var scan = database.SortedSetScan("key", "member*").GetEnumerator();
        var position = Assert.IsAssignableFrom<IScanningCursor>(scan);
        var move = Task.Run(scan.MoveNext);
        await secondPage.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => move);
        Assert.Throws<ObjectDisposedException>(scan.Reset);
        Assert.False(scan.MoveNext());
        var cursor = position.Cursor;
        var offset = position.PageOffset;
        await server.SendRawAsync("*2\r\n$1\r\n0\r\n*2\r\n$6\r\nmember\r\n$1\r\n1\r\n"u8.ToArray());
        Assert.False(await database.KeyExistsAsync("barrier"));
        Assert.False(scan.MoveNext());
        Assert.Equal(cursor, position.Cursor);
        Assert.Equal(offset, position.PageOffset);
        Assert.Equal(2, server.ReceivedCommands.Count(command => command.StartsWith("ZSCAN ", StringComparison.Ordinal)));
    }

    [Test]
    public async Task ScanCancellationDuringPageReadLeavesOwnedOperationsObservable()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("ZSCAN ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
        });
        using var cancellation = new CancellationTokenSource();
        await using var scan = connection.GetDatabase().SortedSetScanAsync("key").GetAsyncEnumerator(cancellation.Token);
        var move = scan.MoveNextAsync().AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        await connection.CloseAsync(allowCommandsToComplete: false);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DeferredScanPreservesCursorMetadataAndRedisKeyDiscovery(int protocol)
    {
        var command = DeferredRawCommands.CreateCommand(RespireCommands.SortedSet.ZSCAN, ["ZSCAN", "key", "17"], 1, 1);
        Assert.Equal(ReadCommandKind.CursorRead, command.ReadKind);
        Assert.Equal(1, command.CursorArgumentIndex);
        Assert.True(CursorCommandMetadata.IsCursorContinuation(in command));
        var initial = DeferredRawCommands.CreateCommand(RespireCommands.SortedSet.ZSCAN,
            ["ZSCAN", "key", "0", "MATCH", "prefix*", "COUNT", 10], 1, 1);
        Assert.False(CursorCommandMetadata.IsCursorContinuation(in initial));
        var layout = Respire.Commands.RawCommandKeyLayouts.GetDeferredLayout("ZSCAN", ["key", "17", "MATCH", "prefix*", "COUNT", 10]);
        Assert.Equal(new Respire.Commands.RawCommandKeyLayouts.KeyLayout(0, 1), layout);
        await using var client = await RespireClient.ConnectAsync(Options(protocol));
        using var keys = await client.ExecuteAsync("COMMAND", "GETKEYS", "ZSCAN", "key", "17", "MATCH", "prefix*", "COUNT", 10);
        Assert.Equal(1, keys.Count);
        Assert.Equal("key", keys[0].AsString());
    }
}
