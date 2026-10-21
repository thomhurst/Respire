using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobActiveFollowupIntegrationTests(RedisTestContainer redis)
{
    private const string Group = RespireJobQueue<string>.ConsumerGroup;
    private const RespireJobDeduplicationMode Mode = RespireJobDeduplicationMode.KeepLastIfActive;

    [Test]
    [Arguments(RespireJobDeduplicationMode.FirstSubmissionWins, false)]
    [Arguments(RespireJobDeduplicationMode.FirstSubmissionWins, true)]
    [Arguments(RespireJobDeduplicationMode.Extend, false)]
    [Arguments(RespireJobDeduplicationMode.Extend, true)]
    [Arguments(RespireJobDeduplicationMode.Replace, false)]
    [Arguments(RespireJobDeduplicationMode.Replace, true)]
    public async Task OrdinaryEnqueuesRetainTheirKeyPermissions(RespireJobDeduplicationMode mode, bool transactional)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var user = "ordinary-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "+@all", "~" + queue.StreamKey,
            "~" + Dedup(queue), "~" + Lifecycle(queue)]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            var producer = Queue(restricted);
            async Task<RespireJobEnqueueResult> Submit(string payload)
            {
                if (!transactional) return await producer.EnqueueAsync(mode, "job", payload);
                await using var transaction = restricted.CreateTransaction();
                var pending = producer.Enqueue(transaction, mode, "job", payload);
                await transaction.CommitAsync();
                return pending.Result;
            }
            await Assert.That((await Submit("first")).Enqueued).IsTrue();
            await Assert.That((await Submit("duplicate")).Enqueued).IsEqualTo(mode == RespireJobDeduplicationMode.Replace);
            await admin.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
            // A superseded Replace envelope is acknowledged before the winner is read.
            if (mode == RespireJobDeduplicationMode.Replace) await Read(admin, queue);
            var entry = (await Read(admin, queue)).Single();
            await Assert.That(entry.GetString("_respire.job.payload"))
                .IsEqualTo(mode == RespireJobDeduplicationMode.Replace ? "duplicate" : "first");
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments("redis")]
    [Arguments("lua")]
    [Arguments("table")]
    public async Task TerminalErrorsPreserveTheirMessageAndHeldFollowup(string kind)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "winner");
        var failing = Terminal(RespireScript.Create(kind switch
        {
            "lua" => "error('NOPERM terminal probe')",
            "table" => "error({err = 'NOPERM terminal probe'})",
            _ => "return redis.call('GET', KEYS[1])",
        }));
        var error = await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(failing,
            [queue.StreamKey, Lifecycle(queue)], [Group, "owner", active.Id.Value]))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains(kind == "redis" ? "WRONGTYPE" : "NOPERM terminal probe");
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(1);
        await Promote(client, queue);
        await Assert.That((await Read(client, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("winner");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ActiveFollowupStillRequiresStagingKeyPermissions(bool transactional)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync(Mode, "job", "first");
        await admin.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(admin, queue)).Single();
        await queue.EnqueueAsync("job", "winner");
        var user = "staging-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "+@all", "~" + queue.StreamKey,
            "~" + Dedup(queue), "~" + Lifecycle(queue)]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            var error = await Assert.That(async () =>
            {
                if (!transactional) await Queue(restricted).EnqueueAsync("job", "denied");
                else
                {
                    await using var transaction = restricted.CreateTransaction();
                    var pending = Queue(restricted).Enqueue(transaction, "job", "denied");
                    await transaction.CommitAsync();
                    _ = pending.Result;
                }
            }).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("NOPERM");
            await Assert.That(await Complete(admin, queue, active)).IsEqualTo(1);
            await Promote(admin, queue);
            await Assert.That((await Read(admin, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("winner");
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RestartedWorkerDiscoversAnUnblockedFollowup(bool scheduling)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "last");
        await Complete(client, queue, active);
        var state = new HandlerState();
        await using var provider = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state)
            .AddRespireJobQueue<Handler, string>("followup", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ReadWait = TimeSpan.FromMilliseconds(10) },
                scheduling ? new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(10) } : null).BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try { await Assert.That(await state.Followup.Task.WaitAsync(deadline.Token)).IsEqualTo("last"); }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    [Arguments(false, false, 2)]
    [Arguments(false, true, 3)]
    [Arguments(true, false, 3)]
    [Arguments(true, true, 2)]
    public async Task ActiveDuplicatesRetainOnlyLatestEnvelopeAcrossApis(bool scheduled, bool transactional, int protocol)
    {
        await using var root = await Connect(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var first = await queue.EnqueueAsync(Mode, "job", "first");
        await Assert.That((await queue.EnqueueAsync("job", "pending duplicate")).Enqueued).IsFalse();
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        var expiry = await Expiry(client, queue);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i => await queue.EnqueueAsync("job", "duplicate-" + i)));
        var options = new RespireJobEnqueueOptions { Group = "new", Priority = 99, TimeToLive = TimeSpan.FromHours(1) };
        RespireJobEnqueueResult last;
        if (transactional)
        {
            await using var transaction = client.CreateTransaction();
            var pending = scheduled ? queue.EnqueueScheduled(transaction, "job", "last", options)
                : queue.Enqueue(transaction, "job", "last");
            await transaction.CommitAsync();
            last = pending.Result;
        }
        else last = scheduled ? await queue.EnqueueScheduledAsync("job", "last", options)
            : await queue.EnqueueAsync("job", "last");
        await Assert.That(last.Enqueued).IsTrue();
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        await Assert.That((await Read(client, queue, scheduled)).Length).IsEqualTo(0);
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(1);
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(0);
        await Promote(client, queue);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(0);
        var followup = (await Read(client, queue, scheduled)).Single();
        await Assert.That(followup.GetString("_respire.job.payload")).IsEqualTo("last");
        await Assert.That(followup.GetString("_respire.job.deduplication")).IsEqualTo("KeepLastIfActive");
        await Assert.That(followup.GetString("_respire.job.group")).IsEqualTo(scheduled ? "new" : null);
        await Assert.That(followup.GetString("_respire.job.priority")).IsEqualTo(scheduled ? "99" : null);
        await Assert.That((await root.Streams.ReadAsync(queue.StreamKey)).Length).IsEqualTo(0);
        await Assert.That(first.EntryId).IsNotEqualTo(followup.Id);
        await Assert.That(await Complete(client, queue, followup, scheduled)).IsEqualTo(1);
        await Assert.That((await queue.EnqueueAsync("job", "terminal duplicate")).Enqueued).IsFalse();
        await Assert.That((await Read(client, queue, scheduled)).Length).IsEqualTo(0);
        using var state = await client.ExecuteAsync("HLEN", [Lifecycle(queue)]);
        await Assert.That(state.AsInteger()).IsEqualTo(0);
    }

    [Test]
    [Arguments("completed")]
    [Arguments("dead-letter")]
    [Arguments("expired")]
    public async Task TerminalTransitionUnblocksOnceAndRejectsStaleOwners(string kind)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "next");
        // Claiming changes the PEL fence. A stale completion must restore both lifecycle fields.
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        var stale = await client.Scripts.ExecuteIntegerAsync(Terminal(Script("JobQueueScripts", "Complete")),
            [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, Lifecycle(queue)],
            [Group, "stale", active.Id.Value, 1, "completed", "", ""]);
        await Assert.That(stale).IsEqualTo(0);
        if (kind == "expired")
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(Terminal(Script("JobQueueScheduling", "CheckExpiry"), true),
                [queue.StreamKey, Lifecycle(queue)], [Group, "owner", active.Id.Value, 1, 1, 0])).IsEqualTo(-1);
        else await Assert.That(await Complete(client, queue, active, kind: kind)).IsEqualTo(1);
        await Promote(client, queue);
        var next = (await Read(client, queue)).Single();
        await Assert.That(next.GetString("_respire.job.payload")).IsEqualTo("next");
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(0);
        await Assert.That(await Complete(client, queue, next)).IsEqualTo(1);
    }

    [Test]
    [Arguments("retry")]
    [Arguments("yield")]
    public async Task BackoffAndYieldKeepFollowupBlocked(string transition)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "next");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("JobQueueScheduling", "Retry"),
            [.. JobGroupIntegrationTests.Keys(queue), Lifecycle(queue)],
            [Group, "owner", active.Id.Value, 1, 0, 0, transition])).IsEqualTo(1);
        await Promote(client, queue);
        var retry = (await Read(client, queue)).Single();
        await Assert.That(retry.GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That((await Read(client, queue)).Length).IsEqualTo(0);
        await queue.EnqueueAsync("job", "last");
        await Complete(client, queue, retry);
        await Promote(client, queue);
        await Assert.That((await Read(client, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("last");
    }

    [Test]
    public async Task WindowExpiryCreatesIndependentGenerationWithoutFencingActiveWork()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "old");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var old = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "old followup");
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIREAT', KEYS[1], 1)"), [Dedup(queue)], []);
        await queue.EnqueueAsync(Mode, "job", "new");
        var fresh = (await Read(client, queue)).Single();
        await Assert.That(old.GetString("_respire.job.generation")).IsNotEqualTo(fresh.GetString("_respire.job.generation"));
        await Complete(client, queue, old);
        await Complete(client, queue, fresh);
        await Promote(client, queue);
        await Assert.That((await Read(client, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("old followup");
    }

    [Test]
    [Arguments("hset")]
    [Arguments("hdel")]
    [Arguments("set")]
    [Arguments("zadd")]
    [Arguments("xdel")]
    public async Task DeniedFollowupWritesPreserveTheActiveJob(string command)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync(Mode, "job", "first");
        await admin.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(admin, queue)).Single();
        var record = await admin.GetStringAsync(Dedup(queue));
        var user = "followup-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Queue(restricted).EnqueueAsync("job", "denied")).Throws<RespireServerException>();
            await Assert.That(await admin.GetStringAsync(Dedup(queue))).IsEqualTo(record);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
            await queue.EnqueueAsync("job", "winner");
            if (command is "hset" or "hdel" or "xdel")
                await Assert.That(async () => await Complete(restricted, Queue(restricted), active)).Throws<RespireServerException>();
            await Assert.That(await Complete(admin, queue, active)).IsEqualTo(1);
            await Promote(admin, queue);
            await Assert.That((await Read(admin, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("winner");
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HostedWorkersRunFollowupAfterActiveHandlerReturns(bool scheduling)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        var state = new HandlerState();
        await using var provider = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state)
            .AddRespireJobQueue<Handler, string>("followup", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ConsumerCount = 2, ReadWait = TimeSpan.FromMilliseconds(10) },
                scheduling ? new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(10) } : null).BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await state.Started.Task.WaitAsync(deadline.Token);
            await queue.EnqueueAsync("job", "superseded");
            await queue.EnqueueAsync("job", "last");
            state.Release.TrySetResult();
            await Assert.That(await state.Followup.Task.WaitAsync(deadline.Token)).IsEqualTo("last");
        }
        finally { state.Release.TrySetResult(); await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task EnqueueRacesHaveOnlyTheTwoAtomicOutcomes()
    {
        await using var client = await Connect();
        await using var rival = await Connect();
        for (var i = 0; i < 12; i++)
        {
            var queue = new RespireJobQueue<string>(client, "race-" + i, Encoding.UTF8.GetBytes);
            var rivalQueue = new RespireJobQueue<string>(rival, "race-" + i, Encoding.UTF8.GetBytes);
            await queue.EnqueueAsync(Mode, "job", "first");
            await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
            var acquiring = Read(client, queue);
            var duplicate = rivalQueue.EnqueueAsync("job", "during acquisition").AsTask();
            await Task.WhenAll(acquiring, duplicate);
            var active = acquiring.Result.Single();
            await Assert.That(active.GetString("_respire.job.payload")).IsEqualTo("first");
            if (duplicate.Result.Enqueued)
            {
                await Complete(client, queue, active);
                await Promote(client, queue);
                active = (await Read(client, queue)).Single();
                await Assert.That(active.GetString("_respire.job.payload")).IsEqualTo("during acquisition");
            }
            var completing = Complete(client, queue, active).AsTask();
            var final = rivalQueue.EnqueueAsync("job", "during completion").AsTask();
            await Task.WhenAll(completing, final);
            await Assert.That(completing.Result).IsEqualTo(1);
            await Promote(client, queue);
            var next = await Read(client, queue);
            await Assert.That(next.Length).IsEqualTo(final.Result.Enqueued ? 1 : 0);
            if (next.Length == 1) await Assert.That(next[0].GetString("_respire.job.payload")).IsEqualTo("during completion");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredHeldFollowupDoesNotRetireTheActiveJob(bool grouped)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueScheduledAsync("job", "expired", new() { TimeToLive = TimeSpan.FromMilliseconds(1), Group = grouped ? "group" : null });
        if (grouped)
        {
            var future = RespireScript.Create(Script("JobQueueControls", "ReadGrouped").Source.Replace("local clock = now()", "local clock = now() + 2000", StringComparison.Ordinal));
            using var ignored = await client.Scripts.ExecuteAsync(future,
                [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey, Lifecycle(queue)], [Group, "owner", 1, 64]);
        }
        else await Promote(client, queue);
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(1);
        await Promote(client, queue);
        await Assert.That((await Read(client, queue, grouped)).Length).IsEqualTo(0);
        using var state = await client.ExecuteAsync("HLEN", [Lifecycle(queue)]);
        await Assert.That(state.AsInteger()).IsEqualTo(0);
    }

    [Test]
    public async Task FailedCompletionAndRecordWriteRestoreHeldFollowup()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "first");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var active = (await Read(client, queue)).Single();
        await queue.EnqueueAsync("job", "winner");
        var record = await client.GetStringAsync(Dedup(queue));
        var enqueue = (RespireScript)typeof(RespireJobQueue<string>).GetField("EnqueueScript", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var broken = RespireScript.Create(enqueue.Source.Replace("deduplicationExpiryMode, deduplicationWindow)", "'PX', 0)", StringComparison.Ordinal));
        await Assert.That(async () =>
        {
            using var ignored = await client.Scripts.ExecuteAsync(broken,
                [queue.StreamKey, Dedup(queue), queue.StreamKey + ":scheduled", queue.StreamKey + ":followups", Lifecycle(queue)],
                ["job", "failed", 60000, "", "", 0, Guid.NewGuid().ToString("N")]);
        }).Throws<RespireServerException>();
        await Assert.That(await client.GetStringAsync(Dedup(queue))).IsEqualTo(record);
        await using (var discarded = client.CreateTransaction()) _ = queue.Enqueue(discarded, "job", "discarded");
        await client.SetAsync(queue.EventsStreamKey, "wrong type");
        await Assert.That(async () => await Complete(client, queue, active)).Throws<RespireServerException>();
        await client.DeleteAsync(queue.EventsStreamKey);
        await Assert.That(await Complete(client, queue, active)).IsEqualTo(1);
        await Promote(client, queue);
        await Assert.That((await Read(client, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("winner");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HeldCandidatesCannotStarveAnotherDueJobInABoundedPage(bool grouped)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(Mode, "job", "active");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        _ = (await Read(client, queue)).Single();
        await queue.EnqueueScheduledAsync("job", "held", new() { Group = grouped ? "group" : null });
        await queue.EnqueueScheduledAsync("unrelated", "eligible", new() { Group = grouped ? "group" : null });
        if (grouped)
        {
            using var first = await client.Scripts.ExecuteAsync(Script("JobQueueControls", "ReadGrouped"),
                [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey, Lifecycle(queue)], [Group, "owner", 1, 1]);
            await Assert.That(first[1].Count).IsEqualTo(0);
            await Assert.That((await Read(client, queue, true)).Single().GetString("_respire.job.payload")).IsEqualTo("eligible");
        }
        else
        {
            RespireKey[] keys = [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due", Lifecycle(queue)];
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("JobQueueScheduling", "Promote"), keys, [1])).IsEqualTo(0);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("JobQueueScheduling", "Promote"), keys, [1])).IsEqualTo(1);
            await Assert.That((await Read(client, queue)).Single().GetString("_respire.job.payload")).IsEqualTo("eligible");
        }
    }

    public sealed class HandlerState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Followup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            if (job.Payload == "first")
            {
                state.Started.TrySetResult();
                await state.Release.Task.WaitAsync(cancellationToken);
            }
            else state.Followup.TrySetResult(job.Payload);
            return RespireStreamWorkerResult.Ack;
        }
    }

    private ValueTask<RespireClient> Connect(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "followup", Encoding.UTF8.GetBytes);
    private static string Lifecycle(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup-deliveries";
    private static string Dedup(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData("job"u8));
    private static ValueTask<long> Expiry(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIRETIME', KEYS[1])"), [Dedup(queue)], []);
    private static Task<RespireStreamEntry[]> Read(IRespireClient client, RespireJobQueue<string> queue, bool grouped = false)
        => JobReplacementIntegrationTests.Read(client, queue, grouped);
    private static async Task Promote(IRespireClient client, RespireJobQueue<string> queue)
    {
        // Advance only the candidate clock, avoiding sleeps for held-candidate rotation.
        var future = RespireScript.Create(Script("JobQueueScheduling", "Promote").Source.Replace("local clock = now()", "local clock = now() + 2000", StringComparison.Ordinal));
        await client.Scripts.ExecuteIntegerAsync(future,
            [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":followups", Lifecycle(queue)], [64, "followups"]);
        await client.Scripts.ExecuteIntegerAsync(future,
            [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due", Lifecycle(queue)], [64]);
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            local members = redis.call('ZRANGE', KEYS[1], 0, -1)
            for _, member in ipairs(members) do
                local prefix, id = string.match(member, '^([^|]+|)%d+|(.+)$')
                redis.call('ZREM', KEYS[1], member)
                redis.call('ZADD', KEYS[1], 0, prefix .. '0000000000000000|' .. id)
            end
            return 1
            """), [queue.StreamKey + ":group-jobs"], []);
    }
    private static ValueTask<long> Complete(IRespireClient client, RespireJobQueue<string> queue, RespireStreamEntry entry, bool grouped = false, string kind = "completed")
        => client.Scripts.ExecuteIntegerAsync(Terminal(Script(grouped ? "JobGroupControls" : "JobQueueScripts", "Complete")),
            grouped ? [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners", Lifecycle(queue)]
                : [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey, Lifecycle(queue)],
            [Group, "owner", entry.Id.Value, 1, kind, "test", ""]);
    private static RespireScript Script(string type, string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming." + type)!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static RespireScript Terminal(RespireScript script, bool expiry = false) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobDeduplication")!.GetMethod("Terminal", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [script, expiry])!;
}
