using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DeferredRawIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task RawCommandsPreserveOrderPrefixesBinaryAndAggregateReplies(int protocol, bool transactional)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("raw:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        byte[] key = [255, 0, 13, 10];
        byte[] value = [0, 128, 255];
        var stored = queue.Execute(RespireCommands.String.SET, key, value);
        var read = queue.Execute("GET", key);
        var missing = queue.Execute("GET", "missing");
        var hash = queue.Execute("HSET", "hash", "field", "value");
        var aggregate = queue.Execute("HGETALL", "hash");
        var script = queue.Execute("EVAL", "return {KEYS[1],ARGV[1],redis.error_reply('nested')}", 1, "key", "argument");
        var keylessScript = queue.Execute("EVAL", "return ARGV[1]", 0, "argument-only");
        var ping = queue.Execute("PING");
        var echo = queue.Execute("ECHO", "key");
        Array.Fill(key, (byte)'x');
        Array.Fill(value, (byte)'x');
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        using var storedResult = stored.Result;
        using var readResult = read.Result;
        using var missingResult = missing.Result;
        using var hashResult = hash.Result;
        using var aggregateResult = aggregate.Result;
        using var scriptResult = script.Result;
        using var keylessScriptResult = keylessScript.Result;
        using var pingResult = ping.Result;
        using var echoResult = echo.Result;
        storedResult.AsString().Should().Be("OK");
        readResult.AsBytes().Should().Equal(0, 128, 255);
        missingResult.IsNull.Should().BeTrue();
        hashResult.AsInteger().Should().Be(1);
        aggregateResult.Count.Should().Be(2);
        aggregateResult[0].AsString().Should().Be("field");
        aggregateResult[1].AsString().Should().Be("value");
        scriptResult[0].AsString().Should().Be("raw:key");
        scriptResult[1].AsString().Should().Be("argument");
        scriptResult[2].IsError.Should().BeTrue();
        keylessScriptResult.AsString().Should().Be("argument-only");
        pingResult.AsString().Should().Be("PONG");
        echoResult.AsString().Should().Be("key");
        (await client.Keys.ExistsAsync("hash")).Should().BeFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WatchedTransactionsExposeTheSameRawQueue(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix("watched:");
        await using var transaction = await view.CreateTransactionAsync("key");
        var pending = transaction.Execute("SET", "key", "value");
        (await transaction.CommitAsync()).Should().BeTrue();
        using var result = pending.Result;
        result.AsString().Should().Be("OK");
        (await view.GetStringAsync("key")).Should().Be("value");
    }
}
