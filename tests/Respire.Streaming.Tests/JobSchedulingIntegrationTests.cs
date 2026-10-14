using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobSchedulingIntegrationTests(RedisTestContainer redis)
{
    private const string Group = RespireJobQueue<string>.ConsumerGroup;

    [Test]
    public async Task BusyReaderDoesNotPromoteOnEveryIteration()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueAsync("first", "payload");
        var state = new HandlerState();
        await using var provider = Worker(client, state, new() { SchedulingPollInterval = TimeSpan.FromHours(1) });
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Until(() => Task.FromResult(state.Jobs.Count == 1), deadline.Token);
            await queue.EnqueueScheduledAsync("scheduled", "payload", new());
            for (var i = 0; i < 10; i++) await queue.EnqueueAsync("immediate-" + i, "payload");
            await Until(() => Task.FromResult(state.Jobs.Count >= 11), deadline.Token);
            await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(1);
            await Assert.That(state.Jobs.Any(call => call.Job.Id == "scheduled")).IsFalse();
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryAndExpiryRespectGroupAwareDeletion(bool expiry)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var initial = await queue.EnqueueAsync("job", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await client.Streams.CreateGroupAsync(queue.StreamKey, "other", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, "other", "owner");
        var result = await client.Scripts.ExecuteIntegerAsync(Script(expiry ? "CheckExpiry" : "Retry"),
            [.. Keys(queue)], [Group, "owner", initial.EntryId.Value, 1, expiry ? 1 : 0, 1]);
        await Assert.That(result).IsEqualTo(expiry ? -1 : 1);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await client.Scripts.ExecuteIntegerAsync(Script("CheckExpiry"), [queue.StreamKey],
            ["other", "owner", initial.EntryId.Value, 1, 1, 1]);
        var info = await client.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(version >= new Version(8, 2) ? 0 : 1);
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(expiry ? 0 : 1);
    }

    [Test]
    public async Task DeniedDeletionDoesNotStageARetry()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        if (Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]) < new Version(8, 2)) return;
        var queue = Queue(admin);
        var initial = await queue.EnqueueAsync("job", "payload");
        await admin.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        var user = "delete-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-xackdel"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(Script("Retry"),
                [.. Keys(queue)], [Group, "owner", initial.EntryId.Value, 1, 0, 1])).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task MissingStagedBodyDoesNotBlockLaterJobs()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var missing = await queue.EnqueueScheduledAsync("missing", "payload", new());
        await queue.EnqueueScheduledAsync("live", "payload", new());
        using (await client.ExecuteAsync("XDEL", [Keys(queue)[1], missing.EntryId.Value])) { }
        await Assert.That(await Promote(client, queue)).IsEqualTo(1);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("_respire.job.id")).IsEqualTo("live");
        using var due = await client.ExecuteAsync("ZCARD", [Keys(queue)[2]]);
        await Assert.That(due.AsInteger()).IsEqualTo(0);
    }

    [Test]
    public async Task ImmediateOnlyRegistrationNeedsNoSchedulingPermissions()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync("immediate", "payload");
        var user = "immediate-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~" + queue.StreamKey, "~" + queue.DeadLetterStreamKey, "+@all"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            var state = new HandlerState();
            var services = new ServiceCollection().AddSingleton<IRespireClient>(restricted).AddSingleton(state);
            services.AddRespireJobQueue<Handler, string>("schedule", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ReadWait = TimeSpan.FromMilliseconds(10) });
            await using var provider = services.BuildServiceProvider();
            var worker = provider.GetServices<IHostedService>().Single();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StartAsync(deadline.Token);
            try
            {
                await Until(async () => state.Jobs.Count == 1 &&
                    (await admin.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count == 0, deadline.Token);
            }
            finally { await worker.StopAsync(deadline.Token); }
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(2, "tenant:")]
    [Arguments(3, "{tenant}:")]
    public async Task SchedulingPreservesDelayPriorityPayloadAndDeduplication(int protocol, string prefix)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix(prefix);
        var queue = Queue(client);
        var delayed = await queue.EnqueueScheduledAsync("delayed", "later", new() { Delay = TimeSpan.FromHours(1), Priority = 100 });
        await queue.EnqueueScheduledAsync("low", "first", new() { Priority = -1 });
        var high = await queue.EnqueueScheduledAsync("high", "original", new() { Priority = 10 });
        var duplicate = await queue.EnqueueScheduledAsync("high", "replacement", new() { Priority = -100 });
        await Assert.That(duplicate.Enqueued).IsFalse();
        await Assert.That(duplicate.EntryId).IsEqualTo(high.EntryId);
        await Assert.That(await Promote(client, queue)).IsEqualTo(2);
        var ready = await client.Streams.ReadAsync(queue.StreamKey);
        await Assert.That(ready.Select(entry => entry.GetString("_respire.job.id")!).ToArray())
            .IsEquivalentTo(["high", "low"]);
        await Assert.That(ready[0].GetString("_respire.job.id")).IsEqualTo("high");
        await Assert.That(ready[0].GetString("_respire.job.payload")).IsEqualTo("original");
        await Assert.That((await client.Streams.ReadAsync(Keys(queue)[1])).Single().Id).IsEqualTo(delayed.EntryId);
        await Assert.That(await root.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        var keys = Keys(queue).Select(key => client.ResolveKey(key).ToBytes()).ToArray();
        await Assert.That(keys.Select(key => ClusterHash.GetSlot(key)).Distinct().Count()).IsEqualTo(1);
        // Advancing only the test's due score makes the next pass deterministic without a wall-clock sleep.
        using (await root.ExecuteAsync("ZADD", [client.ResolveKey(Keys(queue)[2]).ToBytes(), 0, delayed.EntryId.Value])) { }
        await Assert.That(await Promote(client, queue)).IsEqualTo(1);
        await Assert.That(await Promote(client, queue)).IsEqualTo(0);
    }

    [Test]
    public async Task OldestDueBatchCannotBeStarvedByNewHighPriorityJobs()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var old = await queue.EnqueueScheduledAsync("old", "payload", new() { Priority = int.MinValue });
        using (await client.ExecuteAsync("ZADD", [Keys(queue)[2], 0, old.EntryId.Value])) { }
        await queue.EnqueueScheduledAsync("new", "payload", new() { Priority = int.MaxValue });
        await client.Scripts.ExecuteIntegerAsync(Script("Promote"), [.. Keys(queue)], [1]);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("_respire.job.id")).IsEqualTo("old");
    }

    [Test]
    public async Task FailedDedupWriteRollsBackStagedBodyAndIndex()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var dedup = queue.StreamKey + ":dedup:test";
        await Assert.That(async () =>
        {
            using var result = await client.Scripts.ExecuteAsync(Script("Enqueue"), [.. Keys(queue), dedup],
                ["job", "payload", 0, 0, 0, 0]);
        }).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
        await Assert.That(await Promote(client, queue)).IsEqualTo(0);
        await Assert.That((await queue.EnqueueScheduledAsync("job", "payload", new())).Enqueued).IsTrue();
    }

    [Test]
    [Arguments("xadd")]
    [Arguments("xdel")]
    [Arguments("zrem")]
    public async Task DeniedPromotionLeavesTheScheduledJobRecoverable(string command)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueScheduledAsync("job", "payload", new());
        var user = "scheduler-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Promote(restricted, queue)).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await Assert.That(await Promote(admin, queue)).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SchedulerCrashBeforeOrAfterPromotionCannotLoseOrDuplicateAJob(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new());
        // Fault immediately before the destination write: no source cleanup is allowed.
        var before = RespireScript.Create(Script("Promote").Source.Replace(
            "redis.call('XADD', KEYS[1], '*', unpack(entry.fields))", "error('injected scheduler failure before append')"));
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(before, [.. Keys(queue)], [64]))
            .Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(1);
        // Fault after commit models an unavailable reply. A fresh scheduler must not append again.
        await using (var crashed = await ConnectAsync(protocol))
        {
            var after = RespireScript.Create(Script("Promote").Source.Replace("return promoted", "error('injected lost promotion reply')"));
            await Assert.That(async () => await crashed.Scripts.ExecuteIntegerAsync(after, [.. Keys(queue)], [64]))
                .Throws<RespireServerException>();
        }
        await Assert.That(await Promote(client, queue)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RetryIsFencedAndSurvivesFailuresOnBothSidesOfCommit(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var initial = await queue.EnqueueAsync("job", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        // Force the index command to fail after the staging append, exercising compensation.
        var brokenIndex = RespireScript.Create(Script("Retry").Source.Replace(
            "redis.pcall('ZADD', KEYS[3], due, id)", "redis.pcall('ZADD', KEYS[3], 'not-a-number', id)"));
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(brokenIndex, [.. Keys(queue)],
            [Group, "owner", initial.EntryId.Value, 1, 0]))
            .Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(1);
        using (await client.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, [queue.StreamKey], [Group, "owner", 0, "0-0", 1])) { }
        await Assert.That(await Retry(client, queue, initial.EntryId, 1, 0)).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(StreamWorkerScripts.Ack, [queue.StreamKey],
            [Group, "owner", initial.EntryId.Value, 1])).IsEqualTo(0);
        var after = RespireScript.Create(Script("Retry").Source.Replace("return 1", "error('injected lost retry reply')"));
        await using (var crashed = await ConnectAsync(protocol))
        {
            await Assert.That(async () => await crashed.Scripts.ExecuteIntegerAsync(after, [.. Keys(queue)],
                [Group, "owner", initial.EntryId.Value, 2, 0])).Throws<RespireServerException>();
        }
        await Assert.That(await Retry(client, queue, initial.EntryId, 2, 0)).IsEqualTo(0);
        await Assert.That(await Promote(client, queue)).IsEqualTo(1);
        var next = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner")).Single();
        await Assert.That(next.Id).IsNotEqualTo(initial.EntryId);
        await Assert.That(next.GetString("_respire.job.attempts")).IsEqualTo("2");
        await Assert.That(next.GetString("_respire.job.id")).IsEqualTo("job");
        await Assert.That(await Retry(client, queue, initial.EntryId, 2, 0)).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingAsync(queue.StreamKey, Group)).Single().Id).IsEqualTo(next.Id);
    }

    [Test]
    public async Task LostEnqueueReplyKeepsDeduplicationAndTheScheduledBody()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var dedup = queue.StreamKey + ":dedup:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("job")));
        var after = RespireScript.Create(Script("Enqueue").Source.Replace("return {id, 1}", "error('injected lost enqueue reply')"));
        await Assert.That(async () =>
        {
            using var result = await client.Scripts.ExecuteAsync(after, [.. Keys(queue), dedup],
                ["job", "original", 60000, 0, 0, 0]);
        }).Throws<RespireServerException>();
        var repeated = await queue.EnqueueScheduledAsync("job", "replacement", new());
        await Assert.That(repeated.Enqueued).IsFalse();
        await Assert.That((await queue.EnqueueAsync("job", "immediate replacement")).Enqueued).IsFalse();
        await Assert.That(await Promote(client, queue)).IsEqualTo(1);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("_respire.job.payload"))
            .IsEqualTo("original");
    }

    [Test]
    [Arguments("xack")]
    [Arguments("zadd")]
    [Arguments("xdel")]
    public async Task DeniedRetryCannotStageAnUnacknowledgedDuplicate(string command)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var enqueued = await queue.EnqueueAsync("job", "payload");
        await admin.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        var user = "retry-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Retry(restricted, queue, enqueued.EntryId, 1, 0))
                .Throws<RespireServerException>();
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
            await Assert.That(await Retry(admin, queue, enqueued.EntryId, 1, 0)).IsEqualTo(1);
            await Assert.That(await Promote(admin, queue)).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task RetryDiscardsExpiredAttemptsAndRetainsAbsoluteExpiry()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        // Drive the retry check with a known expired deadline.
        var id = await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "expired"),
            ("_respire.job.payload", "payload"), ("_respire.job.expires", "1"));
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        await Assert.That(await Retry(client, queue, id, 1, 0)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);

        await queue.EnqueueScheduledAsync("later", "payload", new() { TimeToLive = TimeSpan.FromMinutes(1) });
        var staged = (await client.Streams.ReadAsync(Keys(queue)[1])).Single();
        await Promote(client, queue);
        var delivery = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner")).Single();
        await Retry(client, queue, delivery.Id, 1, 120000);
        var retried = (await client.Streams.ReadAsync(Keys(queue)[1])).Single();
        await Assert.That(retried.GetString("_respire.job.expires")).IsEqualTo(staged.GetString("_respire.job.expires"));
        await Assert.That(retried.GetString("_respire.job.attempts")).IsEqualTo("1");
    }

    [Test]
    public async Task ExpiryBeforePromotionAndAfterReadSkipsHandlers()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("expired-before-promotion", "payload", new() { TimeToLive = TimeSpan.FromMilliseconds(1) });
        var staged = (await client.Streams.ReadAsync(Keys(queue)[1])).Single();
        await WaitForExpiry(client, long.Parse(staged.GetString("_respire.job.expires")!));
        await Assert.That(await Promote(client, queue)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(Keys(queue)[1])).IsEqualTo(0);
        // A body that expires while waiting in the ready stream must be checked at dispatch too.
        await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "expired-ready"),
            ("_respire.job.payload", "payload"), ("_respire.job.expires", "1"));
        await queue.EnqueueAsync("live", "payload");
        var state = new HandlerState();
        await using var provider = Worker(client, state);
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Until(async () => state.Jobs.Count == 1 &&
                (await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count == 0, deadline.Token);
            await Assert.That(state.Jobs.Single().Job.Id).IsEqualTo("live");
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HostedRetryUsesBackoffOrExplicitDelayAndKeepsTheTotalDeliveryLimit(bool explicitDelay)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueAsync("retry", "payload");
        var state = new HandlerState
        {
            Handle = job => explicitDelay ? job.RetryAfter(TimeSpan.FromMilliseconds(80)) : RespireStreamWorkerResult.Nack,
        };
        await using var provider = Worker(client, state, new()
        {
            RetryBackoff = TimeSpan.FromMilliseconds(40), MaximumRetryBackoff = TimeSpan.FromMilliseconds(60),
            SchedulingPollInterval = TimeSpan.FromMilliseconds(5),
        });
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Until(async () => await client.Streams.CountAsync(queue.DeadLetterStreamKey) == 1, deadline.Token);
            var calls = state.Jobs.ToArray();
            await Assert.That(calls.Length).IsEqualTo(3);
            await Assert.That(calls.Select(call => call.Job.EntryId).Distinct().Count()).IsEqualTo(3);
            // Redis millisecond rounding permits at most one millisecond of clock quantization.
            await Assert.That(Stopwatch.GetElapsedTime(calls[0].Timestamp, calls[1].Timestamp).TotalMilliseconds)
                .IsGreaterThanOrEqualTo(explicitDelay ? 79 : 39);
            await Assert.That(Stopwatch.GetElapsedTime(calls[1].Timestamp, calls[2].Timestamp).TotalMilliseconds)
                .IsGreaterThanOrEqualTo(explicitDelay ? 79 : 59);
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
            var dead = (await client.Streams.ReadAsync(queue.DeadLetterStreamKey)).Single();
            await Assert.That(dead.GetString("_respire.job.attempts")).IsEqualTo("2");
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "schedule", Encoding.UTF8.GetBytes);
    private static string[] Keys(RespireJobQueue<string> queue) => [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due"];
    internal static RespireScript Script(string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobQueueScheduling")!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static ValueTask<long> Promote(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(Script("Promote"), [.. Keys(queue)], [64]);
    private static ValueTask<long> Retry(IRespireClient client, RespireJobQueue<string> queue, RespireStreamId id, long attempt, RespireValue delay)
        => client.Scripts.ExecuteIntegerAsync(Script("Retry"), [.. Keys(queue)], [Group, "owner", id.Value, attempt, delay]);

    private static async Task WaitForExpiry(IRespireClient client, long expires)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Until(async () =>
        {
            using var time = await client.ExecuteAsync("TIME");
            return long.Parse(time[0].AsString()) * 1000 + long.Parse(time[1].AsString()) / 1000 >= expires;
        }, deadline.Token);
    }

    private static async Task Until(Func<Task<bool>> predicate, CancellationToken token)
    {
        while (!await predicate()) await Task.Delay(10, token);
    }

    private static ServiceProvider Worker(IRespireClient client, HandlerState state, RespireJobQueueOptions? queueOptions = null)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("schedule", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(10), MinimumIdleTime = TimeSpan.FromSeconds(10),
                RecoveryPollInterval = TimeSpan.FromMilliseconds(10), DeliveryLimit = 3 }, queueOptions ?? new());
        return services.BuildServiceProvider();
    }

    public sealed class HandlerState
    {
        public ConcurrentQueue<(RespireJob<string> Job, long Timestamp)> Jobs { get; } = new();
        public Func<RespireJob<string>, RespireStreamWorkerResult> Handle { get; init; } = _ => RespireStreamWorkerResult.Ack;
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            state.Jobs.Enqueue((job, Stopwatch.GetTimestamp()));
            return ValueTask.FromResult(state.Handle(job));
        }
    }
}
