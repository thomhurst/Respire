using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public partial class JobQueueIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentEnqueuesDeduplicateAndPreserveFirstPayload(int protocol)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var attempts = Enumerable.Range(0, 24)
            .Select(i => queue.EnqueueAsync("job-1", "payload-" + i).AsTask()).ToArray();
        var results = await Task.WhenAll(attempts);
        await Assert.That(results.Count(result => result.Enqueued)).IsEqualTo(1);
        await Assert.That(results.Select(result => result.EntryId).Distinct().Count()).IsEqualTo(1);
        var entries = await client.Streams.ReadAsync(queue.StreamKey);
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(entries[0].GetString("_respire.job.id")).IsEqualTo("job-1");
        var originalPayload = entries[0].GetString("_respire.job.payload");
        var retry = await queue.EnqueueAsync("job-1", "replacement");
        await Assert.That(retry.Enqueued).IsFalse();
        await Assert.That(retry.EntryId).IsEqualTo(entries[0].Id);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey))[0].GetString("_respire.job.payload"))
            .IsEqualTo(originalPayload);
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That((await Queue(client, "other").EnqueueAsync("job-1", "other")).Enqueued).IsTrue();
    }

    [Test]
    public async Task DeduplicationWindowExpiresAndDoesNotExpireTheJob()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var first = await queue.EnqueueAsync("expiring", "first", TimeSpan.FromMilliseconds(30));
        // Observe the server's expiry, without assuming a particular scheduling delay.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireJobEnqueueResult next;
        do
        {
            next = await queue.EnqueueAsync("expiring", "next", TimeSpan.FromMilliseconds(30), deadline.Token);
            if (!next.Enqueued) await Task.Delay(10, deadline.Token);
        } while (!next.Enqueued);
        await Assert.That(next.EntryId).IsNotEqualTo(first.EntryId);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
    }

    [Test]
    [Arguments(2, "set")]
    [Arguments(3, "set")]
    [Arguments(2, "xadd")]
    [Arguments(3, "xadd")]
    [Arguments(2, "xdel")]
    [Arguments(3, "xdel")]
    public async Task DeniedEnqueueWriteDoesNotAppendOrReserveTheJobId(int protocol, string deniedCommand)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol, AllowAdmin = true });
        var username = "job-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">job-test-password", "~*", "+@all", "-" + deniedCommand]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Protocol = (RespireProtocol)protocol, Username = username, Password = "job-test-password" });
            var queue = Queue(restricted);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.That(async () => await queue.EnqueueAsync("recoverable", "payload"))
                    .Throws<RespireServerException>();
                await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            }

            await admin.Server.AclSetUserAsync(username, ["+" + deniedCommand]);
            var first = await queue.EnqueueAsync("recoverable", "payload");
            var retry = await queue.EnqueueAsync("recoverable", "replacement");
            await Assert.That(first.Enqueued).IsTrue();
            await Assert.That(retry.Enqueued).IsFalse();
            await Assert.That(retry.EntryId).IsEqualTo(first.EntryId);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        }
        finally { using var deleted = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task FailedDeduplicationWriteRollsBackOnlyTheNewEntry(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var earlier = await queue.EnqueueAsync("earlier", "keep me");
        // Inject an invalid expiry directly into the production script: the public API rejects it,
        // but Redis accepts XADD before SET fails, exercising runtime rollback after ACL preflight.
        var script = (RespireScript)typeof(RespireJobQueue<string>)
            .GetField("Enqueue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(null)!;
        var deduplicationKey = queue.StreamKey + ":dedup:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("recoverable")));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.That(async () =>
            {
                using var result = await client.Scripts.ExecuteAsync(script,
                    [queue.StreamKey, deduplicationKey], ["recoverable", "payload", 0]);
            }).Throws<RespireServerException>();
            var retained = await client.Streams.ReadAsync(queue.StreamKey);
            await Assert.That(retained.Length).IsEqualTo(1);
            await Assert.That(retained[0].Id).IsEqualTo(earlier.EntryId);
        }

        var first = await queue.EnqueueAsync("recoverable", "payload");
        var retry = await queue.EnqueueAsync("recoverable", "replacement");
        await Assert.That(first.Enqueued).IsTrue();
        await Assert.That(retry.Enqueued).IsFalse();
        await Assert.That(retry.EntryId).IsEqualTo(first.EntryId);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
    }

    [Test]
    public async Task FailedAppendDoesNotReserveTheJobId()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await client.Strings.SetAsync(queue.StreamKey, "wrong type");
        await Assert.That(async () => await queue.EnqueueAsync("recoverable", "payload")).Throws<RespireServerException>();
        await client.Keys.DeleteAsync(queue.StreamKey);
        await Assert.That((await queue.EnqueueAsync("recoverable", "payload")).Enqueued).IsTrue();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AbandonedConsumerDeliveryIsRecoveredByTypedWorker(int protocol)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("{tenant}:");
        var queue = Queue(client);
        var enqueued = await queue.EnqueueAsync("crashed-job", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        // Fault injection: the first client reads the job and disappears before acknowledging it.
        await using (var crashed = await ConnectAsync(protocol))
        {
            var delivery = await crashed.WithKeyPrefix("{tenant}:").Streams.ReadGroupOnceAsync(
                queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "crashed", new() { Count = 1 });
            await Assert.That(delivery.Single().Id).IsEqualTo(enqueued.EntryId);
        }
        var state = new HandlerState();
        await using var provider = Worker(client, state);
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            var recovered = await state.Received.Task.WaitAsync(deadline.Token);
            await Assert.That(recovered.Id).IsEqualTo("crashed-job");
            await Assert.That(recovered.Payload).IsEqualTo("payload");
            await Assert.That(recovered.EntryId).IsEqualTo(enqueued.EntryId);
            var envelope = new RespireJob<string>(recovered.Id, recovered.Payload, recovered.EntryId);
            await Assert.That(recovered.Equals(envelope)).IsTrue();
            await Assert.That(recovered.GetHashCode()).IsEqualTo(envelope.GetHashCode());
            await UntilAsync(async () => (await client.Streams.PendingSummaryAsync(
                queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count == 0, deadline.Token);
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That((await queue.EnqueueAsync("crashed-job", "duplicate")).Enqueued).IsFalse();
            var completion = (await client.Streams.ReadAsync(queue.EventsStreamKey)).Single();
            await Assert.That(completion.GetString("kind")).IsEqualTo("completed");
            await Assert.That(completion.GetString("attempt")).IsEqualTo("2");
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(RespireStreamWorkerResult.Nack)]
    [Arguments(RespireStreamWorkerResult.DeadLetter)]
    public async Task RetryLimitAndExplicitRejectPreserveEnvelopeInDeadLetter(RespireStreamWorkerResult result)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var enqueued = await queue.EnqueueAsync("poison", "payload");
        var state = new HandlerState { Result = result };
        await using var provider = Worker(client, state);
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await UntilAsync(async () => await client.Streams.CountAsync(queue.DeadLetterStreamKey) == 1, deadline.Token);
            var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
            await Assert.That(dead.GetString("_respire.job.id")).IsEqualTo("poison");
            await Assert.That(dead.GetString("_respire.job.payload")).IsEqualTo("payload");
            await Assert.That(dead.GetString("_respire.source_id")).IsEqualTo(enqueued.EntryId.Value);
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(0);
            await Assert.That(state.Attempts).IsEqualTo(result == RespireStreamWorkerResult.Nack ? 2 : 1);
            var completion = (await client.Streams.ReadAsync(queue.EventsStreamKey)).Single();
            await Assert.That(completion.GetString("kind")).IsEqualTo("dead-letter");
            await Assert.That(completion.GetString("reason")).IsEqualTo(result == RespireStreamWorkerResult.Nack ? "nack" : "explicit");
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(2, RespireStreamWorkerResult.Ack)]
    [Arguments(3, RespireStreamWorkerResult.Ack)]
    [Arguments(2, RespireStreamWorkerResult.DeadLetter)]
    [Arguments(3, RespireStreamWorkerResult.DeadLetter)]
    public async Task StaleHandlerCannotAcknowledgeTheRecoveredAttempt(int protocol, RespireStreamWorkerResult staleResult)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.EnqueueAsync("slow", "payload");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new HandlerState { OnHandle = async token =>
        {
            await releaseFirst.Task.WaitAsync(token);
            return staleResult;
        } };
        var second = new HandlerState { OnHandle = async token =>
        {
            await releaseSecond.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        } };
        await using var firstProvider = Worker(client, first);
        await using var secondProvider = Worker(client, second);
        var firstWorker = firstProvider.GetServices<IHostedService>().Single();
        var secondWorker = secondProvider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await firstWorker.StartAsync(deadline.Token);
        try
        {
            var firstJob = await first.Received.Task.WaitAsync(deadline.Token);
            await Assert.That(await firstJob.ReportProgressAsync(10, deadline.Token)).IsTrue();
            // Deliberately reuse the consumer identity: attempt fencing must still protect recovery.
            await secondWorker.StartAsync(deadline.Token);
            var secondJob = await second.Received.Task.WaitAsync(deadline.Token);
            await Assert.That(await firstJob.ReportProgressAsync(90, deadline.Token)).IsFalse();
            await Assert.That(await secondJob.ReportProgressAsync(50, deadline.Token)).IsTrue();
            releaseFirst.TrySetResult();
            await firstWorker.StopAsync(deadline.Token);
            var pending = await client.Streams.PendingAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup);
            await Assert.That(pending.Length).IsEqualTo(1);
            await Assert.That(pending[0].DeliveryCount).IsEqualTo(2);
            await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(2);
            releaseSecond.TrySetResult();
            await secondWorker.StopAsync(deadline.Token);
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(0);
            await Assert.That(await secondJob.ReportProgressAsync(100, deadline.Token)).IsFalse();
            var events = await client.Streams.ReadAsync(queue.EventsStreamKey);
            await Assert.That(events.Select(entry => entry.GetString("kind")))
                .IsEquivalentTo(new string?[] { "progress", "progress", "completed" });
            await Assert.That(events[^1].GetString("attempt")).IsEqualTo("2");
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
            await using var reader = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Kind).IsEqualTo("progress");
            await Assert.That(reader.Current.Percent).IsEqualTo(10);
            await Assert.That(reader.Current.Attempt).IsEqualTo(1);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await firstWorker.StopAsync(deadline.Token);
            await secondWorker.StopAsync(deadline.Token);
        }
    }
    private ValueTask<RespireClient> ConnectAsync(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });

    private static RespireJobQueue<string> Queue(IRespireClient client, string name = "test")
        => new(client, name, Encoding.UTF8.GetBytes);

    private static ServiceProvider Worker(IRespireClient client, HandlerState state)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, new()
        {
            ConsumerName = "recovery", MinimumIdleTime = TimeSpan.FromMilliseconds(50),
            RecoveryPollInterval = TimeSpan.FromMilliseconds(20), ReadWait = TimeSpan.FromMilliseconds(20),
            DeliveryLimit = 2,
        });
        return services.BuildServiceProvider();
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, CancellationToken token)
    {
        while (!await predicate()) await Task.Delay(10, token);
    }

    public sealed class HandlerState
    {
        public TaskCompletionSource<RespireJob<string>> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RespireStreamWorkerResult Result { get; init; } = RespireStreamWorkerResult.Ack;
        public int Attempts;
        public Func<CancellationToken, ValueTask<RespireStreamWorkerResult>>? OnHandle { get; init; }
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref state.Attempts);
            state.Received.TrySetResult(job);
            return state.OnHandle?.Invoke(cancellationToken) ?? ValueTask.FromResult(state.Result);
        }
    }
}
