using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RecurringJobIntegrationTests(RedisTestContainer redis)
{
    [Test]
    public async Task HostedSchedulerDeliversTypedJobsAndStopsItsPollingDelay()
    {
        await using var client = await ConnectAsync();
        var clock = new ManualClock();
        var received = new HostedJobState();
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client)
            .AddSingleton<TimeProvider>(clock).AddSingleton(received);
        services.AddLogging();
        services.AddRespireJobQueue<HostedJobHandler, string>("recurring", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(50) });
        // A long delay makes StopAsync exercise cancellation instead of merely waiting for another pass.
        services.AddRespireJobScheduler<string>("recurring", TimeSpan.FromHours(1));
        await using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredKeyedService<RespireJobQueue<string>>("recurring");
        var scheduler = provider.GetRequiredKeyedService<RespireJobScheduler<string>>("recurring");
        await scheduler.UpsertAsync("hosted", "typed payload", new() { Interval = TimeSpan.FromMinutes(1) });
        clock.Advance(TimeSpan.FromMinutes(1));
        var hosted = provider.GetServices<IHostedService>().ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            foreach (var service in hosted) await service.StartAsync(deadline.Token);
            var job = await received.Job.Task.WaitAsync(deadline.Token);
            await Assert.That(job.Payload).IsEqualTo("typed payload");
            await using var events = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
            await Assert.That(await events.MoveNextAsync()).IsTrue();
            await Assert.That(events.Current.Kind).IsEqualTo("completed");
            await Assert.That(events.Current.JobId).IsEqualTo(job.Id);
            await Assert.That(events.Current.EntryId).IsEqualTo(job.EntryId);
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(0);
        }
        finally
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var service in hosted.Reverse()) await service.StopAsync(stopping.Token);
        }
        var background = hosted.Cast<BackgroundService>().ToArray();
        await Assert.That(background.Length).IsEqualTo(2);
        await Assert.That(background.All(service => service.ExecuteTask is { IsCompleted: true })).IsTrue();
        // Cancellation stops scheduling without deleting its persisted definition or the caller's client.
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task IndependentSchedulersEnqueueEachTickOnlyOnce(int protocol)
    {
        await using var first = await ConnectAsync(protocol);
        await using var second = await ConnectAsync(protocol);
        var clock = new ManualClock();
        var queue = Queue(first);
        var schedulers = new[] { queue.CreateScheduler(clock), Queue(second).CreateScheduler(clock) };
        await schedulers[0].UpsertAsync("report", "payload", new()
        {
            Interval = TimeSpan.FromSeconds(1), MissedTickPolicy = RespireJobMissedTickPolicy.CatchUp,
        });
        clock.Advance(TimeSpan.FromSeconds(10));
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => schedulers[index % 2].RunDueAsync().AsTask()));
        await Assert.That(results.Sum()).IsEqualTo(10);
        var entries = await first.Streams.ReadAsync(queue.StreamKey);
        await Assert.That(entries.Length).IsEqualTo(10);
        await Assert.That(entries.Select(entry => entry.GetString("_respire.job.id")).Distinct().Count()).IsEqualTo(10);
        await Assert.That(entries.Select(entry => entry.GetString("_respire.job.tick")).Distinct().Count()).IsEqualTo(10);
        await Assert.That(entries.All(entry => entry.GetString("_respire.job.schedule") == "report"
            && entry.GetString("_respire.job.payload") == "payload")).IsTrue();
        await Assert.That(await schedulers[0].RunDueAsync()).IsEqualTo(0);
        await Assert.That(await schedulers[1].RunDueAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task IntervalBoundaryAndSkippedTicksPreserveCadence()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        clock.Advance(TimeSpan.FromMilliseconds(3500));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        var ticks = (await client.Streams.ReadAsync(queue.StreamKey)).Select(Tick).ToArray();
        await Assert.That(ticks).IsEquivalentTo(new[] { start.AddSeconds(1).ToUnixTimeMilliseconds(), start.AddSeconds(4).ToUnixTimeMilliseconds() });
        clock.Advance(TimeSpan.FromMilliseconds(499));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await Assert.That(Tick((await client.Streams.ReadAsync(queue.StreamKey))[^1])).IsEqualTo(start.AddSeconds(5).ToUnixTimeMilliseconds());
    }

    [Test]
    public async Task CatchUpIsBoundedAndResumesOldestTicksAfterClientRestart()
    {
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        await using (var original = await ConnectAsync())
        {
            await Queue(original).CreateScheduler(clock).UpsertAsync("report", "persisted", new()
            {
                Interval = TimeSpan.FromSeconds(1), MissedTickPolicy = RespireJobMissedTickPolicy.CatchUp,
            });
        }
        clock.Advance(TimeSpan.FromSeconds(5));
        await using (var restarted = await ConnectAsync())
        {
            var queue = Queue(restarted);
            await Assert.That(await queue.CreateScheduler(clock).RunDueAsync(2)).IsEqualTo(2);
            var entries = await restarted.Streams.ReadAsync(queue.StreamKey);
            await Assert.That(Tick(entries[0])).IsEqualTo(start.AddSeconds(1).ToUnixTimeMilliseconds());
            await Assert.That(Tick(entries[1])).IsEqualTo(start.AddSeconds(2).ToUnixTimeMilliseconds());
        }
        await using var recovered = await ConnectAsync();
        var recoveredQueue = Queue(recovered);
        var scheduler = recoveredQueue.CreateScheduler(clock);
        await Assert.That(await scheduler.RunDueAsync(2)).IsEqualTo(2);
        await Assert.That(await scheduler.RunDueAsync(2)).IsEqualTo(1);
        await Assert.That(await scheduler.RunDueAsync(2)).IsEqualTo(0);
        var all = await recovered.Streams.ReadAsync(recoveredQueue.StreamKey);
        await Assert.That(all.Length).IsEqualTo(5);
        for (var index = 0; index < all.Length; index++)
        {
            await Assert.That(Tick(all[index])).IsEqualTo(start.AddSeconds(index + 1).ToUnixTimeMilliseconds());
            await Assert.That(all[index].GetString("_respire.job.payload")).IsEqualTo("persisted");
        }
    }

    [Test]
    [Arguments(RespireJobMissedTickPolicy.Skip)]
    [Arguments(RespireJobMissedTickPolicy.CatchUp)]
    public async Task CronMissedTicksFollowTheSelectedPolicy(RespireJobMissedTickPolicy policy)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(30));
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("minute", "payload", new() { Cron = "* * * * *", MissedTickPolicy = policy });
        clock.Advance(TimeSpan.FromSeconds(29));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(151));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(policy == RespireJobMissedTickPolicy.Skip ? 1 : 3);
        var ticks = (await client.Streams.ReadAsync(queue.StreamKey)).Select(Tick).ToArray();
        var expected = policy == RespireJobMissedTickPolicy.Skip
            ? new[] { start.AddMinutes(3).ToUnixTimeMilliseconds() }
            : Enumerable.Range(1, 3).Select(minute => start.AddMinutes(minute).ToUnixTimeMilliseconds()).ToArray();
        await Assert.That(ticks).IsEquivalentTo(expected);
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task CronRangesStepsAndWeekdaysUseUtc()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 16, 10, 45, 0, TimeSpan.Zero));
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("weekday", "payload", new() { Cron = "*/15 9-10 * * 1-5" });
        // Friday's last occurrence is excluded because upsert starts strictly after now.
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(22) + TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        var entry = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(Tick(entry)).IsEqualTo(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Test]
    public async Task ReplacementAndRemovalTakeEffectForExistingSchedulers()
    {
        await using var client = await ConnectAsync();
        await using var editor = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        var editing = Queue(editor).CreateScheduler(clock);
        await editing.UpsertAsync("report", "old", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await editing.UpsertAsync("report", "new", new() { Interval = TimeSpan.FromSeconds(2) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        var entries = await client.Streams.ReadAsync(queue.StreamKey);
        await Assert.That(entries[0].GetString("_respire.job.payload")).IsEqualTo("old");
        await Assert.That(entries[1].GetString("_respire.job.payload")).IsEqualTo("new");
        await Assert.That(entries[0].GetString("_respire.job.id")).IsNotEqualTo(entries[1].GetString("_respire.job.id"));
        await Assert.That(await editing.RemoveAsync("report")).IsTrue();
        await Assert.That(await editing.RemoveAsync("report")).IsFalse();
        clock.Advance(TimeSpan.FromDays(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        await Assert.That(await client.Hashes.CountAsync(queue.StreamKey + ":recurring")).IsEqualTo(0);
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":recurring-due")).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
    }

    [Test]
    public async Task PrefixedQueuesAndScheduleIdsAreIsolated()
    {
        await using var root = await ConnectAsync();
        var first = root.WithKeyPrefix("{tenant-a}:");
        var second = root.WithKeyPrefix("{tenant-b}:");
        var clock = new ManualClock();
        var firstQueue = Queue(first);
        var secondQueue = Queue(second);
        await firstQueue.CreateScheduler(clock).UpsertAsync("shared", "first", new() { Interval = TimeSpan.FromSeconds(1) });
        await secondQueue.CreateScheduler(clock).UpsertAsync("shared", "second", new() { Interval = TimeSpan.FromSeconds(2) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await firstQueue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(1);
        await Assert.That(await secondQueue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await secondQueue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(1);
        await Assert.That((await first.Streams.ReadAsync(firstQueue.StreamKey)).Single().GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That((await second.Streams.ReadAsync(secondQueue.StreamKey)).Single().GetString("_respire.job.payload")).IsEqualTo("second");
        await Assert.That(await root.Streams.CountAsync(firstQueue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    public async Task FailedAppendRetainsDueTickForRetry()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.Strings.SetAsync(queue.StreamKey, "wrong type");
        await Assert.That(async () => await scheduler.RunDueAsync()).Throws<RespireServerException>();
        await client.Keys.DeleteAsync(queue.StreamKey);
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
    }

    [Test]
    [Arguments("xadd")]
    [Arguments("zadd")]
    public async Task DeniedTickWriteDoesNotAppendOrAdvanceTheSchedule(string deniedCommand)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var clock = new ManualClock();
        await queue.CreateScheduler(clock).UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        var user = "recurring-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + deniedCommand]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Queue(restricted).CreateScheduler(clock).RunDueAsync()).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await Assert.That(await queue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(1);
            await Assert.That(await queue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(0);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task InvalidDueIndexDoesNotPersistAnUnindexedSchedule()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await client.Strings.SetAsync(queue.StreamKey + ":recurring-due", "wrong type");
        await Assert.That(async () => await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) }))
            .Throws<RespireServerException>();
        await Assert.That(await client.Hashes.CountAsync(queue.StreamKey + ":recurring")).IsEqualTo(0);
        await client.Keys.DeleteAsync(queue.StreamKey + ":recurring-due");
        await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StaleTickCannotCommitAfterReplacementOrRemoval(bool remove)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "old", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        using var due = await client.Scripts.ExecuteAsync(Script("ReadDue"), [.. Keys(queue)], [clock.GetUtcNow().ToUnixTimeMilliseconds(), 1]);
        var definition = due[0][1].AsString();
        var tick = due[0][2].AsString();
        if (remove) await scheduler.RemoveAsync("report");
        else await scheduler.UpsertAsync("report", "replacement", new() { Interval = TimeSpan.FromSeconds(1) });
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("CommitTick"), [.. Keys(queue)],
            ["report", definition, tick, clock.GetUtcNow().AddSeconds(1).ToUnixTimeMilliseconds(), "stale", "old", tick])).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(remove ? 0 : 1);
        if (!remove)
            await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("_respire.job.payload")).IsEqualTo("replacement");
    }

    [Test]
    public async Task LostCommitReplyDoesNotDuplicateATickOnReplay()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        using var due = await client.Scripts.ExecuteAsync(Script("ReadDue"), [.. Keys(queue)], [clock.GetUtcNow().ToUnixTimeMilliseconds(), 1]);
        var definition = due[0][1].AsString();
        var tick = due[0][2].AsString();
        // The script commits, then produces an error instead of its success reply.
        var lostReply = RespireScript.Create(Script("CommitTick").Source.Replace("return 1", "error('injected lost commit reply')"));
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(lostReply, [.. Keys(queue)],
            ["report", definition, tick, clock.GetUtcNow().AddSeconds(1).ToUnixTimeMilliseconds(), "stable-job-id", "payload", tick]))
            .Throws<RespireServerException>();
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("CommitTick"), [.. Keys(queue)],
            ["report", definition, tick, clock.GetUtcNow().AddSeconds(1).ToUnixTimeMilliseconds(), "stable-job-id", "payload", tick])).IsEqualTo(0);
        await using var restarted = await ConnectAsync();
        await Assert.That(await Queue(restarted).CreateScheduler(clock).RunDueAsync()).IsEqualTo(0);
        var entry = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(entry.GetString("_respire.job.id")).IsEqualTo("stable-job-id");
    }

    [Test]
    public async Task ReplayedUpsertDoesNotRewindARevisionAfterItsTickCommits()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var clock = new ManualClock();
        var scheduler = queue.CreateScheduler(clock);
        await scheduler.UpsertAsync("report", "payload", new() { Interval = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        using var due = await client.Scripts.ExecuteAsync(Script("ReadDue"), [.. Keys(queue)],
            [clock.GetUtcNow().ToUnixTimeMilliseconds(), 1]);
        var definition = due[0][1].AsString();
        var tick = due[0][2].AsString();
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
        await client.Scripts.ExecuteIntegerAsync(Script("Upsert"), [.. Keys(queue)], ["report", definition, tick]);
        await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
    }

    private static string[] Keys(RespireJobQueue<string> queue)
        => [queue.StreamKey, queue.StreamKey + ":recurring", queue.StreamKey + ":recurring-due"];

    private static RespireScript Script(string name)
        => (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.RecurringJobScripts")!
            .GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;

    private ValueTask<RespireClient> ConnectAsync(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespProtocol)protocol });

    private static RespireJobQueue<string> Queue(IRespireClient client)
        => new(client, "recurring", Encoding.UTF8.GetBytes);

    private static long Tick(RespireStreamEntry entry)
        => long.Parse(entry.GetString("_respire.job.tick")!, CultureInfo.InvariantCulture);

    private sealed class ManualClock(DateTimeOffset? initial = null) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial ?? new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }

    public sealed class HostedJobState
    {
        public TaskCompletionSource<RespireJob<string>> Job { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class HostedJobHandler(HostedJobState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
        {
            state.Job.TrySetResult(message);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }
}
