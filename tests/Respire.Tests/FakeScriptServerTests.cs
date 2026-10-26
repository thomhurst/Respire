using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeScriptServerTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RegistrationDoesNotLoadAndClientFallbackCachesExactSource(int protocol)
    {
        var script = RespireScript.Create("return ARGV[1]");
        await using var server = new RespireFakeServer();
        server.RegisterScript(script, (_, _, args) => RespireFakeResponse.Bulk(args[0].ToString()));
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var missing = await session.CommandAsync("EVALSHA", script.Sha1, "0", "value"))
            await Assert.That(missing.IsError && missing.ToString().Contains("NOSCRIPT")).IsTrue();
        using (var exists = await session.CommandAsync("SCRIPT", "EXISTS", script.Sha1))
            await Assert.That(exists.AsArray()[0].AsInteger()).IsEqualTo(0);
        server.ExpectationMode = RespireFakeExpectationMode.Loose;
#pragma warning disable RESP010 // This client belongs to the isolated fake server.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        await Assert.That(await client.Scripts.ExecuteStringAsync(script, [], ["first"])).IsEqualTo("first");
        await Assert.That(await client.Scripts.ExecuteStringAsync(script, [], ["second"])).IsEqualTo("second");
        await Assert.That(server.CommandHistory.Where(command => command.Command is "EVAL" or "EVALSHA")
            .Select(command => command.Command)).IsEquivalentTo(new[] { "EVALSHA", "EVAL", "EVALSHA" });
        using (var exists = await session.CommandAsync("SCRIPT", "EXISTS", script.Sha1, "unknown"))
        {
            await Assert.That(exists.AsArray()[0].AsInteger()).IsEqualTo(1);
            await Assert.That(exists.AsArray()[1].AsInteger()).IsEqualTo(0);
        }
        using (var flushed = await session.CommandAsync("SCRIPT", "FLUSH")) { }
        await Assert.That(await client.Scripts.ExecuteStringAsync(script, [], ["after flush"])).IsEqualTo("after flush");
        using (var changed = await session.CommandAsync("EVAL", script.Source + " ", "0", "value"))
            await Assert.That(changed.IsError && changed.ToString().Contains("unsupported script")).IsTrue();
    }

    [Test]
    public async Task LoadFlushAndInvalidKeyCountsDoNotInvokeHandler()
    {
        var script = RespireScript.Create("return 42");
        var calls = 0;
        await using var server = new RespireFakeServer();
        server.RegisterScript(script, (_, _, _) => { calls++; return RespireFakeResponse.Integer(42); });
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var loaded = await session.CommandAsync("SCRIPT", "LOAD", script.Source))
            await Assert.That(loaded.AsString()).IsEqualTo(script.Sha1);
        foreach (var count in new[] { "-1", "1", "2147483648", "bad" })
        {
            using var invalid = await session.CommandAsync("EVALSHA", script.Sha1, count);
            await Assert.That(invalid.IsError).IsTrue();
        }
        await Assert.That(calls).IsEqualTo(0);
        using (var executed = await session.CommandAsync("EVALSHA", script.Sha1.ToUpperInvariant(), "0"))
            await Assert.That(executed.AsInteger()).IsEqualTo(42);
        using (var flushed = await session.CommandAsync("SCRIPT", "FLUSH", "SYNC"))
            await Assert.That(flushed.AsString()).IsEqualTo("OK");
        using (var missing = await session.CommandAsync("EVALSHA", script.Sha1, "0"))
            await Assert.That(missing.IsError).IsTrue();
        using (var executed = await session.CommandAsync("EVAL", script.Source, "0"))
            await Assert.That(executed.AsInteger()).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task HandlerSharesHashExpiryBinaryKeysAndWatchInvalidation(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        var script = RespireScript.Create("-- application compare and replace");
        server.RegisterScript(script, (data, keys, args) =>
        {
            using var current = data.Call("HGET", keys[0], "f");
            if (current.IsNull || current.AsString() != args[0].ToString()) return RespireFakeResponse.Integer(0);
            using var deleted = data.Call("DEL", keys[0]);
            using var stored = data.Call("HSET", keys[0], "v", args[1]);
            using var expiry = data.Call("PEXPIRE", keys[0], args[2]);
            return RespireFakeResponse.Integer(1);
        });
#pragma warning disable RESP010 // These clients belong to the isolated fake server.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
#pragma warning restore RESP010
        byte[] key = [0, 255, 128];
        byte[] value = [255, 0, 13, 10];
        using (var stored = await client.ExecuteAsync(RespireCommands.Hash.HSET, key, "f", "old")) { }
        await using var transaction = await client.CreateTransactionAsync([new RespireKey(key)]);
        transaction.Set("result", "must not execute");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [new RespireKey(key)], ["wrong", value, 1000])).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [new RespireKey(key)], ["old", value, 1000])).IsEqualTo(1);
        await Assert.That(await transaction.CommitAsync()).IsFalse();
        using (var read = await client.ExecuteAsync(RespireCommands.Hash.HGET, key, "v"))
            await Assert.That(read.AsBytes()).IsEquivalentTo(value);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await client.ExistsAsync(new RespireKey(key))).IsFalse();
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [new RespireKey(key)], ["", value, 1000])).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentHandlersAreAtomicAndContextCannotEscape()
    {
        await using var server = new RespireFakeServer();
        RespireFakeScriptContext? captured = null;
        var script = RespireScript.Create("-- increment twice");
        server.RegisterScript(script, (data, keys, _) =>
        {
            captured = data;
            using var first = data.Call("INCR", keys[0]);
            using var second = data.Call("INCR", keys[0]);
            return RespireFakeResponse.Integer(second.AsInteger());
        });
#pragma warning disable RESP010 // This client belongs to the isolated fake server.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 4 });
#pragma warning restore RESP010
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
            await client.Scripts.ExecuteIntegerAsync(script, ["counter"])));
        await Assert.That(results.Order()).IsEquivalentTo(Enumerable.Range(1, 40).Select(value => value * 2L));
        await Assert.That(() => captured!.Call("GET", "counter")).Throws<InvalidOperationException>();
        await Assert.That(await client.GetStringAsync("counter")).IsEqualTo("80");
    }

    [Test]
    public async Task CommandErrorsPreserveEarlierWritesAndRejectConnectionControl()
    {
        await using var server = new RespireFakeServer();
        var script = RespireScript.Create("-- partial effects on error");
        server.RegisterScript(script, (data, _, _) =>
        {
            using var written = data.Call("SET", "key", "value");
            using var wrongType = data.Call("HGET", "key", "field");
            return RespireFakeResponse.Integer(1);
        });
        var control = RespireScript.Create("-- forbidden command");
        server.RegisterScript(control, (data, _, _) =>
        {
            using var result = data.Call("MULTI");
            return RespireFakeResponse.Integer(1);
        });
#pragma warning disable RESP010 // This client belongs to the isolated fake server.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(script)).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(control)).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
    }

    [Test]
    public async Task RegisteredScriptRunsInsideExecWithNestedRepliesAndPrefix()
    {
        await using var server = new RespireFakeServer();
        var script = RespireScript.Create("-- return nested replies");
        server.RegisterScript(script, (data, keys, args) =>
        {
            using var stored = data.Call("SET", keys[0], args[0]);
            return RespireFakeResponse.Array(RespireFakeResponse.Null,
                RespireFakeResponse.Array(RespireFakeResponse.Bulk("done"), RespireFakeResponse.Integer(7)));
        });
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("EVAL", script.Source, "1", "key", "value"))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        using (var executed = await session.CommandAsync("EXEC"))
        {
            await Assert.That(executed.AsArray()[0].AsArray()[0].IsNull).IsTrue();
            await Assert.That(executed.AsArray()[0].AsArray()[1].AsArray()[1].AsInteger()).IsEqualTo(7);
        }
#pragma warning disable RESP010 // This client belongs to the isolated fake server.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
#pragma warning restore RESP010
        using var response = await client.WithKeyPrefix("test:").Scripts.ExecuteAsync(script, ["key"], ["prefixed"]);
        await Assert.That(response[0].IsNull).IsTrue();
        await Assert.That(response[1][0].AsString()).IsEqualTo("done");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("test:key")).IsEqualTo("prefixed");
    }

    [Test]
    public async Task RegistrationIsIsolatedAndRejectsDuplicatesAndDisposal()
    {
        var script = RespireScript.Create("return 1");
        await using var server = new RespireFakeServer();
        await using var other = new RespireFakeServer();
        server.RegisterScript(script, (_, _, _) => RespireFakeResponse.Integer(1));
        await Assert.That(() => server.RegisterScript(script, (_, _, _) => RespireFakeResponse.Integer(2)))
            .Throws<ArgumentException>();
        await using var session = await TestRespSession.ConnectAsync(other.CreateOptions());
        using (var result = await session.CommandAsync("EVAL", script.Source, "0"))
            await Assert.That(result.IsError).IsTrue();
        await server.DisposeAsync();
        await Assert.That(() => server.RegisterScript(script, (_, _, _) => RespireFakeResponse.Integer(1)))
            .Throws<ObjectDisposedException>();
    }
}
