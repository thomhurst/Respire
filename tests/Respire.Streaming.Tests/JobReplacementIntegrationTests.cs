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
public class JobReplacementIntegrationTests(RedisTestContainer redis)
{
    private const string Group = RespireJobQueue<string>.ConsumerGroup;
    private const string Name = "replacement";

    [Test]
    [Arguments(RespireJobDeduplicationMode.FirstSubmissionWins)]
    [Arguments(RespireJobDeduplicationMode.Extend)]
    public async Task ReplaceCannotChangeAnExistingGenerationPolicy(RespireJobDeduplicationMode mode)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var first = await queue.EnqueueAsync(mode, "job", "first");
        var duplicate = await queue.EnqueueScheduledAsync("job", "replacement", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Replace, Group = "group",
        });
        await Assert.That(duplicate).IsEqualTo(new RespireJobEnqueueResult(first.EntryId, false));
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That(await Lifecycle(client, queue)).IsNull();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false, 2)]
    [Arguments(false, true, 3)]
    [Arguments(true, false, 3)]
    [Arguments(true, true, 2)]
    public async Task LastSubmissionReplacesTheEntireEnvelopeAcrossApis(bool scheduled, bool transactional, int protocol)
    {
        await using var root = await Connect(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "first", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Replace, Delay = TimeSpan.FromHours(1),
            Group = "old", Priority = -10, TimeToLive = TimeSpan.FromHours(2), MaximumRuntime = TimeSpan.FromMinutes(1),
        });
        var original = (await client.Streams.ReadAsync(queue.StreamKey + ":scheduled")).Single();
        var expiry = await Expiry(client, queue);
        var options = new RespireJobEnqueueOptions
        {
            Delay = TimeSpan.FromMinutes(5), Group = "new", Priority = 99,
            TimeToLive = TimeSpan.FromMinutes(10), MaximumRuntime = TimeSpan.FromSeconds(2),
            DeduplicationWindow = TimeSpan.FromMilliseconds(1),
        };
        RespireJobEnqueueResult replaced;
        if (transactional)
        {
            await using var transaction = client.CreateTransaction();
            var pending = scheduled ? queue.EnqueueScheduled(transaction, "job", "last", options)
                : queue.Enqueue(transaction, "job", "last");
            await transaction.CommitAsync();
            replaced = pending.Result;
        }
        else replaced = scheduled ? await queue.EnqueueScheduledAsync("job", "last", options)
            : await queue.EnqueueAsync("job", "last");
        await Assert.That(replaced.Enqueued).IsTrue();
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        var stream = queue.StreamKey + (scheduled ? ":scheduled" : "");
        var last = (await client.Streams.ReadAsync(stream)).Single(entry => entry.Id == replaced.EntryId);
        await Assert.That(last.GetString("_respire.job.payload")).IsEqualTo("last");
        await Assert.That(last.GetString("_respire.job.deduplication")).IsEqualTo("Replace");
        await Assert.That(last.GetString("_respire.job.generation")).IsEqualTo(original.GetString("_respire.job.generation"));
        await Assert.That(last.GetString("_respire.job.revision")).IsNotEqualTo(original.GetString("_respire.job.revision"));
        await Assert.That(last.GetString("_respire.job.group")).IsEqualTo(scheduled ? "new" : null);
        await Assert.That(last.GetString("_respire.job.priority")).IsEqualTo(scheduled ? "99" : null);
        await Assert.That(last.GetString("_respire.job.runtime")).IsEqualTo(scheduled ? "2000" : null);
        await Assert.That(last.GetString("_respire.job.checkpoint")).IsNotEqualTo(original.GetString("_respire.job.checkpoint"));
        if (scheduled)
        {
            var expires = long.Parse(last.GetString("_respire.job.expires")!);
            var originalExpiry = long.Parse(original.GetString("_respire.job.expires")!);
            await Assert.That(expires).IsLessThan(originalExpiry);
            using var due = await client.Scripts.ExecuteAsync(RespireScript.Create("return redis.call('ZRANGE', KEYS[1], 0, -1)"),
                [queue.StreamKey + ":group-jobs"], []);
            await Assert.That(Enumerable.Range(0, due.Count).Any(i => due[i].AsString().StartsWith("6e6577|", StringComparison.Ordinal)
                && due[i].AsString().EndsWith("|" + replaced.EntryId.Value, StringComparison.Ordinal))).IsTrue();
        }
        await Assert.That(await root.Streams.CountAsync(stream)).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentDuplicatesAndAcquisitionChooseOneImmutableWinner(int protocol)
    {
        await using var client = await Connect(protocol);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "first");
        await CreateGroup(client, queue);
        var duplicates = Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => queue.EnqueueAsync("job", "payload-" + i).AsTask()));
        var read = Read(client, queue);
        await Task.WhenAll(duplicates, read);
        var accepted = new List<RespireStreamEntry>(await read);
        // Bounded drains use known submitted-entry counts, without wall-clock polling.
        for (var i = 0; i < 25; i++) accepted.AddRange(await Read(client, queue));
        await Assert.That(accepted.Count).IsEqualTo(1);
        var winner = accepted.Single();
        var state = await Lifecycle(client, queue);
        await Assert.That(state).IsEqualTo(winner.GetString("_respire.job.revision") + "|active");
        var duplicate = await queue.EnqueueScheduledAsync("job", "too late", new() { Group = "changed" });
        await Assert.That(duplicate.Enqueued).IsFalse();
        await Assert.That(duplicate.EntryId).IsEqualTo(winner.Id);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PromotedAndGroupedReplacementsDiscardOldEntriesBeforeAcquisition(bool grouped)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "old", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Replace,
            Group = grouped ? "old" : null,
        });
        if (!grouped) await Promote(client, queue);
        await queue.EnqueueScheduledAsync("job", "last", new() { Group = grouped ? "new" : null });
        await Promote(client, queue);
        await CreateGroup(client, queue);
        var entries = new List<RespireStreamEntry>();
        for (var i = 0; i < 5; i++) entries.AddRange(await Read(client, queue, grouped));
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].GetString("_respire.job.payload")).IsEqualTo("last");
        await Assert.That(entries[0].GetString("_respire.job.group")).IsEqualTo(grouped ? "new" : null);
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":group-jobs")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquisitionFreezesRetriesAndCompletionUntilTheWindowExpires(bool grouped)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "original", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Replace, Group = grouped ? "group" : null,
        });
        await Promote(client, queue);
        await CreateGroup(client, queue);
        var first = (await Read(client, queue, grouped)).Single();
        var expiry = await Expiry(client, queue);
        await Assert.That((await queue.EnqueueAsync("job", "active duplicate")).Enqueued).IsFalse();
        // The same durable state survives retry staging and promotion; retry payload/backoff stay intact.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("JobQueueScheduling", "Retry"),
            JobGroupIntegrationTests.Keys(queue), [Group, "owner", first.Id.Value, 1, 60000, 0])).IsEqualTo(1);
        await Assert.That((await queue.EnqueueScheduledAsync("job", "retry duplicate", new())).Enqueued).IsFalse();
        if (!grouped)
            using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":due", 0,
                (await client.Streams.ReadAsync(queue.StreamKey + ":scheduled")).Single().Id.Value])) { }
        if (grouped)
        {
            // Group retries use their group index; move its single member's due time forward deterministically.
            using var members = await client.ExecuteAsync("ZRANGE", [queue.StreamKey + ":group-jobs", 0, -1]);
            var member = members[0].AsString();
            using (await client.ExecuteAsync("ZREM", [queue.StreamKey + ":group-jobs", member])) { }
            var parts = member.Split('|');
            using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":group-jobs", 0, parts[0] + "|0000000000000000|" + parts[2]])) { }
            await client.Scripts.ExecuteIntegerAsync(Script("JobGroupControls", "Release"), JobGroupIntegrationTests.Keys(queue),
                ["owner", first.Id.Value, 1]);
        }
        await Promote(client, queue);
        var retry = (await Read(client, queue, grouped)).Single();
        await Assert.That(retry.GetString("_respire.job.payload")).IsEqualTo("original");
        await Assert.That(retry.GetString("_respire.job.attempts")).IsEqualTo("1");
        var complete = Terminal(Script(grouped ? "JobGroupControls" : "JobQueueScripts", "Complete"));
        RespireKey[] completionKeys = grouped
            ? [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners", LifecycleKey(queue)]
            : [queue.StreamKey, queue.EventsStreamKey, LifecycleKey(queue)];
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(complete,
            completionKeys,
            [Group, "owner", retry.Id.Value, 1, "completed", "", ""])).IsEqualTo(1);
        await Assert.That(await Lifecycle(client, queue)).IsNull();
        await Assert.That((await queue.EnqueueAsync("job", "completed duplicate")).Enqueued).IsFalse();
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIREAT', KEYS[1], 1)"), [DedupKey(queue)], []);
        await Assert.That((await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "new generation")).Enqueued).IsTrue();
    }

    [Test]
    public async Task WindowExpiryDoesNotFenceAnEarlierPendingGeneration()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "old generation");
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIREAT', KEYS[1], 1)"), [DedupKey(queue)], []);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "new generation");
        await CreateGroup(client, queue);
        var first = (await Read(client, queue)).Single();
        var second = (await Read(client, queue)).Single();
        await Assert.That(first.GetString("_respire.job.payload")).IsEqualTo("old generation");
        await Assert.That(second.GetString("_respire.job.payload")).IsEqualTo("new generation");
        await Assert.That(first.GetString("_respire.job.generation")).IsNotEqualTo(second.GetString("_respire.job.generation"));
    }

    [Test]
    [Arguments("hset", false)]
    [Arguments("hdel", true)]
    [Arguments("set", false)]
    [Arguments("zadd", true)]
    public async Task DeniedReplacementWritesPreserveTheWinningEnvelope(string command, bool scheduled)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "original");
        var state = await Lifecycle(admin, queue);
        var dedup = await admin.GetStringAsync(DedupKey(queue));
        var expiry = await Expiry(admin, queue);
        var user = "replacement-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () =>
            {
                if (scheduled) await Queue(restricted).EnqueueScheduledAsync("job", "denied", new() { Group = "new" });
                else await Queue(restricted).EnqueueAsync("job", "denied");
            }).Throws<RespireServerException>();
            await Assert.That(await Lifecycle(admin, queue)).IsEqualTo(state);
            await Assert.That(await admin.GetStringAsync(DedupKey(queue))).IsEqualTo(dedup);
            await Assert.That(await Expiry(admin, queue)).IsEqualTo(expiry);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedRecordWriteRestoresPreviousRevisionAndTransactionDiscardDoesNothing(bool scheduled)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "original");
        var state = await Lifecycle(client, queue);
        var dedup = await client.GetStringAsync(DedupKey(queue));
        await using (var transaction = client.CreateTransaction())
            _ = queue.EnqueueScheduled(transaction, "job", "discarded", new());
        await Assert.That(await Lifecycle(client, queue)).IsEqualTo(state);
        var script = scheduled ? Script("JobQueueScheduling", "Enqueue")
            : (RespireScript)typeof(RespireJobQueue<string>).GetField("EnqueueScript", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var broken = RespireScript.Create(script.Source.Replace("deduplicationExpiryMode, deduplicationWindow)", "'PX', 0)", StringComparison.Ordinal));
        RespireKey[] keys = scheduled
            ? [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due", DedupKey(queue), LifecycleKey(queue)]
            : [queue.StreamKey, DedupKey(queue), LifecycleKey(queue)];
        RespireValue[] args = scheduled
            ? ["job", "failed", 60000, 0, 0, 0, "", "", "", 0, "", 0, Guid.NewGuid().ToString("N")]
            : ["job", "failed", 60000, "", "", 0, Guid.NewGuid().ToString("N")];
        await Assert.That(async () => { using var result = await client.Scripts.ExecuteAsync(broken, keys, args); })
            .Throws<RespireServerException>();
        await Assert.That(await Lifecycle(client, queue)).IsEqualTo(state);
        await Assert.That(await client.GetStringAsync(DedupKey(queue))).IsEqualTo(dedup);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task HostedWorkerProcessesOnlyTheReplacementAndRetiresLifecycle(bool scheduling, bool grouped)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "superseded");
        if (grouped) await queue.EnqueueScheduledAsync("job", "winner", new() { Group = "group", MaximumRuntime = TimeSpan.FromSeconds(10) });
        else await queue.EnqueueAsync("job", "winner");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(received)
            .AddRespireJobQueue<Handler, string>(Name, Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                new() { ReadWait = TimeSpan.FromMilliseconds(10) },
                scheduling ? new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(10) } : null).BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Assert.That(await received.Task.WaitAsync(deadline.Token)).IsEqualTo("winner");
            await worker.StopAsync(deadline.Token);
            await Assert.That(await Lifecycle(client, queue)).IsNull();
            await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Test]
    [Arguments("promotion")]
    [Arguments("acquisition")]
    [Arguments("retry")]
    public async Task ExpiredEnvelopesRetireTheGenerationWithoutResurrection(string stage)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "expires", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Replace, TimeToLive = TimeSpan.FromHours(1),
        });
        if (stage != "promotion")
        {
            await Promote(client, queue);
            await CreateGroup(client, queue);
            var delivery = (await Read(client, queue)).Single();
            if (stage == "retry")
            {
                var future = RespireScript.Create(Script("JobQueueScheduling", "Retry").Source.Replace(
                    "local clock = now()", "local clock = now() + 7200000", StringComparison.Ordinal));
                await Assert.That(await client.Scripts.ExecuteIntegerAsync(future,
                    [.. JobGroupIntegrationTests.Keys(queue), LifecycleKey(queue)],
                    [Group, "owner", delivery.Id.Value, 1, 0, 0])).IsEqualTo(1);
            }
            else
                await Assert.That(await client.Scripts.ExecuteIntegerAsync(Terminal(Script("JobQueueScheduling", "CheckExpiry"), true),
                    [queue.StreamKey, LifecycleKey(queue)], [Group, "owner", delivery.Id.Value, 1, 1, 0])).IsEqualTo(-1);
        }
        else
        {
            var future = RespireScript.Create(Script("JobQueueScheduling", "Promote").Source.Replace(
                "local clock = now()", "local clock = now() + 7200000", StringComparison.Ordinal));
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(future,
                [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due", LifecycleKey(queue)], [64])).IsEqualTo(0);
        }
        await Assert.That(await Lifecycle(client, queue)).IsNull();
        await Assert.That((await queue.EnqueueAsync("job", "after expiration")).Enqueued).IsFalse();
    }

    [Test]
    [Arguments("Replay")]
    [Arguments("Claim")]
    public async Task RecoveryRejectsAnEnvelopeReplacedAfterAnIncompleteRead(string operation)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var first = await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "superseded");
        await CreateGroup(client, queue);
        // Model a reader failing after XREADGROUP but before its lifecycle write/return.
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        await queue.EnqueueAsync("job", "replacement");
        RespireValue[] args = operation == "Replay" ? [Group, "owner", 1, "0-0"]
            : [Group, "recovery", 0, "0-0", 1];
        using var reply = await client.Scripts.ExecuteAsync(Script("JobQueueControls", operation),
            [queue.StreamKey, queue.ControlKey, LifecycleKey(queue)], args);
        await Assert.That(reply[1].Count).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
        var accepted = (await Read(client, queue)).Single();
        await Assert.That(accepted.Id).IsNotEqualTo(first.EntryId);
        await Assert.That(accepted.GetString("_respire.job.payload")).IsEqualTo("replacement");
    }

    public sealed class Handler(TaskCompletionSource<string> received) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            received.TrySetResult(job.Payload);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    private ValueTask<RespireClient> Connect(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, Name, Encoding.UTF8.GetBytes);
    private static string LifecycleKey(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup-deliveries";
    private static string DedupKey(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData("job"u8));
    private static ValueTask<long> Expiry(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIRETIME', KEYS[1])"), [DedupKey(queue)], []);
    private static async Task<string?> Lifecycle(IRespireClient client, RespireJobQueue<string> queue)
    {
        using var values = await client.ExecuteAsync("HVALS", [LifecycleKey(queue)]);
        return values.Count == 0 ? null : values[0].AsString();
    }
    private static ValueTask<bool> CreateGroup(IRespireClient client, RespireJobQueue<string> queue)
        => client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
    internal static ValueTask<long> Promote(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(Script("JobQueueScheduling", "Promote"),
            [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due", LifecycleKey(queue)], [64]);
    internal static async Task<RespireStreamEntry[]> Read(IRespireClient client, RespireJobQueue<string> queue, bool grouped = false)
    {
        using var reply = await client.Scripts.ExecuteAsync(Script("JobQueueControls", grouped ? "ReadGrouped" : "Read"),
            grouped ? [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey, LifecycleKey(queue)]
                : [queue.StreamKey, queue.ControlKey, LifecycleKey(queue)],
            grouped ? [Group, "owner", 1, 64] : [Group, "owner"]);
        var entries = new RespireStreamEntry[reply[1].Count];
        for (var i = 0; i < entries.Length; i++)
        {
            var row = reply[1][i];
            var fields = new KeyValuePair<string, byte[]>[row[1].Count / 2];
            for (var j = 0; j < fields.Length; j++) fields[j] = new(row[1][j * 2].AsString(), row[1][j * 2 + 1].AsBytes());
            entries[i] = new(new(row[0].AsString()), fields);
        }
        return entries;
    }
    private static RespireScript Script(string type, string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming." + type)!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static RespireScript Terminal(RespireScript script, bool expiry = false) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobDeduplication")!.GetMethod("Terminal", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [script, expiry])!;
}
