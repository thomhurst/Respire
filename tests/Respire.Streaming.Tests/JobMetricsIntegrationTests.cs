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
    [Arguments(2, false, false)]
    [Arguments(3, false, false)]
    [Arguments(2, true, false)]
    [Arguments(3, true, false)]
    [Arguments(2, false, true)]
    [Arguments(3, false, true)]
    public async Task MetricsFenceTerminalDeliveryAndLostReplyReplay(int protocol, bool delete, bool failed)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var id = await PendingJobAsync(client, queue);
        var script = JobScript(delete ? "CompleteAndDeleteWithMetrics" : "CompleteWithMetrics");
        RespireKey[] keys = failed
            ? [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, queue.StreamKey + ":metrics"]
            : [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "wrong-owner", id.Value, 1,
            failed ? "dead-letter" : "completed", "explicit", "", 0, Array.Empty<byte>(), 3];
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys, args)).IsEqualTo(0);
        await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Count).IsEqualTo(0);
        args[1] = "crashed";
        args[3] = 2;
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys, args)).IsEqualTo(0);
        args[3] = 1;
        // Discard the first reply, then replay the exact operation as after an ambiguous reply.
        await client.Scripts.ExecuteIntegerAsync(script, keys, args);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys, args)).IsEqualTo(0);
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey);
        await Assert.That(buckets.Count).IsEqualTo(3);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(failed ? 0 : 1);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(failed ? 1 : 0);
        await Assert.That((await RespireJobMetrics.ReadAsync(root, queue.StreamKey)).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, false, false)]
    [Arguments(5, false, false)]
    [Arguments(0, true, false)]
    [Arguments(5, true, false)]
    [Arguments(0, true, true)]
    [Arguments(5, true, true)]
    public async Task MetricsHostedConcurrentWorkersCountTerminalOutcomesOnly(int retention, bool grouped, bool delete)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        if (grouped) await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 2 });
        for (var i = 0; i < 20; i++)
        {
            if (grouped)
                await queue.EnqueueScheduledAsync("job-" + i, i % 2 == 0 ? "ok" : "fail", new() { Group = "a" });
            else
                await queue.EnqueueAsync("job-" + i, i % 2 == 0 ? "ok" : "fail");
        }
        ServiceProvider Provider(string consumer)
        {
            var services = new ServiceCollection().AddSingleton<IRespireClient>(client);
            services.AddRespireJobQueue<MetricsHandler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ConsumerName = consumer, ConsumerCount = 2, DeliveryLimit = 2, DeleteAcknowledgedEntries = delete,
                    ReadWait = TimeSpan.FromMilliseconds(10), RecoveryPollInterval = TimeSpan.FromMilliseconds(10) },
                new() { MetricsRetentionMinutes = retention, RetryBackoff = TimeSpan.Zero,
                    SchedulingPollInterval = TimeSpan.FromMilliseconds(10) });
            return services.BuildServiceProvider();
        }
        await using var first = Provider("first");
        await using var second = Provider("second");
        var workers = new[] { first.GetRequiredService<IHostedService>(), second.GetRequiredService<IHostedService>() };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var worker in workers) await worker.StartAsync(deadline.Token);
        try { await UntilAsync(async () => await client.Streams.CountAsync(queue.EventsStreamKey) == 20, deadline.Token); }
        finally { foreach (var worker in workers) await worker.StopAsync(deadline.Token); }
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey);
        await Assert.That(buckets.Count).IsEqualTo(retention);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(retention == 0 ? 0 : 10);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(retention == 0 ? 0 : 10);
    }

    public sealed class MetricsHandler : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
            => ValueTask.FromResult(job.Payload == "ok" ? RespireStreamWorkerResult.Ack : RespireStreamWorkerResult.Nack);
    }

    [Test]
    public async Task MetricsCompletionPreservesFlowKeyPositions()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueFlowAsync("flow", new("parent", "ok") { Children = [new("child", "ok")] });
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client);
        services.AddRespireJobQueue<MetricsHandler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(10) }, new() { MetricsRetentionMinutes = 3 });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        try { await UntilAsync(async () => (await queue.GetFlowResultsAsync("flow")).All(r => r.Status == "completed"), deadline.Token); }
        finally { await worker.StopAsync(deadline.Token); }
        await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Sum(b => b.Completed)).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsCompletionPreservesBatchKeyPositionsAndCountsCallbacks(bool cancel)
    {
        await using var root = await ConnectAsync();
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        await queue.CreateBatchAsync(new() { OnComplete = new("callback", "ok") }, "batch");
        await queue.AddBatchJobAsync("batch", "member", "ok");
        if (cancel) await queue.CancelBatchAsync("batch");
        else await queue.SealBatchAsync("batch");
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client);
        services.AddRespireJobQueue<MetricsHandler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(10) }, new() { MetricsRetentionMinutes = 3 });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        try { await UntilAsync(async () => await client.Streams.CountAsync(queue.EventsStreamKey) == 2, deadline.Token); }
        finally { await worker.StopAsync(deadline.Token); }
        var status = (await queue.GetBatchStatusAsync("batch"))!;
        await Assert.That(status.IsCompleted).IsTrue();
        await Assert.That(status.Cancelled).IsEqualTo(cancel ? 1 : 0);
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(cancel ? 1 : 2);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(0);
    }

    [Test]
    [Arguments("completed", false)]
    [Arguments("dead-letter", false)]
    [Arguments("completed", true)]
    [Arguments("dead-letter", true)]
    public async Task MetricsBatchTerminalCountsMatchEvents(string outcome, bool cancel)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(batchId: "batch");
        await queue.AddBatchJobAsync("batch", "member", "ok");
        if (cancel) await queue.CancelBatchAsync("batch");
        else await queue.SealBatchAsync("batch");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(client, queue)).Single();
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey,
            member.GetString("_respire.job.batch")!, queue.StreamKey + ":metrics"];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 1,
            outcome, "explicit", "", 0, Array.Empty<byte>(), 3];
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("CompleteWithMetrics"), keys, args)).IsEqualTo(cancel ? 2 : 1);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("CompleteWithMetrics"), keys, args)).IsEqualTo(0);
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(!cancel && outcome == "completed" ? 1 : 0);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(!cancel && outcome == "dead-letter" ? 1 : 0);
        var terminal = (await client.Streams.ReadAsync(queue.EventsStreamKey)).Single();
        await Assert.That(terminal.GetString("kind")).IsEqualTo(cancel ? "cancelled" : outcome);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsAndBatchCallbacksRollbackTogether(bool failMetrics)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.CreateBatchAsync(new() { OnComplete = new("callback", "ok") }, "batch");
        await queue.AddBatchJobAsync("batch", "member", "ok");
        await queue.SealBatchAsync("batch");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var member = (await ReadBatchJobs(client, queue)).Single();
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey,
            member.GetString("_respire.job.batch")!, queue.StreamKey + ":metrics"];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "batch-consumer", member.Id.Value, 1,
            "completed", "", "", 0, Array.Empty<byte>(), 3];
        var script = JobScript("CompleteWithMetrics");
        var source = failMetrics
            ? script.Source.Replace("redis.call('PEXPIREAT', metricsKey, (minute + retention) * 60000)",
                "error('ERR injected metrics failure')", StringComparison.Ordinal)
            : script.Source.Replace("return redis.pcall('XACK', KEYS[1], ARGV[1], ARGV[3])",
                "return {err = 'ERR injected acknowledgement failure'}", StringComparison.Ordinal);
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(RespireScript.Create(source), keys, args))
            .Throws<RespireServerException>();
        var status = (await queue.GetBatchStatusAsync("batch"))!;
        await Assert.That(status.Pending).IsEqualTo(1);
        await Assert.That(status.Succeeded).IsEqualTo(0);
        await Assert.That(status.IsCompleted).IsFalse();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Count).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(1);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, keys, args)).IsEqualTo(1);
        await Assert.That((await queue.GetBatchStatusAsync("batch"))!.IsCompleted).IsTrue();
        await Assert.That((await ReadBatchJobs(client, queue)).Single().GetString("_respire.job.id")).IsEqualTo("callback");
        await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Sum(b => b.Completed)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MetricsRollbackRestoresCountersAndExpiryWhenAcknowledgementFails(bool existing)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var id = await PendingJobAsync(client, queue);
        var key = queue.StreamKey + ":metrics";
        if (existing)
            await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
                local minute = math.floor(tonumber(redis.call('TIME')[1]) / 60)
                redis.call('HSET', KEYS[1], 'retention', 3, tostring(minute % 3), cmsgpack.pack({minute, 7, 2}))
                redis.call('PEXPIRE', KEYS[1], 10000)
                return 1
                """), [key], []);
        // Inject an acknowledgement error after successful event and metrics writes. Production
        // can reach this rollback when a capability-aware acknowledgement is denied by Redis.
        var source = JobScript("CompleteWithMetrics").Source.Replace(
            "return redis.pcall('XACK', KEYS[1], ARGV[1], ARGV[3])", "return {err = 'ERR injected acknowledgement failure'}", StringComparison.Ordinal);
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(RespireScript.Create(source),
            [queue.StreamKey, queue.EventsStreamKey, key],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 3])).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(1);
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(existing ? 7 : 0);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(existing ? 2 : 0);
        var ttl = await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PTTL', KEYS[1])"), [key], []);
        if (existing)
        {
            await Assert.That(ttl).IsGreaterThan(0);
            await Assert.That(ttl).IsLessThanOrEqualTo(10000);
        }
        else await Assert.That(ttl).IsEqualTo(-2);
    }

    [Test]
    public async Task MetricsRejectWrongTypeAndRetentionMismatchWithoutAcknowledging()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var id = await PendingJobAsync(client, queue);
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "crashed", id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 3];
        foreach (var seed in new[] { "redis.call('SET', KEYS[1], 'wrong')", "redis.call('HSET', KEYS[1], 'retention', 4)" })
        {
            await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("redis.call('DEL', KEYS[1]); " + seed + "; return 1"), [keys[^1]], []);
            await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(JobScript("CompleteWithMetrics"), keys, args)).Throws<RespireServerException>();
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("hset")]
    [Arguments("pexpireat")]
    public async Task MetricsDeniedWritePreservesDelivery(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var id = await PendingJobAsync(admin, queue);
        var username = "metrics-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">metrics-test", "~*", "+@all", "-" + denied]);
        try
        {
            await using var limited = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Username = username, Password = "metrics-test" });
            await Assert.That(async () => await limited.Scripts.ExecuteIntegerAsync(JobScript("CompleteWithMetrics"),
                [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"],
                [RespireJobQueue<string>.ConsumerGroup, "crashed", id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 3])).Throws<RespireServerException>();
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That((await RespireJobMetrics.ReadAsync(admin, queue.StreamKey)).Count).IsEqualTo(0);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    public async Task MetricsRetentionBoundsSlotsIgnoresAgedBucketsAndExpiresIdleQueues()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        // Seed a full ring with buckets from the previous rotation, using Redis time.
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            local minute = math.floor(tonumber(redis.call('TIME')[1]) / 60)
            redis.call('HSET', KEYS[1], 'retention', 3)
            for at = minute - 5, minute - 3 do
                redis.call('HSET', KEYS[1], tostring(at % 3), cmsgpack.pack({at, 99, 99}))
            end
            return 1
            """), [queue.StreamKey + ":metrics"], []);
        var id = await PendingJobAsync(client, queue);
        await client.Scripts.ExecuteIntegerAsync(JobScript("CompleteWithMetrics"),
            [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 3]);
        var buckets = await RespireJobMetrics.ReadAsync(client, queue.StreamKey, 1440);
        await Assert.That(buckets.Count).IsEqualTo(3);
        await Assert.That(buckets.Sum(b => b.Completed)).IsEqualTo(1);
        await Assert.That(buckets.Sum(b => b.Failed)).IsEqualTo(0);
        using var bounds = await client.Scripts.ExecuteAsync(RespireScript.Create("return {redis.call('HLEN', KEYS[1]), redis.call('PTTL', KEYS[1])}"), [queue.StreamKey + ":metrics"], []);
        await Assert.That(bounds[0].AsInteger()).IsEqualTo(4);
        await Assert.That(bounds[1].AsInteger()).IsGreaterThan(0);
        await Assert.That(bounds[1].AsInteger()).IsLessThanOrEqualTo(180000);
        // Move the verified expiry deadline into the past; no worker or reader cleanup is needed.
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIREAT', KEYS[1], 1)"), [queue.StreamKey + ":metrics"], []);
        await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Count).IsEqualTo(0);
    }
}
