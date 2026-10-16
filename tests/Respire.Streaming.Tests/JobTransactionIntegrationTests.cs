using System.Diagnostics;
using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobTransactionIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(false, 2, false)]
    [Arguments(true, 2, false)]
    [Arguments(false, 3, false)]
    [Arguments(true, 3, false)]
    [Arguments(false, 2, true)]
    [Arguments(true, 2, true)]
    [Arguments(false, 3, true)]
    [Arguments(true, 3, true)]
    public async Task CommitDefersEnqueueAndDeduplicatesAtExecution(bool scheduled, int protocol, bool decorated)
    {
        await using var root = await Connect(protocol);
        var client = root.WithKeyPrefix("tenant:");
        if (decorated) client = JobTransactionTests.Wrap(client);
        var queue = new RespireJobQueue<string>(client, "transaction", Encoding.UTF8.GetBytes);
        using var activity = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        await using var transaction = client.CreateTransaction();
        var saved = transaction.Set(queue.StreamKey + ":order", "paid");
        var pending = Enqueue(queue, transaction, scheduled);
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(() => pending.Result).Throws<RespirePendingNotReadyException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(queue.StreamKey + ":order")).IsNull();
        await transaction.CommitAsync();
        await Assert.That(saved.Result).IsTrue();
        await Assert.That(pending.Result.Enqueued).IsTrue();
        await Assert.That(await client.GetStringAsync(queue.StreamKey + ":order")).IsEqualTo("paid");
        var stream = queue.StreamKey + (scheduled ? ":scheduled" : "");
        var entries = await client.Streams.ReadAsync(stream);
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].Id).IsEqualTo(pending.Result.EntryId);
        await Assert.That(entries[0].GetString("_respire.job.payload")).IsEqualTo("payload");
        await Assert.That(entries[0].GetString("traceparent")).IsEqualTo(activity.Id);
        await Assert.That(await root.Streams.CountAsync(stream)).IsEqualTo(0);
        if (scheduled)
        {
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            var due = await client.SortedSets.ScoreAsync(queue.StreamKey + ":due", pending.Result.EntryId.Value);
            await Assert.That(due > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).IsTrue();
        }

        await using var retry = client.CreateTransaction();
        var duplicate = Enqueue(queue, retry, scheduled);
        await retry.CommitAsync();
        await Assert.That(duplicate.Result.Enqueued).IsFalse();
        await Assert.That(duplicate.Result.EntryId).IsEqualTo(pending.Result.EntryId);
        // Async and transactional producers share the deduplication namespace.
        await Assert.That((await queue.EnqueueAsync("job", "replacement")).Enqueued).IsFalse();
        await Assert.That(await client.Streams.CountAsync(stream)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscardCreatesNeitherJobNorDeduplicationRecord(bool scheduled)
    {
        await using var client = await Connect();
        var queue = new RespireJobQueue<string>(client, "discard", Encoding.UTF8.GetBytes);
        var transaction = client.CreateTransaction();
        var pending = Enqueue(queue, transaction, scheduled);
        await transaction.DisposeAsync();
        await Assert.That(() => pending.Result).Throws<RespireTransactionDiscardedException>();
        await AssertNoJob(client, queue);
        await Assert.That((await queue.EnqueueAsync("job", "retry")).Enqueued).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task WatchControlsBothBusinessWriteAndEnqueue(bool scheduled, bool conflict)
    {
        await using var client = await Connect();
        var queue = new RespireJobQueue<string>(client, "watch", Encoding.UTF8.GetBytes);
        var key = queue.StreamKey + ":order";
        await client.SetAsync(key, "before");
        await using var transaction = await client.CreateTransactionAsync(key);
        var saved = transaction.Set(key, "paid");
        var pending = Enqueue(queue, transaction, scheduled);
        if (conflict) await client.SetAsync(key, "changed");
        await Assert.That(await transaction.CommitAsync()).IsEqualTo(!conflict);
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo(conflict ? "changed" : "paid");
        if (conflict)
        {
            await Assert.That(saved.Status).IsEqualTo(RespirePendingStatus.Aborted);
            await Assert.That(() => pending.Result).Throws<RespireTransactionAbortedException>();
            await AssertNoJob(client, queue);
            await Assert.That((await queue.EnqueueAsync("job", "retry")).Enqueued).IsTrue();
        }
        else await Assert.That(pending.Result.Enqueued).IsTrue();
    }

    [Test]
    public async Task RuntimeScriptErrorDoesNotRollBackBusinessWrite()
    {
        await using var client = await Connect();
        var queue = new RespireJobQueue<string>(client, "error", Encoding.UTF8.GetBytes);
        await client.SetAsync(queue.StreamKey, "wrong type");
        await using var transaction = client.CreateTransaction();
        var saved = transaction.Set(queue.StreamKey + ":order", "paid");
        var pending = queue.Enqueue(transaction, "job", "payload");
        await transaction.CommitAsync();
        await Assert.That(saved.Result).IsTrue();
        await Assert.That(() => pending.Result).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync(queue.StreamKey + ":order")).IsEqualTo("paid");
        await client.DeleteAsync(queue.StreamKey);
        await Assert.That((await queue.EnqueueAsync("job", "retry")).Enqueued).IsTrue();
    }

    private ValueTask<RespireClient> Connect(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespirePending<RespireJobEnqueueResult> Enqueue(RespireJobQueue<string> queue,
        RespireTransactionBase transaction, bool scheduled)
        => scheduled ? queue.EnqueueScheduled(transaction, "job", "payload", new() { Delay = TimeSpan.FromHours(1), Priority = 7 })
            : queue.Enqueue(transaction, "job", "payload");

    private static async Task AssertNoJob(IRespireClient client, RespireJobQueue<string> queue)
    {
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":due")).IsEqualTo(0);
    }
}
