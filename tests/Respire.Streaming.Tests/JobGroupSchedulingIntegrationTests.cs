using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobGroupSchedulingIntegrationTests(RedisTestContainer redis)
{
    private const string ConsumerGroup = RespireJobQueue<string>.ConsumerGroup;

    [Test]
    public async Task HostedWorkersConsumeGroupedJobsThroughIndependentClients()
    {
        await using var first = await Connect();
        await using var second = await Connect();
        var queue = Queue(first);
        for (var i = 0; i < 20; i++)
            await queue.EnqueueScheduledAsync("id-" + i, "p", new() { Group = i % 2 == 0 ? "a" : "b" });
        var state = new HandledJobs();
        await using var firstHost = Host(first, "first");
        await using var secondHost = Host(second, "second");
        var workers = new[] { firstHost, secondHost }.Select(provider => provider.GetServices<IHostedService>().Single()).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            foreach (var worker in workers) await worker.StartAsync(deadline.Token);
            await state.Complete.Task.WaitAsync(deadline.Token);
            await Assert.That(state.Jobs.Count).IsEqualTo(20);
            await Assert.That(state.Jobs.Count(job => job.Group == "a")).IsEqualTo(10);
            await Assert.That(state.Jobs.Select(job => job.Id).Distinct().Count()).IsEqualTo(20);
        }
        finally { foreach (var worker in workers) await worker.StopAsync(CancellationToken.None); }
        await Assert.That((await first.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);

        ServiceProvider Host(IRespireClient client, string consumer)
        {
            var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
            services.AddRespireJobQueue<GroupHandler, string>("fair", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ConsumerName = consumer, BatchSize = 2, ReadWait = TimeSpan.FromMilliseconds(20) },
                new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(20) });
            return services.BuildServiceProvider();
        }
    }

    public sealed class HandledJobs
    {
        public ConcurrentQueue<RespireJob<string>> Jobs { get; } = new();
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class GroupHandler(HandledJobs state) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            state.Jobs.Enqueue(job);
            if (state.Jobs.Count == 20) state.Complete.TrySetResult();
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task TenSmallJobsFinishWithinTwentySelectionsAcrossClientsAndRestarts(int protocol)
    {
        await using var producer = await Connect(protocol);
        var queue = Queue(producer);
        for (var i = 0; i < 10000; i++)
            await queue.EnqueueScheduledAsync("large-" + i, "payload", new() { Group = "large" });
        for (var i = 0; i < 10; i++)
            await queue.EnqueueScheduledAsync("small-" + i, "payload", new() { Group = "small" });
        await producer.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);

        // Two independent connections use exactly the hosted worker's atomic selection script.
        // Recreate both midway through a round: neither process owns a fairness cursor.
        var delivered = new List<RespireStreamEntry>();
        for (var restart = 0; restart < 2; restart++)
        {
            await using var first = await Connect(protocol);
            await using var second = await Connect(protocol);
            var reads = await Task.WhenAll(Consume(first, "first-" + restart, 5), Consume(second, "second-" + restart, 5));
            delivered.AddRange(reads.SelectMany(entries => entries));
        }
        await Assert.That(delivered.Count).IsEqualTo(20);
        await Assert.That(delivered.Count(entry => entry.GetString("_respire.job.group") == "small")).IsEqualTo(10);
        await Assert.That(delivered.Select(entry => entry.GetString("_respire.job.id")).Distinct().Count()).IsEqualTo(20);
        await Assert.That((await producer.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
        await Assert.That(await producer.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(9990);
        var ordered = (await producer.Streams.ReadAsync(queue.StreamKey)).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            await Assert.That(ordered[i].GetString("_respire.job.group")).IsEqualTo(i % 2 == 0 ? "large" : "small");

        async Task<List<RespireStreamEntry>> Consume(IRespireClient client, string name, int count)
        {
            var result = new List<RespireStreamEntry>();
            for (var i = 0; i < count; i++)
            {
                var entry = (await JobGroupIntegrationTests.Read(client, queue, name)).Single();
                using (await client.ExecuteAsync("XACK", [queue.StreamKey, ConsumerGroup, entry.Id.Value])) { }
                result.Add(entry);
            }
            return result;
        }
    }

    [Test]
    public async Task NewGroupJoinsRotationWithoutWaitingForTheExistingBacklog()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        for (var i = 0; i < 100; i++) await queue.EnqueueScheduledAsync("large-" + i, "p", new() { Group = "large" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await JobGroupIntegrationTests.Read(client, queue, "first", count: 5);
        await queue.EnqueueScheduledAsync("new", "p", new() { Group = "small" });
        var next = (await JobGroupIntegrationTests.Read(client, queue, "restarted")).Single();
        await Assert.That(next.GetString("_respire.job.id")).IsEqualTo("new");
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(95);
    }

    [Test]
    public async Task DelaysExpiryMissingBodiesPriorityAndUngroupedPrecedenceAreExplicit()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("delayed", "p", new() { Group = "a", Delay = TimeSpan.FromDays(1) });
        var missing = await queue.EnqueueScheduledAsync("missing", "p", new() { Group = "b" });
        using (await client.ExecuteAsync("XDEL", [queue.StreamKey + ":scheduled", missing.EntryId.Value])) { }
        await queue.EnqueueScheduledAsync("low", "p", new() { Group = "c", Priority = -1 });
        await queue.EnqueueScheduledAsync("high", "p", new() { Group = "c", Priority = 10 });
        await queue.EnqueueScheduledAsync("peer", "p", new() { Group = "d", Priority = -100 });
        // A ready flow/recurring/immediate job has FIFO precedence over grouped selection.
        await queue.EnqueueAsync("immediate", "p");
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var immediate = (await JobGroupIntegrationTests.Read(client, queue, "owner", count: 8)).Single();
        await Assert.That(immediate.GetString("_respire.job.id")).IsEqualTo("immediate");
        var entries = await JobGroupIntegrationTests.Read(client, queue, "owner", count: 4);
        await Assert.That(entries.Select(entry => entry.GetString("_respire.job.id")!).ToArray()).IsEquivalentTo(["high", "peer"]);
        await Assert.That(entries[0].GetString("_respire.job.id")).IsEqualTo("high");
        var low = (await JobGroupIntegrationTests.Read(client, queue, "owner", count: 2)).Single();
        await Assert.That(low.GetString("_respire.job.id")).IsEqualTo("low");

        // Fixed expired envelope, indexed using the real staged member: no timing dependency.
        var staged = await client.Streams.AddAsync(queue.StreamKey + ":scheduled", ("_respire.job.id", "expired"),
            ("_respire.job.payload", "p"), ("_respire.job.group", "e"), ("_respire.job.expires", "1"));
        using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":group-order", 0, "65|"])) { }
        using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":group-jobs", 0, "65|0000000000000000|" + staged.Value])) { }
        await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "owner", count: 4)).Length).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
    }

    [Test]
    [Arguments("zrem")]
    [Arguments("xdel")]
    [Arguments("set")]
    [Arguments("xreadgroup")]
    [Arguments("xrange")]
    public async Task DeniedSelectionLeavesTheJobStagedAndUndelivered(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueScheduledAsync("id", "p", new() { Group = "a" });
        await admin.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var user = "group-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await JobGroupIntegrationTests.Read(restricted, queue, "owner")).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
            await Assert.That((await JobGroupIntegrationTests.Read(admin, queue, "owner")).Length).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(":group-order")]
    [Arguments(":group-jobs")]
    public async Task WrongIndexTypeCannotPartiallyEnqueue(string suffix)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await client.Strings.SetAsync(queue.StreamKey + suffix, "wrong type");
        await Assert.That(async () => await queue.EnqueueScheduledAsync("id", "p", new() { Group = "a" })).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        await client.Keys.DeleteAsync(queue.StreamKey + suffix);
        await Assert.That((await queue.EnqueueScheduledAsync("id", "p", new() { Group = "a" })).Enqueued).IsTrue();
    }

    [Test]
    public async Task CursorFailureRollsBackPromotionAndLostReadReplyLeavesRecoverablePendingJob()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("id", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var source = JobSchedulingIntegrationTests.Script("ReadGroups").Source;
        var failure = RespireScript.Create(source.Replace("redis.pcall('SET', cursor, prefix)", "redis.pcall('SET', cursor, prefix, 'PX', -1)"));
        await Assert.That(async () => await JobGroupIntegrationTests.Read(client, queue, "owner", script: failure)).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
        var lost = RespireScript.Create(source.Replace("return ready[1][2]", "error('injected lost read reply')"));
        await Assert.That(async () => await JobGroupIntegrationTests.Read(client, queue, "crashed", script: lost)).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(1);
        await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "replacement")).Length).IsEqualTo(0);
        using var claimed = await client.Scripts.ExecuteAsync(Respire.Internal.StreamWorkerScripts.Claim,
            [queue.StreamKey], [ConsumerGroup, "replacement", 0, "0-0", 1]);
        await Assert.That(claimed[1].Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedGroupedIndexOrDedupWriteRollsBackOnlyTheNewJob(bool dedupFailure)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("existing", "p", new() { Group = "a" });
        var dedup = queue.StreamKey + ":dedup:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("new")));
        var source = JobSchedulingIntegrationTests.Script("Enqueue").Source;
        var brokenSource = dedupFailure
            ? source.Replace("record(id, ARGV[12], ARGV[3]), 'PX', ARGV[3]", "record(id, ARGV[12], ARGV[3]), 'PX', -1")
            : source.Replace("redis.pcall('ZADD', order, 0, prefix)", "redis.pcall('ZADD', order, 'bad-score', prefix)");
        await Assert.That(brokenSource).IsNotEqualTo(source);
        var broken = RespireScript.Create(brokenSource);
        var keys = JobGroupIntegrationTests.Keys(queue);
        await Assert.That(async () =>
        {
            using var reply = await client.Scripts.ExecuteAsync(broken, [.. keys.Take(3), dedup, .. keys.Skip(3)],
                ["new", "p", 60000, 0, 0, 0, "", "", "a"]);
        }).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
        await Assert.That((await queue.EnqueueScheduledAsync("new", "p", new() { Group = "a" })).Enqueued).IsTrue();
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var entries = await JobGroupIntegrationTests.Read(client, queue, "owner", count: 2);
        await Assert.That(entries.Select(entry => entry.GetString("_respire.job.id")!).ToArray()).IsEquivalentTo(["existing", "new"]);
    }

    [Test]
    public async Task FailedGroupedRetryAcknowledgementKeepsOriginalPendingAndRemovesNewIndexes()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("id", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var entry = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
        var source = JobSchedulingIntegrationTests.Script("Retry").Source;
        var broken = RespireScript.Create(source.Replace("redis.call('XACK', KEYS[1], ARGV[1], ARGV[3])", "error('injected acknowledgement failure')"));
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(broken, JobGroupIntegrationTests.Keys(queue),
            [ConsumerGroup, "owner", entry.Id.Value, 1, 0, 0])).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(1);
        await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "other")).Length).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Retry"), JobGroupIntegrationTests.Keys(queue),
            [ConsumerGroup, "owner", entry.Id.Value, 1, 0, 0])).IsEqualTo(1);
        var retried = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
        await Assert.That(retried.GetString("_respire.job.group")).IsEqualTo("a");
        await Assert.That(retried.GetString("_respire.job.attempts")).IsEqualTo("1");
    }

    private ValueTask<RespireClient> Connect(int protocol = 3) => RespireClient.ConnectAsync(
        RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "fair", Encoding.UTF8.GetBytes);
}
