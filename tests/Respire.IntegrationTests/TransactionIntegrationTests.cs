using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TransactionIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task ReadOnlyRetryCallbackValidatesWatch(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        var key = $"tx:readonly:{Guid.NewGuid():N}";
        await client.SetAsync(key, "before");
        var attempts = 0;
        var result = await client.RunTransactionAsync([key], async (_, token) =>
        {
            var value = await client.GetStringAsync(key, token);
            if (++attempts == 1) await client.SetAsync(key, "after", cancellationToken: token);
            return value;
        });
        result.Should().Be("after");
        attempts.Should().Be(2);
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task Transaction_ReturnsPerCommandResultsInOrder(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.DeleteAsync("tx:key", "tx:counter");

        var transaction = client.CreateTransaction();
        var setPending = transaction.Set("tx:key", "tx-value");
        var incrPending = transaction.Increment("tx:counter");
        var getPending = transaction.GetString("tx:key");
        transaction.Count.Should().Be(3);

        // Queued results are unreadable until the transaction commits.
        var readEarly = () => getPending.Result;
        readEarly.Should().Throw<RespirePendingNotReadyException>();

        await transaction.CommitAsync();

        setPending.Result.Should().BeTrue();
        incrPending.Result.Should().Be(1);
        getPending.Result.Should().Be("tx-value");

        // Effects are visible after EXEC.
        (await client.GetStringAsync("tx:key")).Should().Be("tx-value");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task Transaction_DisposeWithoutCommit_FaultsQueuedPendings(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.DeleteAsync("tx:discarded");
        var transaction = client.CreateTransaction();
        var setPending = transaction.Set("tx:discarded", "value");
        var getPending = transaction.GetString("tx:discarded");

        await transaction.DisposeAsync();

        setPending.Status.Should().Be(RespirePendingStatus.Faulted);
        getPending.Status.Should().Be(RespirePendingStatus.Faulted);
        setPending.Error.Should().BeOfType<RespireTransactionDiscardedException>();
        getPending.Error.Should().BeOfType<RespireTransactionDiscardedException>();
        var readDiscarded = () => getPending.Result;
        readDiscarded.Should().ThrowExactly<RespireTransactionDiscardedException>();
        (await client.ExistsAsync("tx:discarded")).Should().BeFalse();
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task Transaction_RuntimeError_FaultsOnlyThatCommand(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.DeleteAsync("tx:err:applied");
        await client.SetAsync("tx:err:string", "not-a-number");

        var transaction = client.CreateTransaction();
        var setPending = transaction.Set("tx:err:applied", "persisted");
        // INCR on a non-numeric value queues fine but fails inside EXEC.
        var incrPending = transaction.Increment("tx:err:string");

        await transaction.CommitAsync();

        // EXEC ran: only the failing command's pending faults, the rest of the transaction applies.
        setPending.Result.Should().BeTrue();
        var readFaulted = () => incrPending.Result;
        readFaulted.Should().Throw<RespireServerException>()
            .Which.Code.Should().Be("ERR");

        // The other command in the transaction was applied, and the connection still works.
        (await client.GetStringAsync("tx:err:applied")).Should().Be("persisted");
        (await client.PingAsync()).Should().BePositive();
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task Transaction_ManyCommands_AllApplied(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        var transaction = client.CreateTransaction();
        var pendings = new RespirePending<bool>[100];
        for (var i = 0; i < 100; i++)
        {
            pendings[i] = transaction.Set($"tx:bulk:{i}", $"value-{i}");
        }

        await transaction.CommitAsync();

        pendings.Should().OnlyContain(pending => pending.Result);

        (await client.GetStringAsync("tx:bulk:73")).Should().Be("value-73");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task Transaction_ConcurrentWithRegularTraffic_StaysAtomic(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.DeleteAsync("tx:concurrent:counter");

        await using var competitor = await RespireClient.ConnectAsync(options);
        // Another connection increments the same key. Transaction results must be consecutive
        // even when unrelated increments execute before or after the entire EXEC.
        var traffic = Enumerable.Range(0, 200)
            .Select(_ => competitor.IncrementAsync("tx:concurrent:counter").AsTask())
            .ToArray();

        var transaction = client.CreateTransaction();
        var pendings = new RespirePending<long>[10];
        for (var i = 0; i < 10; i++)
        {
            pendings[i] = transaction.Increment("tx:concurrent:counter");
        }

        await transaction.CommitAsync();
        await Task.WhenAll(traffic);

        var replies = pendings.Select(pending => pending.Result).ToArray();
        replies.Should().BeEquivalentTo(Enumerable.Range(0, 10).Select(i => replies[0] + i),
            options => options.WithStrictOrdering());
        (await client.GetStringAsync("tx:concurrent:counter")).Should().Be("210");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WatchedTransaction_WatchedKeyModified_CommitReturnsFalse(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.SetAsync("tx:watched", "initial");

        await using var transaction = await client.CreateTransactionAsync(new RespireKey[] { "tx:watched" });
        var setPending = transaction.Set("tx:watched", "from-transaction");

        // Another client writes the watched key between WATCH and EXEC, voiding the transaction.
        await using (var interloper = await RespireClient.ConnectAsync(options))
        {
            await interloper.SetAsync("tx:watched", "from-interloper");
        }

        var committed = await transaction.CommitAsync();

        committed.Should().BeFalse();
        var readAborted = () => setPending.Result;
        readAborted.Should().Throw<RespireTransactionAbortedException>();
        (await client.GetStringAsync("tx:watched")).Should().Be("from-interloper");
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task WatchedTransaction_WatchedKeyUnchanged_CommitsAndReturnsResults(bool useFake, int protocol)
    {
        await using var server = useFake ? new RespireFakeServer() : null;
        var options = (server?.CreateOptions() ?? RespireOptions.Parse(fixture.ConnectionString))
            with { Protocol = (RespProtocol)protocol };
        await using var client = await RespireClient.ConnectAsync(options);
        await client.SetAsync("tx:watched:success", "initial");

        await using var transaction = await client.CreateTransactionAsync(
            new RespireKey[] { "tx:watched:success" });
        var setPending = transaction.Set("tx:watched:success", "committed");
        var getPending = transaction.GetString("tx:watched:success");

        var committed = await transaction.CommitAsync();

        committed.Should().BeTrue();
        setPending.Result.Should().BeTrue();
        getPending.Result.Should().Be("committed");
        (await client.GetStringAsync("tx:watched:success")).Should().Be("committed");
    }
}
