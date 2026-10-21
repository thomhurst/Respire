using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardDeduplicationTests(RedisTestContainer redis)
{
    [Test]
    public async Task BatchCallbacksUseSeparateLifecycle()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "batch-callback-policy", Encoding.UTF8.GetBytes);
        await queue.CreateBatchAsync(new()
        {
            OnSuccess = new("success", "success-secret"),
            OnComplete = new("complete", "complete-secret"),
        }, "batch");
        await queue.SealBatchAsync("batch");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var entries = await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "worker", new() { Count = 2 });
        await Assert.That(entries.Length).IsEqualTo(2);
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "batch-callback-policy", Stream = queue.StreamKey,
            Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });

        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var policies = snapshot.Streams[0].Deduplication!;
        await Assert.That(policies.Count).IsEqualTo(2);
        foreach (var entry in entries)
        {
            await Assert.That(entry.GetString("_respire.job.batch_callback")).IsNotNull();
            await Assert.That(policies[entry.Id].Identity).IsEqualTo(entry.GetString("_respire.job.id"));
            await Assert.That(policies[entry.Id].Mode).IsEqualTo("Separate lifecycle");
        }
        await Assert.That(snapshot.Streams[0].Error).IsNull();
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Separate lifecycle");
        await Assert.That(html).DoesNotContain("FirstSubmissionWins");
        await Assert.That(html).DoesNotContain("success-secret");
        await Assert.That(html).DoesNotContain("complete-secret");
    }

    [Test]
    public async Task RecurringTicksUseSeparateLifecycle()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "recurring-policy", Encoding.UTF8.GetBytes);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "recurring-secret", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var entries = await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "worker", new() { Count = 1 });
        var options = new RespireDashboardOptions();
        options.Streams.Add(new() { Name = "recurring-policy", Stream = queue.StreamKey,
            Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });

        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var policy = snapshot.Streams[0].Deduplication![entries[0].Id];
        await Assert.That(policy.Identity).IsEqualTo(entries[0].GetString("_respire.job.id"));
        await Assert.That(policy.Mode).IsEqualTo("Separate lifecycle");
        await Assert.That(snapshot.Streams[0].Error).IsNull();
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Separate lifecycle");
        await Assert.That(html).DoesNotContain("FirstSubmissionWins");
        await Assert.That(html).DoesNotContain("recurring-secret");
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    [Test]
    public async Task PendingPoliciesAreBoundedEncodedAndDoNotExposePayloads()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "deduplication", Encoding.UTF8.GetBytes);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "<identity>&", "secret-payload");
        await queue.EnqueueAsync("default", "other-secret");
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "replace-identity", "replacement-secret");
        await queue.EnqueueAsync(RespireJobDeduplicationMode.KeepLastIfActive, "followup-identity", "followup-secret");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "worker", new() { Count = 4 });
        var options = new RespireDashboardOptions { EntryLimit = 4 };
        options.Streams.Add(new() { Name = "deduplication", Stream = queue.StreamKey,
            Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var policies = snapshot.Streams[0].Deduplication!;
        await Assert.That(policies.Count).IsEqualTo(4);
        await Assert.That(policies.Values.Any(policy => policy.Identity == "<identity>&" && policy.Mode == "Extend")).IsTrue();
        await Assert.That(policies.Values.Any(policy => policy.Identity == "default" && policy.Mode == "FirstSubmissionWins")).IsTrue();
        await Assert.That(policies.Values.Any(policy => policy.Identity == "replace-identity" && policy.Mode == "Replace")).IsTrue();
        await Assert.That(policies.Values.Any(policy => policy.Identity == "followup-identity" && policy.Mode == "KeepLastIfActive")).IsTrue();
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Deduplication identity");
        await Assert.That(html).Contains("&lt;identity&gt;&amp;");
        await Assert.That(html).Contains("Extend");
        await Assert.That(html).Contains("Replace");
        await Assert.That(html).Contains("KeepLastIfActive");
        await Assert.That(html).DoesNotContain("secret-payload");
        await Assert.That(html).DoesNotContain("other-secret");
        await Assert.That(html).DoesNotContain("replacement-secret");
        await Assert.That(html).DoesNotContain("followup-secret");
        await Assert.That(snapshot.Streams[0].Error).IsNull();
        options.EntryLimit = 1;
        var bounded = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(bounded.Streams[0].Deduplication!.Count).IsEqualTo(1);
    }
}
