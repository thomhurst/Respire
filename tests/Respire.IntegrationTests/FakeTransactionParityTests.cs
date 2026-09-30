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
                aborted.AsString().Should().StartWith("EXECABORT");
            (await observer.ExistsAsync("never")).Should().BeFalse();
        }
        await observer.SetAsync("wrong", "not a number");
        await Text(session, "OK", "WATCH", "watched");
        await Text(session, "OK", "MULTI");
        using (var nested = await session.CommandAsync("MULTI")) nested.AsString().Should().Contain("nested");
        using (var watch = await session.CommandAsync("WATCH", "other")) watch.AsString().Should().Contain("inside MULTI");
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
            invalid.AsString().Should().Be($"ERR {command} without MULTI");
        }
    }

    private sealed record WatchCase(string[][] Setup, string[] Mutation, bool Changes, bool Error = false);

    private RespireOptions Options(RespireFakeServer? fake, int protocol)
        => (fake?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString)) with { Protocol = (RespProtocol)protocol };

    private static async Task Text(TestRespSession session, string expected, params string[] arguments)
    {
        using var reply = await session.CommandAsync(arguments);
        reply.AsString().Should().Be(expected);
    }
}
