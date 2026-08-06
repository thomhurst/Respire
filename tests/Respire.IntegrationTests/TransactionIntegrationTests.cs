using FluentAssertions;
using Respire;
using Respire.Commands;
using Respire.FastClient;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestFixture>(Shared = SharedType.Keyed)]
[NotInParallel("redis-integration")]
public class TransactionIntegrationTests
{
    private readonly RedisTestFixture _fixture;
    private RespireClient _client = null!;

    public TransactionIntegrationTests(RedisTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.CreateAsync(_fixture.Host, _fixture.Port);
    }

    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Test]
    public async Task Transaction_ReturnsPerCommandResultsInOrder()
    {
        await _client.DelAsync("tx:key");
        await _client.DelAsync("tx:counter");

        var result = await _client.CreateTransaction()
            .Set("tx:key", "tx-value")
            .Incr("tx:counter")
            .Get("tx:key")
            .ExecuteAsync();

        result.Type.Should().Be(RespDataType.Array);
        var count = result.AsArray().Length;
        var setReply = result.AsArray()[0].AsString();
        var incrReply = result.AsArray()[1].AsInteger();
        var getReply = result.AsArray()[2].AsString();
        count.Should().Be(3);
        setReply.Should().Be("OK");
        incrReply.Should().Be(1);
        getReply.Should().Be("tx-value");
        result.Dispose();

        // Effects are visible after EXEC.
        var value = await _client.GetAsync("tx:key");
        value.AsString().Should().Be("tx-value");
        value.Dispose();
    }

    [Test]
    public async Task Transaction_QueueTimeError_AbortsWholeTransaction()
    {
        await _client.DelAsync("tx:abort");

        var transaction = _client.CreateTransaction()
            .Set("tx:abort", "should-not-persist")
            // INCR with no key: rejected at queue time, forcing EXECABORT.
            .Add(new RawCommand("*1\r\n$4\r\nINCR\r\n"u8.ToArray()));

        var act = async () => (await transaction.ExecuteAsync()).Dispose();
        (await act.Should().ThrowAsync<RespireServerException>())
            .Which.Message.Should().Contain("EXECABORT");

        // Nothing in the aborted transaction was applied, and the connection still works.
        var exists = await _client.ExistsAsync("tx:abort");
        exists.Should().BeFalse();
        (await _client.PingAsync()).Should().Be("PONG");
    }

    [Test]
    public async Task Transaction_ManyCommands_AllApplied()
    {
        var transaction = _client.CreateTransaction();
        for (var i = 0; i < 100; i++)
        {
            transaction.Set($"tx:bulk:{i}", $"value-{i}");
        }

        var result = await transaction.ExecuteAsync();
        var count = result.AsArray().Length;
        count.Should().Be(100);
        result.Dispose();

        var spot = await _client.GetAsync("tx:bulk:73");
        spot.AsString().Should().Be("value-73");
        spot.Dispose();
    }

    [Test]
    public async Task Transaction_ConcurrentWithRegularTraffic_StaysAtomic()
    {
        await _client.DelAsync("tx:concurrent:counter");

        // Regular commands hammer the same multiplexer while the transaction executes; the
        // atomic MULTI..EXEC append must keep them out of the transaction block.
        var traffic = Enumerable.Range(0, 200)
            .Select(i => _client.SetAsync($"tx:noise:{i}", "x").AsTask())
            .ToArray();

        var transaction = _client.CreateTransaction();
        for (var i = 0; i < 10; i++)
        {
            transaction.Incr("tx:concurrent:counter");
        }

        var result = await transaction.ExecuteAsync();
        await Task.WhenAll(traffic);

        // INCR replies inside the transaction must be strictly sequential 1..10 — proof that
        // no interleaved command executed between them.
        var replies = new long[10];
        for (var i = 0; i < 10; i++)
        {
            replies[i] = result.AsArray()[i].AsInteger();
        }

        replies.Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(i => (long)i),
            options => options.WithStrictOrdering());
        result.Dispose();
    }
}
