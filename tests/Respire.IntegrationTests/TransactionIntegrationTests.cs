using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TransactionIntegrationTests
{
    private readonly RedisTestContainer _fixture;
    private RespireClient _client = null!;

    public TransactionIntegrationTests(RedisTestContainer fixture)
    {
        _fixture = fixture;
    }

    [Before(HookType.Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(_fixture.ConnectionString);
    }

    [After(HookType.Test)]
    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    [Test]
    public async Task Transaction_ReturnsPerCommandResultsInOrder()
    {
        await _client.DeleteAsync("tx:key", "tx:counter");

        var transaction = _client.CreateTransaction();
        var setPending = transaction.SetAsync("tx:key", "tx-value");
        var incrPending = transaction.IncrementAsync("tx:counter");
        var getPending = transaction.GetStringAsync("tx:key");
        transaction.Count.Should().Be(3);

        // Queued results are unreadable until the transaction commits.
        var readEarly = () => getPending.Result;
        readEarly.Should().Throw<InvalidOperationException>();

        var committed = await transaction.CommitAsync();

        committed.Should().BeTrue();
        setPending.Result.Should().BeTrue();
        incrPending.Result.Should().Be(1);
        getPending.Result.Should().Be("tx-value");

        // Effects are visible after EXEC.
        (await _client.GetStringAsync("tx:key")).Should().Be("tx-value");
    }

    [Test]
    public async Task Transaction_RuntimeError_FaultsOnlyThatCommand()
    {
        await _client.DeleteAsync("tx:err:applied");
        await _client.SetAsync("tx:err:string", "not-a-number");

        var transaction = _client.CreateTransaction();
        var setPending = transaction.SetAsync("tx:err:applied", "persisted");
        // INCR on a non-numeric value queues fine but fails inside EXEC.
        var incrPending = transaction.IncrementAsync("tx:err:string");

        var committed = await transaction.CommitAsync();

        // EXEC ran: only the failing command's pending faults, the rest of the transaction applies.
        committed.Should().BeTrue();
        setPending.Result.Should().BeTrue();
        var readFaulted = () => incrPending.Result;
        readFaulted.Should().Throw<RespireServerException>()
            .Which.Code.Should().Be("ERR");

        // The other command in the transaction was applied, and the connection still works.
        (await _client.GetStringAsync("tx:err:applied")).Should().Be("persisted");
        (await _client.PingAsync()).Should().BePositive();
    }

    [Test]
    public async Task Transaction_ManyCommands_AllApplied()
    {
        var transaction = _client.CreateTransaction();
        var pendings = new RespirePending<bool>[100];
        for (var i = 0; i < 100; i++)
        {
            pendings[i] = transaction.SetAsync($"tx:bulk:{i}", $"value-{i}");
        }

        var committed = await transaction.CommitAsync();

        committed.Should().BeTrue();
        pendings.Should().OnlyContain(pending => pending.Result);

        (await _client.GetStringAsync("tx:bulk:73")).Should().Be("value-73");
    }

    [Test]
    public async Task Transaction_ConcurrentWithRegularTraffic_StaysAtomic()
    {
        await _client.DeleteAsync("tx:concurrent:counter");

        // Regular commands hammer the same multiplexer while the transaction executes; the
        // atomic MULTI..EXEC append must keep them out of the transaction block.
        var traffic = Enumerable.Range(0, 200)
            .Select(i => _client.SetAsync($"tx:noise:{i}", "x").AsTask())
            .ToArray();

        var transaction = _client.CreateTransaction();
        var pendings = new RespirePending<long>[10];
        for (var i = 0; i < 10; i++)
        {
            pendings[i] = transaction.IncrementAsync("tx:concurrent:counter");
        }

        var committed = await transaction.CommitAsync();
        await Task.WhenAll(traffic);

        committed.Should().BeTrue();

        // INCR replies inside the transaction must be strictly sequential 1..10 — proof that
        // no interleaved command executed between them.
        var replies = pendings.Select(pending => pending.Result).ToArray();
        replies.Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(i => (long)i),
            options => options.WithStrictOrdering());
    }

    [Test]
    public async Task WatchedTransaction_WatchedKeyModified_CommitReturnsFalse()
    {
        await _client.SetAsync("tx:watched", "initial");

        await using var transaction = await _client.CreateTransactionAsync(new RespireKey[] { "tx:watched" });
        var setPending = transaction.SetAsync("tx:watched", "from-transaction");

        // Another client writes the watched key between WATCH and EXEC, voiding the transaction.
        await using (var interloper = await RespireClient.ConnectAsync(_fixture.ConnectionString))
        {
            await interloper.SetAsync("tx:watched", "from-interloper");
        }

        var committed = await transaction.CommitAsync();

        committed.Should().BeFalse();
        var readAborted = () => setPending.Result;
        readAborted.Should().Throw<InvalidOperationException>();
        (await _client.GetStringAsync("tx:watched")).Should().Be("from-interloper");
    }
}
