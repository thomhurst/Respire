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
        var options = Options(fake, protocol);
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var writer = await TestRespSession.ConnectAsync(options);
        foreach (var scenario in new WatchCase[]
        {
            new([ ["SET", "key", "value"] ], ["SET", "key", "value"], true),
            new([ ["SET", "key", "value"] ], ["SET", "key", "new", "NX"], false),
            new([], ["SET", "key", "new", "XX"], false),
            new([ ["SET", "key", "value"] ], ["APPEND", "key", ""], true),
            new([ ["SET", "key", "1"] ], ["INCRBY", "key", "0"], true),
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
        })
        {
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

    private sealed record WatchCase(string[][] Setup, string[] Mutation, bool Changes, bool Error = false);

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task MalformedExecAbortsImmediatelyAndClearsWatch(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        var options = Options(fake, protocol);
        await using var session = await TestRespSession.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        await Text(session, "OK", "WATCH", "key");
        await Text(session, "OK", "MULTI");
        await Text(session, "QUEUED", "SET", "discarded", "never");
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
