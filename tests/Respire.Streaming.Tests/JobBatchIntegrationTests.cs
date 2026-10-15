using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class JobQueueIntegrationTests
{
    [Test]
    [Arguments(2, "tenant:")]
    [Arguments(3, "{tenant}:")]
    public async Task BatchMembershipDeduplicatesWithinBatchAndPreservesOriginalCallbacks(int protocol, string prefix)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix(prefix);
        var queue = Queue(client);
        await Assert.That(await queue.GetBatchStatusAsync("missing")).IsNull();
        await Assert.That(await queue.RemoveBatchAsync("missing")).IsFalse();
        await Assert.That(await queue.CreateBatchAsync(BatchCallbacks(), "batch")).IsEqualTo("batch");
        await queue.CreateBatchAsync(new() { OnSuccess = new("replacement", "replacement") }, "batch");
        var adds = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(i => queue.AddBatchJobAsync("batch", "member", "payload-" + i).AsTask()));
        await Assert.That(adds.Count(value => value)).IsEqualTo(1);
        var original = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(original.GetString("_respire.job.batch")).IsNotNull();
        await Assert.That(original.GetString("_respire.job.member")).IsNotNull();
        await Assert.That(original.GetString("_respire.job.batch_callback")).IsNull();
        await queue.SealBatchAsync("batch");
        await Assert.That(await queue.AddBatchJobAsync("batch", "member", "replacement")).IsFalse();
        await Assert.That(async () => await queue.AddBatchJobAsync("batch", "new", "payload"))
            .Throws<RespireServerException>();
        await Assert.That(async () => await queue.RemoveBatchAsync("batch")).Throws<RespireServerException>();
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(client, queue)).Single();
        await Assert.That(await CompleteBatchJob(client, queue, member)).IsEqualTo(1);
        var callbacks = await ReadBatchJobs(client, queue);
        await Assert.That(callbacks.Select(entry => entry.GetString("_respire.job.id")))
            .IsEquivalentTo(new string?[] { "success", "complete" });
        foreach (var callback in callbacks)
        {
            await Assert.That(callback.GetString("_respire.job.batch_callback")).IsNotNull();
            await Assert.That(callback.GetString("_respire.job.batch")).IsNull();
            await Assert.That(callback.GetString("_respire.job.member")).IsNull();
        }
        await Assert.That(await queue.AddBatchJobAsync("batch", "member", "replacement")).IsFalse();
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey))[0].GetString("_respire.job.payload"))
            .IsEqualTo(original.GetString("_respire.job.payload"));
        // Ordinary enqueue and another batch have independent ID reservations.
        await Assert.That((await queue.EnqueueAsync("member", "ordinary")).Enqueued).IsTrue();
        await queue.CreateBatchAsync(batchId: "other");
        await Assert.That(await queue.AddBatchJobAsync("other", "member", "other")).IsTrue();
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await queue.RemoveBatchAsync("batch")).IsTrue();
        await Assert.That(await queue.RemoveBatchAsync("batch")).IsFalse();
        await queue.CreateBatchAsync(batchId: "batch");
        await Assert.That(await queue.AddBatchJobAsync("batch", "member", "new run")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentBatchFinalCompletionsAndRedeliveryPublishCallbacksOnce(bool fail)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        for (var i = 0; i < 16; i++) await queue.AddBatchJobAsync("batch", "member-" + i, "payload");
        await queue.SealBatchAsync("batch");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var members = await ReadBatchJobs(client, queue);
        await Assert.That(members.Length).IsEqualTo(16);
        var completions = await Task.WhenAll(members.Select((entry, i) =>
            CompleteBatchJob(client, queue, entry, fail && i == 0 ? "dead-letter" : "completed").AsTask()));
        await Assert.That(completions.All(result => result == 1)).IsTrue();
        foreach (var entry in members)
            await Assert.That(await CompleteBatchJob(client, queue, entry)).IsEqualTo(0);
        await queue.SealBatchAsync("batch");
        // Cancelling an already completed batch preserves its successful/failed outcome.
        await queue.CancelBatchAsync("batch");
        var status = (await queue.GetBatchStatusAsync("batch"))!;
        await Assert.That(status.Pending).IsEqualTo(0);
        await Assert.That(status.Succeeded).IsEqualTo(fail ? 15 : 16);
        await Assert.That(status.Failed).IsEqualTo(fail ? 1 : 0);
        await Assert.That(status.Cancelled).IsEqualTo(0);
        await Assert.That(status.CancellationRequested).IsFalse();
        await Assert.That(status.IsCompleted).IsTrue();
        await Assert.That((await ReadBatchJobs(client, queue)).Select(entry => entry.GetString("_respire.job.id")))
            .IsEquivalentTo(new string?[] { fail ? "failure" : "success", "complete" });
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(16);
        await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(fail ? 1 : 0);
    }

    [Test]
    public async Task BatchMembersCanFinishBeforeSealAndMembershipCanStillGrow()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        await queue.AddBatchJobAsync("batch", "first", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await CompleteBatchJob(client, queue, (await ReadBatchJobs(client, queue)).Single());
        var open = (await queue.GetBatchStatusAsync("batch"))!;
        await Assert.That(open.Pending).IsEqualTo(0);
        await Assert.That(open.Succeeded).IsEqualTo(1);
        await Assert.That(open.IsSealed).IsFalse();
        await Assert.That(open.IsCompleted).IsFalse();
        await Assert.That((await ReadBatchJobs(client, queue)).Length).IsEqualTo(0);
        await queue.AddBatchJobAsync("batch", "second", "payload");
        await CompleteBatchJob(client, queue, (await ReadBatchJobs(client, queue)).Single());
        await queue.SealBatchAsync("batch");
        await Assert.That((await queue.GetBatchStatusAsync("batch"))!.Succeeded).IsEqualTo(2);
        await Assert.That((await ReadBatchJobs(client, queue)).Select(entry => entry.GetString("_respire.job.id")))
            .IsEquivalentTo(new string?[] { "success", "complete" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyBatchFinalizesOnceWhenSealedOrCancelled(bool cancel)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        for (var i = 0; i < 2; i++)
        {
            if (cancel) await queue.CancelBatchAsync("batch");
            else await queue.SealBatchAsync("batch");
        }
        var status = (await queue.GetBatchStatusAsync("batch"))!;
        await Assert.That(status.IsCompleted).IsTrue();
        await Assert.That(status.IsSealed).IsTrue();
        await Assert.That(status.CancellationRequested).IsEqualTo(cancel);
        await Assert.That(status.Pending + status.Succeeded + status.Failed + status.Cancelled).IsEqualTo(0);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Select(entry => entry.GetString("_respire.job.id")))
            .IsEquivalentTo(new string?[] { cancel ? "failure" : "success", "complete" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchRetryIsPendingUntilEventualTerminalOutcomeAndCallbacksAreOrdinaryJobs(bool fail)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        await queue.AddBatchJobAsync("batch", "member", "payload");
        await queue.SealBatchAsync("batch");
        var state = new BatchHandlerState { Retry = true, Fail = fail, HoldRetry = true };
        await using var provider = BatchWorker(client, state);
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await state.RetryEntered.Task.WaitAsync(deadline.Token);
            var retrying = (await queue.GetBatchStatusAsync("batch"))!;
            await Assert.That(retrying.Pending).IsEqualTo(1);
            await Assert.That(retrying.Succeeded + retrying.Failed).IsEqualTo(0);
            await Assert.That(retrying.IsCompleted).IsFalse();
            state.Release.TrySetResult();
            await UntilAsync(async () => (await client.Streams.ReadAsync(queue.EventsStreamKey))
                .Count(entry => entry.GetString("kind") == "completed" && entry.GetString("job_id") == "complete") == 1,
                deadline.Token);
            var status = (await queue.GetBatchStatusAsync("batch"))!;
            await Assert.That(status.Pending).IsEqualTo(0);
            await Assert.That(status.Succeeded).IsEqualTo(fail ? 0 : 1);
            await Assert.That(status.Failed).IsEqualTo(fail ? 1 : 0);
            await Assert.That(status.IsCompleted).IsTrue();
            await Assert.That(state.Members).IsEqualTo(fail ? 3 : 2);
            await Assert.That(state.Calls.Keys).IsEquivalentTo(new[] { "member", fail ? "failure" : "success", "complete" });
            await Assert.That(state.Calls["complete"]).IsEqualTo(1);
            if (fail)
            {
                var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
                await Assert.That(dead.GetString("_respire.reason")).IsEqualTo("nack");
            }
        }
        finally { state.Release.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchCancellationSkipsQueuedMembersAndCountsInFlightCompletionAsCancelled(bool inFlight)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        await queue.AddBatchJobAsync("batch", "member", "payload");
        await queue.AddBatchJobAsync("batch", "queued", "payload");
        var batchKey = (await client.Streams.ReadAsync(queue.StreamKey))[0].GetString("_respire.job.batch");
        var state = new BatchHandlerState { HoldFirst = inFlight };
        await using var provider = BatchWorker(client, state);
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (!inFlight) await queue.CancelBatchAsync("batch");
        await worker.StartAsync(deadline.Token);
        try
        {
            if (inFlight)
            {
                await state.FirstEntered.Task.WaitAsync(deadline.Token);
                await queue.CancelBatchAsync("batch");
                await Assert.That((await queue.GetBatchStatusAsync("batch"))!.Pending).IsEqualTo(2);
                state.Release.TrySetResult();
            }
            await UntilAsync(async () => (await client.Streams.ReadAsync(queue.EventsStreamKey))
                .Any(entry => entry.GetString("kind") == "completed" && entry.GetString("job_id") == "complete"), deadline.Token);
            var status = (await queue.GetBatchStatusAsync("batch"))!;
            await Assert.That(status.Pending).IsEqualTo(0);
            await Assert.That(status.Cancelled).IsEqualTo(2);
            await Assert.That(status.Succeeded + status.Failed).IsEqualTo(0);
            await Assert.That(status.IsSealed && status.IsCompleted && status.CancellationRequested).IsTrue();
            await Assert.That(state.Calls.ContainsKey("queued")).IsFalse();
            await Assert.That(state.Calls.ContainsKey("member")).IsEqualTo(inFlight);
            await Assert.That(state.Calls["failure"]).IsEqualTo(1);
            await Assert.That(state.Calls["complete"]).IsEqualTo(1);
            await Assert.That(state.Calls.ContainsKey("success")).IsFalse();
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
            await using var events = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
            for (var i = 0; i < 4; i++)
            {
                await Assert.That(await events.MoveNextAsync()).IsTrue();
                var terminal = events.Current;
                var member = terminal.JobId is "member" or "queued";
                await Assert.That(terminal.Kind).IsEqualTo(member ? "cancelled" : "completed");
                await Assert.That(terminal.BatchKey).IsEqualTo(member ? batchKey : null);
                await Assert.That(terminal.FlowKey).IsNull();
                await Assert.That(terminal.Reason).IsNull();
            }
        }
        finally { state.Release.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task BatchCancellationCheckFencesConsumerAndAttemptBeforeReadingRetainedState()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(batchId: "batch");
        await queue.AddBatchJobAsync("batch", "member", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(client, queue)).Single();
        var script = (RespireScript)typeof(RespireJobQueue<string>).Assembly
            .GetType("Respire.Streaming.JobQueueBatches")!.GetField("CheckCancellation",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        RespireKey[] keys = [queue.StreamKey, member.GetString("_respire.job.batch")!];
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "other", member.Id.Value, 1])).IsEqualTo(-1);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 1])).IsEqualTo(0);
        using (await client.ExecuteAsync("XCLAIM", [queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "batch-consumer", 0, member.Id.Value])) { }
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 1])).IsEqualTo(-1);
        await queue.CancelBatchAsync("batch");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 1])).IsEqualTo(-1);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 2])).IsEqualTo(1);
        await Assert.That(await CompleteBatchJob(client, queue, member, attempt: 1)).IsEqualTo(0);
        await Assert.That(await CompleteBatchJob(client, queue, member, attempt: 2)).IsEqualTo(2);
        await Assert.That((await queue.GetBatchStatusAsync("batch"))!.Cancelled).IsEqualTo(1);
        await queue.RemoveBatchAsync("batch");
        // A stale buffered delivery must not fail by reading already-removed batch state.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys,
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 2])).IsEqualTo(-1);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, true)]
    public async Task BatchCompletionFencesStaleAttemptsAndSurvivesLostReplies(int protocol, bool delete)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        await queue.AddBatchJobAsync("batch", "member", "payload");
        await queue.SealBatchAsync("batch");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(client, queue)).Single();
        using (await client.ExecuteAsync("XCLAIM", [queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "batch-consumer", 0, member.Id.Value])) { }
        await Assert.That(await CompleteBatchJob(client, queue, member, delete: delete)).IsEqualTo(0);
        await Assert.That((await queue.GetBatchStatusAsync("batch"))!.Pending).IsEqualTo(1);
        await using (var recovered = await ConnectAsync(protocol))
            await Assert.That(await CompleteBatchJob(recovered, queue, member, attempt: 2, delete: delete)).IsEqualTo(1);
        await Assert.That(await CompleteBatchJob(client, queue, member, attempt: 2, delete: delete)).IsEqualTo(0);
        await Assert.That((await queue.GetBatchStatusAsync("batch"))!.Succeeded).IsEqualTo(1);
        await Assert.That((await ReadBatchJobs(client, queue)).Length).IsEqualTo(2);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(1);
    }

    [Test]
    [Arguments("set", false)]
    [Arguments("xadd", false)]
    [Arguments("xdel", false)]
    [Arguments("xack", false)]
    [Arguments("xackdel", true)]
    public async Task DeniedBatchFinalizationRollsBackCountsCallbacksAndEvents(string denied, bool delete)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        if (delete)
        {
            var info = await admin.Server.InfoAsync("server");
            var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
            if (Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]) < new Version(8, 2)) return;
        }
        var queue = Queue(admin);
        await queue.CreateBatchAsync(BatchCallbacks(), "batch");
        await queue.AddBatchJobAsync("batch", "member", "payload");
        await queue.SealBatchAsync("batch");
        await admin.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(admin, queue)).Single();
        var username = "batch-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">batch-password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = username, Password = "batch-password" });
            await Assert.That(async () => await CompleteBatchJob(restricted, queue, member, delete: delete))
                .Throws<RespireServerException>();
            var status = (await queue.GetBatchStatusAsync("batch"))!;
            await Assert.That(status.Pending).IsEqualTo(1);
            await Assert.That(status.Succeeded).IsEqualTo(0);
            await Assert.That(status.IsCompleted).IsFalse();
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(1);
            await Assert.That(await CompleteBatchJob(admin, queue, member, delete: delete)).IsEqualTo(1);
            await Assert.That((await ReadBatchJobs(admin, queue)).Length).IsEqualTo(2);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    public async Task BatchSizeLimitsIncludeCallbacksAndRejectExcessWithoutMutation()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(batchId: "count");
        for (var i = 0; i < 256; i++) await queue.AddBatchJobAsync("count", i.ToString(), "");
        await Assert.That(async () => await queue.AddBatchJobAsync("count", "overflow", ""))
            .Throws<RespireServerException>();
        await Assert.That(await queue.AddBatchJobAsync("count", "0", "replacement")).IsFalse();
        await Assert.That((await queue.GetBatchStatusAsync("count"))!.Pending).IsEqualTo(256);
        // UTF-8 callback ID+payload take three bytes; the member consumes the exact remainder.
        await queue.CreateBatchAsync(new() { OnComplete = new("c", "é") }, "bytes");
        await queue.AddBatchJobAsync("bytes", "m", new string('x', 262140));
        await Assert.That(async () => await queue.AddBatchJobAsync("bytes", "n", ""))
            .Throws<RespireServerException>();
        await Assert.That((await queue.GetBatchStatusAsync("bytes"))!.Pending).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(257);
    }

    private static RespireJobBatchOptions<string> BatchCallbacks() => new()
    {
        OnSuccess = new("success", "success payload"),
        OnFailure = new("failure", "failure payload"),
        OnComplete = new("complete", "complete payload"),
    };

    private static ValueTask<RespireStreamEntry[]> ReadBatchJobs(IRespireClient client, RespireJobQueue<string> queue)
        => client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup,
            "batch-consumer", new() { Count = 64 });

    private static ValueTask<long> CompleteBatchJob(IRespireClient client, RespireJobQueue<string> queue,
        RespireStreamEntry entry, string outcome = "completed", int attempt = 1, bool delete = false)
        => client.Scripts.ExecuteIntegerAsync(JobScript(delete ? "CompleteAndDelete" : "Complete"),
            [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, entry.GetString("_respire.job.batch")!],
            [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", entry.Id.Value, attempt, outcome, "explicit", ""]);

    private static ServiceProvider BatchWorker(IRespireClient client, BatchHandlerState state)
    {
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state);
        services.AddRespireJobQueue<BatchHandler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerCount = 1, DeliveryLimit = 3, ReadWait = TimeSpan.FromMilliseconds(20) },
            new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(20) });
        return services.BuildServiceProvider();
    }

    public sealed class BatchHandlerState
    {
        public bool Retry { get; init; }
        public bool Fail { get; init; }
        public bool HoldFirst { get; init; }
        public bool HoldRetry { get; init; }
        public int Members;
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class BatchHandler(BatchHandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            state.Calls.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
            if (job.Id != "member") return RespireStreamWorkerResult.Ack;
            var attempt = Interlocked.Increment(ref state.Members);
            state.FirstEntered.TrySetResult();
            if (state.HoldFirst) await state.Release.Task.WaitAsync(cancellationToken);
            if (state.Retry && attempt == 1) return job.RetryAfter(TimeSpan.FromMilliseconds(20));
            state.RetryEntered.TrySetResult();
            if (state.HoldRetry) await state.Release.Task.WaitAsync(cancellationToken);
            return state.Fail ? job.RetryAfter(TimeSpan.Zero) : RespireStreamWorkerResult.Ack;
        }
    }
}
