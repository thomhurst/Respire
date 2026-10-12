using System.Security.Cryptography;
using System.Text;
using Respire.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public class OrleansCommandsTests(RedisTestContainer redis)
{
    private RespireOptions Options(int protocol) => RespireOptions.Parse(redis.ConnectionString) with
    {
        Connections = 1,
        Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
    };

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScriptsPreserveBinaryAndNestedRepliesAcrossDispatchPaths(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        byte[] key = [0xff, 0, 0x80];
        byte[] value = [0xfe, 0, 0x81];
        const string script = "return {42, ARGV[1], false, {KEYS[1], -7}}";
        Check(database.ScriptEvaluate(script, [key], [value]));
        Check(await database.ScriptEvaluateAsync(script, [key], [value]));
        var batch = database.CreateBatch();
        var queued = batch.ScriptEvaluateAsync(script, [key], [value]);
        Assert.False(queued.IsCompleted);
        batch.Execute();
        Check(await queued);
        var transaction = database.CreateTransaction();
        queued = transaction.ScriptEvaluateAsync(script, [key], [value]);
        Assert.False(queued.IsCompleted);
        Assert.True(await transaction.ExecuteAsync());
        Check(await queued);
        Assert.Equal(9, (long)await database.ScriptEvaluateAsync("return 9"));
        Assert.True((await database.ScriptEvaluateAsync("return false")).IsNull);
        Assert.True((await database.ScriptEvaluateAsync("return 9", flags: CommandFlags.FireAndForget)).IsNull);
        Assert.Equal(10, (long)await database.ScriptEvaluateAsync("return 10"));

        void Check(RedisResult result)
        {
            var reply = (RedisResult[])result!;
            Assert.Equal(42, (long)reply[0]);
            Assert.Equal(value, (byte[]?)reply[1]);
            Assert.True(reply[2].IsNull);
            var nested = (RedisResult[])reply[3]!;
            Assert.Equal(key, (byte[]?)nested[0]);
            Assert.Equal(-7, (long)nested[1]);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScriptArgumentsAreCopiedBeforeDeferredExecution(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        foreach (var transactionMode in new[] { false, true })
        {
            byte[] key = [0xff, 0];
            byte[] value = [0xfe, 0];
            RedisKey[] keys = [key];
            RedisValue[] values = [value];
            var queue = transactionMode ? database.CreateTransaction() : database.CreateBatch();
            var pending = queue.ScriptEvaluateAsync("return {KEYS[1], ARGV[1]}", keys, values);
            key[0] = value[0] = 0;
            keys[0] = "changed-key";
            values[0] = "changed-value";
            if (queue is ITransaction transaction) Assert.True(await transaction.ExecuteAsync());
            else queue.Execute();
            var reply = (RedisResult[])(await pending)!;
            Assert.Equal(new byte[] { 0xff, 0 }, (byte[]?)reply[0]);
            Assert.Equal(new byte[] { 0xfe, 0 }, (byte[]?)reply[1]);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ScriptHashesAndServerErrorsRetainRedisBehavior(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        var database = connection.GetDatabase();
        const string script = "return ARGV[1]";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
        Assert.Equal("loaded", (string?)await database.ScriptEvaluateAsync(script, values: ["loaded"]));
        Assert.Equal("hash", (string?)await database.ScriptEvaluateAsync(hash, values: ["hash"]));
        var hashBatch = database.CreateBatch();
        var hashReply = hashBatch.ScriptEvaluateAsync(hash, values: ["batch-hash"]);
        hashBatch.Execute();
        Assert.Equal("batch-hash", (string?)await hashReply);
        var hashTransaction = database.CreateTransaction();
        hashReply = hashTransaction.ScriptEvaluateAsync(hash, values: ["transaction-hash"]);
        Assert.True(await hashTransaction.ExecuteAsync());
        Assert.Equal("transaction-hash", (string?)await hashReply);
        var missing = await Assert.ThrowsAsync<RespireServerException>(() => database.ScriptEvaluateAsync(new string('0', 40)));
        Assert.Contains("NOSCRIPT", missing.Message);
        var error = await Assert.ThrowsAsync<RespireServerException>(() => database.ScriptEvaluateAsync("return redis.error_reply('provider-failure')"));
        Assert.Contains("provider-failure", error.Message);
        var batch = database.CreateBatch();
        var failed = batch.ScriptEvaluateAsync("return redis.error_reply('batch-failure')");
        var success = batch.ScriptEvaluateAsync("return 21");
        batch.Execute();
        Assert.Contains("batch-failure", (await Assert.ThrowsAsync<RespireServerException>(() => failed)).Message);
        Assert.Equal(21, (long)await success);
        Assert.Throws<NotSupportedException>(() => database.ScriptEvaluate(new byte[20]));
        Assert.Throws<NotSupportedException>(() => database.ScriptEvaluate(LuaScript.Prepare("return 1")));
        Assert.Throws<ArgumentNullException>(() => database.ScriptEvaluate((string)null!));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LexicographicReadsMatchUpstreamBoundsAndPagination(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        RedisValue[] members = [Array.Empty<byte>(), new byte[] { 0 }, "+", "-", "[", "a", "b", new byte[] { 0xff, 0 }, 1, 2];
        await expected.SortedSetAddAsync("lex", members.Select(static member => new StackExchange.Redis.SortedSetEntry(member, 0)).ToArray());
        (RedisValue Min, RedisValue Max)[] bounds =
        [
            (RedisValue.Null, RedisValue.Null), (RedisValue.Null, "b"), ("a", RedisValue.Null),
            ("a", "b"), ("b", "a"), (Array.Empty<byte>(), "b"),
            (new byte[] { 0 }, new byte[] { 0xff, 0 }), (1, 2), (2, 1),
        ];
        foreach (var (min, max) in bounds)
        foreach (var exclude in new[] { Exclude.None, Exclude.Start, Exclude.Stop, Exclude.Both })
        foreach (var order in new[] { Order.Ascending, Order.Descending })
        foreach (var (skip, take) in new[] { (0L, -1L), (1L, 2L), (0L, 0L) })
        {
            var result = await expected.SortedSetRangeByValueAsync("lex", min, max, exclude, order, skip, take);
            Assert.Equal(result, await database.SortedSetRangeByValueAsync("lex", min, max, exclude, order, skip, take));
            Assert.Equal(result, database.SortedSetRangeByValue("lex", min, max, exclude, order, skip, take));
        }
        var ascending = await expected.SortedSetRangeByValueAsync("lex", "a", "b", Exclude.Start, 0, -1, CommandFlags.None);
        Assert.Equal(ascending, await database.SortedSetRangeByValueAsync("lex", "a", "b", Exclude.Start, 0, -1, CommandFlags.None));
        Assert.Equal(ascending, database.SortedSetRangeByValue("lex", "a", "b", Exclude.Start, 0, -1, CommandFlags.None));
        var batch = database.CreateBatch();
        var pending = batch.SortedSetRangeByValueAsync("lex", RedisValue.Null, RedisValue.Null);
        Assert.False(pending.IsCompleted);
        batch.Execute();
        Assert.Equal(await expected.SortedSetRangeByValueAsync("lex"), await pending);
        var transaction = database.CreateTransaction();
        pending = transaction.SortedSetRangeByValueAsync("lex", RedisValue.Null, RedisValue.Null, order: Order.Descending);
        Assert.True(await transaction.ExecuteAsync());
        Assert.Equal(await expected.SortedSetRangeByValueAsync("lex", order: Order.Descending), await pending);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LexicographicRemovalMatchesUpstreamAcrossDispatchPaths(int protocol)
    {
        await using var connection = RespireConnectionMultiplexer.Create(Options(protocol));
        using var control = await ConnectionMultiplexer.ConnectAsync(redis.StackExchangeConnectionString);
        var database = connection.GetDatabase();
        var expected = control.GetDatabase();
        StackExchange.Redis.SortedSetEntry[] members = [new("a", 0), new("b", 0), new("c", 0), new(new byte[] { 0xff, 0 }, 0)];
        foreach (var (min, max) in new (RedisValue, RedisValue)[] { ("a", "c"), ("c", "a"), (RedisValue.Null, RedisValue.Null), ("c", new byte[] { 0xff, 0 }) })
        foreach (var exclude in new[] { Exclude.None, Exclude.Start, Exclude.Stop, Exclude.Both })
        foreach (var path in new[] { "async", "sync", "batch", "transaction" })
        {
            await expected.KeyDeleteAsync(["control", "shim"]);
            await expected.SortedSetAddAsync("control", members);
            await expected.SortedSetAddAsync("shim", members);
            var count = await expected.SortedSetRemoveRangeByValueAsync("control", min, max, exclude);
            if (path == "sync") Assert.Equal(count, database.SortedSetRemoveRangeByValue("shim", min, max, exclude));
            else if (path == "async") Assert.Equal(count, await database.SortedSetRemoveRangeByValueAsync("shim", min, max, exclude));
            else
            {
                var queue = path == "batch" ? database.CreateBatch() : database.CreateTransaction();
                var pending = queue.SortedSetRemoveRangeByValueAsync("shim", min, max, exclude);
                Assert.False(pending.IsCompleted);
                if (queue is ITransaction transaction) Assert.True(await transaction.ExecuteAsync());
                else queue.Execute();
                Assert.Equal(count, await pending);
            }
            Assert.Equal(await expected.SortedSetRangeByRankAsync("control"), await expected.SortedSetRangeByRankAsync("shim"));
        }
    }
}
