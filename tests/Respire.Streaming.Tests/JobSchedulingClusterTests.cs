using System.Text;
using Respire.Internal;
using System.Reflection;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobSchedulingClusterTests
{
    [Test]
    [NotInParallel]
    public async Task ActiveFollowupsUseSameSlotKeysWithPrefixedTransactions()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireJobQueue<string>(client, "followup-cluster", Encoding.UTF8.GetBytes);
            await queue.EnqueueAsync(RespireJobDeduplicationMode.KeepLastIfActive, "job", "active");
            await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
            var active = (await JobReplacementIntegrationTests.Read(client, queue)).Single();
            await using var transaction = client.CreateTransaction();
            var successor = queue.Enqueue(transaction, "job", "superseded");
            var latest = queue.EnqueueScheduled(transaction, "job", "last", new() { Group = "group:{other}:東京" });
            await transaction.CommitAsync();
            await Assert.That(successor.Result.Enqueued).IsTrue();
            await Assert.That(latest.Result.Enqueued).IsTrue();
            var type = typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueScripts")!;
            var complete = (RespireScript)type.GetField("Complete", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            complete = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobDeduplication")!
                .GetMethod("Terminal", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [complete, false])!;
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(complete,
                [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":dedup-deliveries"],
                [RespireJobQueue<string>.ConsumerGroup, "owner", active.Id.Value, 1, "completed", "", ""])).IsEqualTo(1);
            await Assert.That((await JobReplacementIntegrationTests.Read(client, queue, grouped: true)).Single()
                .GetString("_respire.job.payload")).IsEqualTo("last");
        }
    }

    [Test]
    [NotInParallel]
    public async Task ReplaceUsesExplicitSameSlotLifecycleKeysWithPrefixedTransactionsAndAcquisition()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireJobQueue<string>(client, "replacement", Encoding.UTF8.GetBytes);
            await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "old");
            await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "orphan", "deleted");
            var orphan = (await client.Streams.ReadAsync(queue.StreamKey)).Single(entry => entry.GetString("_respire.job.id") == "orphan");
            await client.Streams.RemoveAsync(queue.StreamKey, orphan.Id);
            await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(1);
            await using var transaction = client.CreateTransaction();
            var replace = queue.EnqueueScheduled(transaction, "job", "new", new() { Group = "group:{other}:東京" });
            await transaction.CommitAsync();
            await Assert.That(replace.Result.Enqueued).IsTrue();
            await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
            var entries = new List<RespireStreamEntry>();
            for (var i = 0; i < 3; i++) entries.AddRange(await JobReplacementIntegrationTests.Read(client, queue, grouped: true));
            await Assert.That(entries.Count).IsEqualTo(1);
            await Assert.That(entries[0].GetString("_respire.job.payload")).IsEqualTo("new");
            await Assert.That((await queue.EnqueueAsync("job", "active duplicate")).Enqueued).IsFalse();
        }
    }

    [Test]
    [NotInParallel]
    public async Task TransactionalEnqueueSharesCallerHashSlotAndRejectsCrossSlotWrites()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireJobQueue<string>(client, "transaction", Encoding.UTF8.GetBytes);
            await using var transaction = client.CreateTransaction();
            var saved = transaction.Set("{respire-job:transaction}:order", "paid");
            var immediate = queue.Enqueue(transaction, "immediate", "payload");
            var delayed = queue.EnqueueScheduled(transaction, "delayed", "payload", new() { Delay = TimeSpan.FromHours(1) });
            var grouped = queue.EnqueueScheduled(transaction, "grouped", "payload", new() { Group = "group" });
            await transaction.CommitAsync();
            await Assert.That(saved.Result).IsTrue();
            await Assert.That(immediate.Result.Enqueued).IsTrue();
            await Assert.That(delayed.Result.Enqueued).IsTrue();
            await Assert.That(grouped.Result.Enqueued).IsTrue();
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(2);
        }
        var otherQueue = new RespireJobQueue<string>(root, "cross-slot", Encoding.UTF8.GetBytes);
        await using var invalid = root.CreateTransaction();
        _ = invalid.Set("{different}:order", "paid");
        await Assert.That(() => otherQueue.Enqueue(invalid, "id", "payload")).Throws<InvalidOperationException>();
        await Assert.That(invalid.Count).IsEqualTo(1);
        await Assert.That(await root.Streams.CountAsync(otherQueue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task PrefixedEnqueuePromotionAndRetryExecuteInOneRealClusterSlot()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var batchQueue = new RespireJobQueue<string>(client, "batch-cluster", Encoding.UTF8.GetBytes);
            await batchQueue.CreateBatchAsync(new() { OnComplete = new("callback", "done") }, "batch{untrusted}");
            await batchQueue.AddBatchJobAsync("batch{untrusted}", "member", "payload");
            await batchQueue.SealBatchAsync("batch{untrusted}");
            await client.Streams.CreateGroupAsync(batchQueue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
            var member = (await client.Streams.ReadGroupOnceAsync(batchQueue.StreamKey,
                RespireJobQueue<string>.ConsumerGroup, "owner", new() { Count = 1 })).Single();
            var complete = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueScripts")!
                .GetField("Complete", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(complete,
                [batchQueue.StreamKey, batchQueue.EventsStreamKey, batchQueue.DeadLetterStreamKey,
                    member.GetString("_respire.job.batch")!],
                [RespireJobQueue<string>.ConsumerGroup, "owner", member.Id.Value, 1, "completed"]))
                .IsEqualTo(1);
            await Assert.That((await batchQueue.GetBatchStatusAsync("batch{untrusted}"))!.IsCompleted).IsTrue();
            var callback = (await client.Streams.ReadGroupOnceAsync(batchQueue.StreamKey,
                RespireJobQueue<string>.ConsumerGroup, "owner", new() { Count = 1 })).Single();
            await Assert.That(callback.GetString("_respire.job.id")).IsEqualTo("callback");
            var queue = new RespireJobQueue<string>(client, "cluster", Encoding.UTF8.GetBytes);
            var keys = JobGroupIntegrationTests.Keys(queue);
            var original = await queue.EnqueueScheduledAsync("id{untrusted}", "payload", new()
            {
                Group = "tenant:{other}:東京", DeduplicationMode = RespireJobDeduplicationMode.Extend,
            });
            await Assert.That(await queue.EnqueueAsync("id{untrusted}", "duplicate"))
                .IsEqualTo(new RespireJobEnqueueResult(original.EntryId, false));
            const string group = RespireJobQueue<string>.ConsumerGroup;
            await client.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
            var first = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
            var logs = JobLogKeys.ForJob(queue.StreamKey, "id{untrusted}");
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", first.Id.Value, 1, "before retry", 10, 60000])).IsEqualTo(1);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Retry"),
                [.. keys], [group, "owner", first.Id.Value, 1, 0])).IsEqualTo(1);
            var second = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", first.Id.Value, 1, "stale", 10, 60000])).IsEqualTo(0);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", second.Id.Value, 1, "after retry", 10, 60000])).IsEqualTo(1);
            await Assert.That(string.Join("|", await queue.GetLogsAsync("id{untrusted}")))
                .IsEqualTo("before retry|after retry");
            await Assert.That(second.GetString("_respire.job.attempts")).IsEqualTo("1");
            await Assert.That(second.GetString("_respire.job.id")).IsEqualTo("id{untrusted}");
            await Assert.That(second.GetString("_respire.job.group")).IsEqualTo("tenant:{other}:東京");
            await Assert.That(second.GetString("_respire.job.deduplication")).IsEqualTo("Extend");
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("CompleteWithMetrics"),
                [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"],
                [group, "owner", second.Id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 5])).IsEqualTo(1);
            await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Sum(bucket => bucket.Completed)).IsEqualTo(1);
            await queue.EnqueueScheduledAsync("controlled", "payload", new() { Group = "controlled" });
            await queue.SetRateLimitAsync(new() { PermitLimit = 1, Window = TimeSpan.FromHours(1) });
            await queue.PauseAsync();
            using (var paused = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(paused[2].AsInteger()).IsEqualTo(1);
            await queue.ResumeAsync();
            using (var admitted = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(admitted[1].Count).IsEqualTo(1);
            using (var denied = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(denied[3].AsInteger()).IsGreaterThan(0);
            await Assert.That((await queue.GetStateAsync()).RateLimitHits).IsEqualTo(1);

            // Application-group controls use the same explicit keys even when a client prefix
            // supplies the effective Cluster hash tag instead of the queue's own tag.
            await queue.SetRateLimitAsync(null);
            const string applicationGroup = "limited:{foreign}:東京";
            await queue.SetGroupOptionsAsync(applicationGroup, new() { MaxConcurrency = 1 });
            await queue.PauseGroupAsync(applicationGroup);
            await queue.EnqueueScheduledAsync("limited-1", "p", new() { Group = applicationGroup });
            await queue.EnqueueScheduledAsync("limited-2", "p", new() { Group = applicationGroup });
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Length).IsEqualTo(0);
            await queue.ResumeGroupAsync(applicationGroup);
            var leased = (await JobGroupIntegrationTests.Read(client, queue, "limited")).Single();
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Length).IsEqualTo(0);
            var release = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobGroupControls")!
                .GetField("Release", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(release, keys, ["limited", leased.Id.Value, 1])).IsEqualTo(1);
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Single()
                .GetString("_respire.job.id")).IsEqualTo("limited-2");
        }
    }
}
