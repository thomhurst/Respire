using FluentAssertions;
using Respire.TestSupport;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FakeTransactionParityTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task QueueErrorsAbortButExecutionErrorsPreserveOtherResults(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        foreach (var invalid in new[] { new[] { "INCR" }, new[] { "NOT-A-COMMAND", "key" }, new[] { "CLIENT", "ID", "extra" } })
        {
            await Text(session, "OK", "MULTI");
            using (var error = await session.CommandAsync(invalid)) error.IsError.Should().BeTrue();
            await Text(session, "QUEUED", "SET", "never", "value");
            using (var aborted = await session.CommandAsync("EXEC"))
                aborted.GetErrorMessage().Should().StartWith("EXECABORT");
            (await observer.ExistsAsync("never")).Should().BeFalse();
        }
        await observer.SetAsync("wrong", "not a number");
        await Text(session, "OK", "WATCH", "watched");
        await Text(session, "OK", "MULTI");
        using (var nested = await session.CommandAsync("MULTI")) nested.GetErrorMessage().Should().Contain("nested");
        using (var watch = await session.CommandAsync("WATCH", "other")) watch.GetErrorMessage().Should().Contain("inside MULTI");
        await Text(session, "QUEUED", "SET", "never", "value", "invalid-option");
        await Text(session, "QUEUED", "INCR", "wrong");
        await Text(session, "QUEUED", "SET", "applied", "value");
        await Text(session, "QUEUED", "UNWATCH");
        using (var executed = await session.CommandAsync("EXEC"))
        {
            executed.AsArray().Length.Should().Be(4);
            executed.AsArray()[0].IsError.Should().BeTrue();
            executed.AsArray()[1].IsError.Should().BeTrue();
            executed.AsArray()[2].AsString().Should().Be("OK");
            executed.AsArray()[3].AsString().Should().Be("OK");
        }
        (await observer.GetStringAsync("applied")).Should().Be("value");
        (await observer.ExistsAsync("never")).Should().BeFalse();
        await Text(session, "PONG", "PING");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task HandlerSyntaxErrorsAreQueuedAndDoNotAbortOtherCommands(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var session = await TestRespSession.ConnectAsync(Options(fake, protocol));
        string[][] invalid =
        [
            ["PING", "one", "two"], ["LPOP", "list", "1", "extra"],
            ["RPOP", "list", "1", "extra"], ["MSET", "key", "value", "unpaired"],
            ["HSET", "hash", "field", "value", "unpaired"], ["SINTERCARD", "0", "key"],
            ["PEXPIRE", "key", "bad"], ["GETEX", "key", "bad"],
            ["LSET", "missing-list", "0", "value"], ["CLIENT", "SETNAME", "invalid name"],
        ];
        await Text(session, "OK", "MULTI");
        foreach (var arguments in invalid) await Text(session, "QUEUED", arguments);
        await Text(session, "QUEUED", "PING");
        using var executed = await session.CommandAsync("EXEC");
        executed.AsArray().Length.Should().Be(invalid.Length + 1);
        for (var index = 0; index < invalid.Length; index++)
            executed.AsArray()[index].IsError.Should().BeTrue(string.Join(' ', invalid[index]));
        executed.AsArray()[invalid.Length].AsString().Should().Be("PONG");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task ConcurrentWatchersHaveExactlyOneWinner(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var first = await RespireClient.ConnectAsync(options);
        await using var second = await RespireClient.ConnectAsync(options);
        await first.SetAsync("contended", "0");
        await using var left = await first.CreateTransactionAsync(["contended"]);
        await using var right = await second.CreateTransactionAsync(["contended"]);
        var leftResult = left.Increment("contended");
        var rightResult = right.Increment("contended");
        var results = await Task.WhenAll(left.CommitAsync().AsTask(), right.CommitAsync().AsTask());
        results.Count(committed => committed).Should().Be(1);
        (results[0] ? leftResult : rightResult).Result.Should().Be(1);
        (results[0] ? rightResult : leftResult).Status.Should().Be(RespirePendingStatus.Aborted);
        (await first.GetStringAsync("contended")).Should().Be("1");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WatchTracksMutationsButNotRejectedOrNoOpWrites(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await AssertWatchTracksMutationsAsync(Options(fake, protocol), useFake);
    }

    internal static async Task AssertWatchTracksMutationsAsync(RespireOptions options, bool useFake, bool requireArrays = false)
    {
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var writer = await TestRespSession.ConnectAsync(options);
        var scenarios = new WatchCase[]
        {
            new([], ["ARSET", "key", "0", "value"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARSET", "key", "0", "value"], true),
            new([], ["ARMSET", "key", "0", "value", "2", "other"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARMSET", "key", "0", "value"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARDEL", "key", "0"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARDEL", "key", "1"], false),
            new([], ["ARDEL", "key", "0"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARDELRANGE", "key", "0", "1"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARDELRANGE", "key", "1", "2"], false),
            new([], ["ARDELRANGE", "key", "0", "1"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARSEEK", "key", "0"], true),
            new([], ["ARSEEK", "key", "0"], false),
            new([], ["ARINSERT", "key", "value"], true),
            new([], ["ARRING", "key", "2", "value"], true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARRING", "key", "0", "value"], false, Error: true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARSET", "key", "18446744073709551615", "new"], false, Error: true),
            new([ ["SET", "key", "wrong-type"] ], ["ARINSERT", "key", "value"], false, Error: true),
            new([ ["ARSET", "key", "0", "value"] ], ["ARCOUNT", "key"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARGET", "key", "0"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARGETRANGE", "key", "0", "1"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARGREP", "key", "-", "+", "EXACT", "value"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARINFO", "key", "FULL"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARLASTITEMS", "key", "2"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARLEN", "key"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARMGET", "key", "0", "1"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARNEXT", "key"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["AROP", "key", "0", "1", "USED"], false),
            new([ ["ARSET", "key", "0", "value"] ], ["ARSCAN", "key", "0", "1"], false),
            new([], ["XADD", "key", "1-0", "f", "value"], true),
            new([ ["XADD", "key", "1-0", "f", "value"] ], ["XADD", "key", "1-0", "f", "duplicate"], false, Error: true),
            new([ ["XADD", "key", "1-0", "f", "value"] ], ["XGROUP", "CREATE", "key", "g", "0"], false),
            new([], ["XGROUP", "CREATE", "key", "g", "0", "MKSTREAM"], true),
            new([ ["XGROUP", "CREATE", "key", "g", "0", "MKSTREAM"] ], ["XGROUP", "CREATE", "key", "g", "0"], false, Error: true),
            new([ ["XADD", "key", "1-0", "f", "value"] ], ["XREAD", "STREAMS", "key", "0"], false),
            new([ ["XADD", "key", "1-0", "f", "value"], ["XGROUP", "CREATE", "key", "g", "0"] ],
                ["XREADGROUP", "GROUP", "g", "c", "STREAMS", "key", ">"], false),
            new([ ["XGROUP", "CREATE", "key", "g", "0", "MKSTREAM"] ],
                ["XREADGROUP", "GROUP", "g", "c", "STREAMS", "key", ">"], false),
            new([ ["XADD", "key", "1-0", "f", "value"], ["XGROUP", "CREATE", "key", "g", "0"],
                ["XREADGROUP", "GROUP", "g", "c", "STREAMS", "key", ">"] ], ["XACK", "key", "g", "1-0"], false),
            new([ ["XADD", "key", "1-0", "f", "value"], ["XGROUP", "CREATE", "key", "g", "0"],
                ["XREADGROUP", "GROUP", "g", "c", "STREAMS", "key", ">"] ],
                ["XREADGROUP", "GROUP", "g", "c", "STREAMS", "key", "0"], false),
            new([ ["XGROUP", "CREATE", "key", "g", "0", "MKSTREAM"] ], ["XACK", "key", "g", "1-0"], false),
            new([ ["SET", "key", "value"] ], ["SET", "key", "value"], true),
            new([ ["SET", "key", "value"] ], ["SET", "key", "new", "NX"], false),
            new([], ["SET", "key", "new", "XX"], false),
            new([ ["SET", "key", "value"] ], ["APPEND", "key", ""], true),
            new([ ["SET", "key", "1"] ], ["INCRBY", "key", "0"], true),
            new([], ["INCREX", "key"], true),
            new([ ["SET", "key", "1"] ], ["INCREX", "key", "BYINT", "0"], true),
            new([ ["SET", "key", "1"] ], ["INCREX", "key", "UBOUND", "1"], false),
            new([ ["SET", "key", "1"] ], ["INCREX", "key", "UBOUND", "1", "SATURATE"], true),
            new([ ["SET", "key", "wrong"] ], ["INCREX", "key"], false, Error: true),
            new([ ["SET", "key", "wrong"] ], ["INCR", "key"], false, Error: true),
            new([], ["GETDEL", "key"], false),
            new([ ["SET", "key", "value"] ], ["GETDEL", "key"], true),
            new([ ["SET", "key", "value"] ], ["GETEX", "key", "PERSIST"], false),
            new([ ["SET", "key", "value", "PX", "60000"] ], ["GETEX", "key", "PERSIST"], true),
            new([ ["SET", "key", "value"] ], ["GETEX", "key", "PX", "60000"], true),
            new([ ["SET", "key", "value"] ], ["PERSIST", "key"], false),
            new([ ["SET", "key", "value"] ], ["PEXPIRE", "key", "60000"], true),
            new([ ["SET", "key", "value"] ], ["PEXPIRE", "key", "60000", "XX"], false),
            new([], ["DEL", "key"], false),
            new([ ["SET", "key", "value"] ], ["UNLINK", "key"], true),
            new([], ["MSET", "other", "1", "key", "2"], true),
            new([ ["SET", "other", "1"] ], ["MSETNX", "other", "2", "key", "3"], false),
            new([ ["HSET", "key", "field", "value"] ], ["HSET", "key", "field", "value"], true),
            new([ ["HSET", "key", "field", "value"] ], ["HSETNX", "key", "field", "new"], false),
            new([ ["HSET", "key", "field", "value"] ], ["HDEL", "key", "missing"], false),
            new([ ["HSET", "key", "field", "1"] ], ["HINCRBY", "key", "field", "0"], true),
            new([ ["SADD", "key", "member"] ], ["SADD", "key", "member"], false),
            new([ ["SADD", "key", "member"] ], ["SADD", "key", "new"], true),
            new([ ["SADD", "key", "member"] ], ["SREM", "key", "missing"], false),
            new([ ["SADD", "key", "member"] ], ["SMOVE", "key", "key", "member"], false),
            new([ ["SADD", "other", "member"], ["SADD", "key", "member"] ], ["SMOVE", "other", "key", "member"], false),
            new([ ["SADD", "other", "member"] ], ["SMOVE", "other", "key", "member"], true),
            new([ ["SADD", "key", "member"] ], ["SINTERSTORE", "key", "key"], true),
            new([], ["SINTERSTORE", "key", "missing"], false),
            new([ ["SET", "key", "value"] ], ["SINTERSTORE", "key", "missing"], true),
            new([], ["LPUSH", "key", "value"], true),
            new([], ["LPUSHX", "key", "value"], false),
            new([ ["RPUSH", "key", "value"] ], ["RPUSHX", "key", "value"], true),
            new([ ["RPUSH", "key", "value"] ], ["LPOP", "key", "0"], false),
            new([ ["RPUSH", "key", "value"] ], ["RPOP", "key"], true),
            new([ ["RPUSH", "key", "value"] ], ["LSET", "key", "0", "value"], true),
            new([ ["RPUSH", "key", "value"] ], ["LSET", "key", "1", "value"], false, Error: true),
            new([ ["RPUSH", "key", "value"] ], ["LTRIM", "key", "0", "-1"], true),
            new([], ["LTRIM", "key", "0", "-1"], false),
            new([ ["RPUSH", "key", "value"] ], ["LREM", "key", "0", "missing"], false),
            new([ ["RPUSH", "key", "value"] ], ["LREM", "key", "0", "value"], true),
            new([ ["RPUSH", "key", "value"] ], ["LINSERT", "key", "BEFORE", "missing", "new"], false),
            new([ ["RPUSH", "key", "value"] ], ["LINSERT", "key", "BEFORE", "value", "new"], true),
            new([ ["RPUSH", "key", "value"] ], ["LMOVEM", "key", "other", "LEFT", "RIGHT"], true),
            new([ ["RPUSH", "other", "value"] ], ["LMOVEM", "other", "key", "LEFT", "RIGHT"], true),
            new([ ["RPUSH", "key", "value"] ], ["LMOVEM", "key", "key", "LEFT", "RIGHT"], true),
            new([ ["RPUSH", "key", "value"] ], ["LMOVEM", "key", "other", "LEFT", "RIGHT", "EXACTLY", "2", "BULK"], false),
            new([ ["RPUSH", "key", "value"], ["SET", "other", "wrong"] ], ["LMOVEM", "key", "other", "LEFT", "RIGHT"], false, Error: true),
            new([], ["LMOVEM", "key", "other", "LEFT", "RIGHT"], false),
            new([ ["RPUSH", "key", "value"] ], ["BLMOVEM", "key", "other", "LEFT", "RIGHT", "0.001"], true),
            new([ ["RPUSH", "other", "value"] ], ["BLMOVEM", "other", "key", "LEFT", "RIGHT", "0.001"], true),
            new([ ["RPUSH", "key", "value"] ], ["BLMOVEM", "key", "key", "LEFT", "RIGHT", "0.001"], true),
            new([ ["RPUSH", "key", "value"] ], ["BLMOVEM", "key", "other", "LEFT", "RIGHT", "0.001", "EXACTLY", "2", "BULK"], false),
            new([ ["RPUSH", "key", "value"], ["SET", "other", "wrong"] ], ["BLMOVEM", "key", "other", "LEFT", "RIGHT", "0.001"], false, Error: true),
            new([], ["BLMOVEM", "key", "other", "LEFT", "RIGHT", "0.001"], false),
            new([], ["ZADD", "key", "1", "member"], true),
            new([], ["ZADD", "key", "XX", "1", "member"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZADD", "key", "1", "member"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZADD", "key", "2", "member"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZADD", "key", "NX", "2", "member"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZADD", "key", "GT", "0", "member"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZINCRBY", "key", "0", "member"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZINCRBY", "key", "1", "member"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREM", "key", "missing"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREM", "key", "member"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZPOPMIN", "key", "0"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZPOPMAX", "key"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREMRANGEBYRANK", "key", "0", "-1"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREMRANGEBYSCORE", "key", "2", "3"], false),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREMRANGEBYSCORE", "key", "1", "1"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZREMRANGEBYLEX", "key", "-", "+"], true),
            new([], ["INCR", "key"], true),
            new([], ["DECR", "key"], true),
            new([], ["DECRBY", "key", "2"], true),
            new([], ["GETSET", "key", "new"], true),
            new([ ["SET", "key", "old"] ], ["DEL", "key"], true),
            new([], ["MSETNX", "key", "new"], true),
            new([ ["SET", "key", "old", "PX", "60000"] ], ["PERSIST", "key"], true),
            new([ ["SET", "key", "old"] ], ["EXPIRE", "key", "60"], true),
            new([ ["SET", "key", "old"] ], ["EXPIREAT", "key", "1"], true),
            new([ ["SET", "key", "old"] ], ["PEXPIREAT", "key", "1"], true),
            new([], ["HMSET", "key", "field", "new"], true),
            new([], ["HSETNX", "key", "field", "new"], true),
            new([ ["HSET", "key", "field", "old"] ], ["HDEL", "key", "field"], true),
            new([ ["SADD", "key", "member"] ], ["SREM", "key", "member"], true),
            new([ ["SADD", "other", "member"] ], ["SUNIONSTORE", "key", "other"], true),
            new([ ["SADD", "other", "member"] ], ["SDIFFSTORE", "key", "other"], true),
            new([], ["RPUSH", "key", "new"], true),
            new([ ["LPUSH", "key", "old"] ], ["LPUSHX", "key", "new"], true),
            new([ ["LPUSH", "key", "old"] ], ["LPOP", "key"], true),
            new([ ["ZADD", "key", "1", "member"] ], ["ZPOPMIN", "key"], true),
            new([], ["XADD", "key", "*", "field", "value"], true),
            new([], ["XADD", "key", "NOMKSTREAM", "*", "field", "value"], false),
            new([ ["XADD", "key", "*", "field", "value"] ], ["XCFGSET", "key", "IDMP-MAXSIZE", "1"], false),
            new([ ["XADD", "key", "*", "field", "value"] ], ["XCFGSET", "key", "IDMP-MAXSIZE", "100"], false),
            new([ ["XADD", "key", "*", "field", "value"] ], ["XCFGSET", "key", "IDMP-MAXSIZE", "0"], false, Error: true),
            new([ ["HIMPORT", "PREPARE", "watch-import", "field"] ], ["HIMPORT", "SET", "key", "watch-import", "value"], true),
        };
        if (useFake) AssertMutationCoverage(scenarios);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        if (!useFake)
        {
            var optionalCommands = new[] { "LMOVEM", "BLMOVEM", "INCREX", "XCFGSET", "HIMPORT" }
                .Concat(scenarios.Select(scenario => scenario.Mutation[0]).Where(command => command.StartsWith("AR", StringComparison.Ordinal)).Distinct());
            foreach (var command in optionalCommands)
            {
                using var info = await writer.CommandAsync("COMMAND", "INFO", command);
                if (info.AsArray()[0].IsNull) unsupported.Add(command);
            }
            if (requireArrays)
                unsupported.Where(command => command.StartsWith("AR", StringComparison.Ordinal)).Should().BeEmpty(
                    "the Redis 8.10 acceptance server must execute every array WATCH parity vector");
        }
        foreach (var scenario in scenarios)
        {
            if (unsupported.Contains(scenario.Mutation[0])) continue;
            using (var cleared = await writer.CommandAsync("DEL", "key", "other", "marker")) { }
            foreach (var setup in scenario.Setup) using (var reply = await writer.CommandAsync(setup)) reply.IsError.Should().BeFalse();
            await Text(session, "OK", "WATCH", "key");
            using (var changed = await writer.CommandAsync(scenario.Mutation))
                changed.IsError.Should().Be(scenario.Error, string.Join(' ', scenario.Mutation));
            await Text(session, "OK", "MULTI");
            await Text(session, "QUEUED", "SET", "marker", "committed");
            using var executed = await session.CommandAsync("EXEC");
            executed.IsNull.Should().Be(scenario.Changes, string.Join(' ', scenario.Mutation));
            if (!scenario.Changes) executed.AsArray()[0].AsString().Should().Be("OK");
        }
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task DiscardUnwatchAndExecClearConnectionState(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var writer = await RespireClient.ConnectAsync(options);
        foreach (var reset in new[] { "UNWATCH", "DISCARD", "EXEC" })
        {
            await Text(session, "OK", "WATCH", "key", "key");
            await writer.SetAsync("key", "changed");
            if (reset != "UNWATCH") await Text(session, "OK", "MULTI");
            using (var cleared = await session.CommandAsync(reset))
            {
                if (reset == "EXEC") cleared.IsNull.Should().BeTrue();
                else cleared.AsString().Should().Be("OK");
            }
            await Text(session, "OK", "MULTI");
            await Text(session, "QUEUED", "SET", "result", reset);
            using var executed = await session.CommandAsync("EXEC");
            executed.AsArray()[0].AsString().Should().Be("OK");
        }
        await Text(session, "OK", "MULTI");
        await Text(session, "QUEUED", "SET", "discarded", "value");
        await Text(session, "OK", "DISCARD");
        (await writer.ExistsAsync("discarded")).Should().BeFalse();
        foreach (var command in new[] { "EXEC", "DISCARD" })
        {
            using var invalid = await session.CommandAsync(command);
            invalid.GetErrorMessage().Should().Be($"ERR {command} without MULTI");
        }
    }

    private static void AssertMutationCoverage(WatchCase[] scenarios)
    {
        // Every new fake command must be classified here or gain a successful WATCH
        // invalidation case above. Real Redis runs the same vectors to check semantics.
        string[] nonInvalidatingCommands =
        [
            "HELLO", "MULTI", "EXEC", "DISCARD", "WATCH", "UNWATCH", "PING", "ECHO",
            "ARCOUNT", "ARGET", "ARGETRANGE", "ARGREP", "ARINFO", "ARLASTITEMS", "ARLEN",
            "ARMGET", "ARNEXT", "AROP", "ARSCAN",
            "HIMPORT PREPARE", "HIMPORT DISCARD", "HIMPORT DISCARDALL",
            "SUBSCRIBE", "UNSUBSCRIBE", "PUBLISH", "XREAD",
            // Group cursor/PEL changes do not invalidate WATCH, unlike XGROUP CREATE ... MKSTREAM.
            "XREADGROUP", "XACK",
            "SELECT", "CLIENT", "GET", "MGET", "EXISTS", "TYPE", "STRLEN", "TTL",
            "PTTL", "EXPIRETIME", "PEXPIRETIME", "HGET", "HMGET", "HGETALL", "HEXISTS", "HLEN",
            "HKEYS", "HVALS", "HSTRLEN", "SMEMBERS", "SCARD", "SISMEMBER", "SMISMEMBER", "SINTER",
            "SUNION", "SDIFF", "SINTERCARD", "SDIFFCARD", "SUNIONCARD", "LLEN", "LRANGE", "LINDEX", "LPOS", "ZCARD",
            "ZSCORE", "ZMSCORE", "ZRANK", "ZREVRANK", "ZCOUNT", "ZLEXCOUNT", "ZRANGE", "ZREVRANGE",
            "ZRANGEBYSCORE", "ZREVRANGEBYSCORE", "ZRANGEBYLEX", "ZREVRANGEBYLEX", "ZINTERCARD", "XLEN",
            // XCFGSET changes metadata without signaling watched keys (Redis keyModified signal=0).
            "XCFGSET",
        ];
        var commands = (System.Collections.IDictionary)typeof(RespireFakeServer)
            .GetField("Commands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var covered = scenarios.Where(scenario => scenario.Changes && !scenario.Error)
            .Select(scenario => scenario.Mutation[0] == "HIMPORT"
                ? $"HIMPORT {scenario.Mutation[1]}" : scenario.Mutation[0]).Distinct();
        commands.Keys.Cast<string>().Except(nonInvalidatingCommands).Should().BeEquivalentTo(covered,
            "every WATCH-invalidating fake command needs a successful invalidation parity case");
    }

    private sealed record WatchCase(string[][] Setup, string[] Mutation, bool Changes, bool Error = false);

    [Test]
    [Arguments(false, 2, false)]
    [Arguments(false, 3, false)]
    [Arguments(true, 2, false)]
    [Arguments(true, 3, false)]
    [Arguments(false, 2, true)]
    [Arguments(false, 3, true)]
    [Arguments(true, 2, true)]
    [Arguments(true, 3, true)]
    public async Task MalformedExecAbortsImmediatelyAndClearsWatch(bool useFake, int protocol, bool multi)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        await Text(session, "OK", "WATCH", "key");
        if (multi)
        {
            await Text(session, "OK", "MULTI");
            await Text(session, "QUEUED", "SET", "discarded", "never");
        }
        using (var malformed = await session.CommandAsync("EXEC", "extra"))
            malformed.GetErrorMessage().Should().Be("EXECABORT Transaction discarded because of: wrong number of arguments for 'exec' command");
        (await observer.ExistsAsync("discarded")).Should().BeFalse();
        await Text(session, "OK", "SET", "key", "outside transaction");
        (await observer.GetStringAsync("key")).Should().Be("outside transaction");
        using (var invalid = await session.CommandAsync("EXEC"))
            invalid.GetErrorMessage().Should().Be("ERR EXEC without MULTI");
        await Text(session, "OK", "MULTI");
        await Text(session, "QUEUED", "SET", "key", "next");
        using var executed = await session.CommandAsync("EXEC");
        executed.AsArray()[0].AsString().Should().Be("OK");
    }

    private RespireOptions Options(RespireFakeServer? fake, int protocol)
        => (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol };

    private static async Task Text(TestRespSession session, string expected, params string[] arguments)
    {
        using var reply = await session.CommandAsync(arguments);
        reply.AsString().Should().Be(expected);
    }
}
