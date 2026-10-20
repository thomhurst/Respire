using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobDeduplicationIntegrationTests(RedisTestContainer redis)
{
    private const string Group = RespireJobQueue<string>.ConsumerGroup;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    [Test]
    public async Task ConcurrentFirstSubmissionsFixModeAndWindow()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => queue.EnqueueAsync(
            i % 2 == 0 ? RespireJobDeduplicationMode.Extend : RespireJobDeduplicationMode.FirstSubmissionWins,
            "job", "payload-" + i, Window).AsTask()));
        await Assert.That(results.Count(result => result.Enqueued)).IsEqualTo(1);
        await Assert.That(results.Select(result => result.EntryId).Distinct().Count()).IsEqualTo(1);
        var winner = Array.FindIndex(results, result => result.Enqueued);
        var entry = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(entry.GetString("_respire.job.payload")).IsEqualTo("payload-" + winner);
        var shortened = await Shorten(client, queue);
        await queue.EnqueueAsync("job", "duplicate", TimeSpan.FromMilliseconds(1));
        var expiry = await Expiry(client, queue);
        if (winner % 2 == 0)
        {
            await Assert.That(entry.GetString("_respire.job.deduplication")).IsEqualTo("Extend");
            await Assert.That(expiry).IsGreaterThan(shortened + 3_000_000);
        }
        else
        {
            await Assert.That(entry.GetString("_respire.job.deduplication")).IsNull();
            await Assert.That(expiry).IsEqualTo(shortened);
        }
    }

    [Test]
    [Arguments(false, false, 2)]
    [Arguments(false, true, 2)]
    [Arguments(true, false, 2)]
    [Arguments(true, true, 2)]
    [Arguments(false, false, 3)]
    [Arguments(false, true, 3)]
    [Arguments(true, false, 3)]
    [Arguments(true, true, 3)]
    public async Task ExtendRetainsWinningEnvelopeAndWindowAcrossEnqueueApis(bool scheduled, bool transactional, int protocol)
    {
        await using var root = await Connect(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var options = new RespireJobEnqueueOptions
        {
            DeduplicationMode = RespireJobDeduplicationMode.Extend, DeduplicationWindow = Window,
            Delay = TimeSpan.FromMinutes(5), TimeToLive = TimeSpan.FromMinutes(10), Priority = 7, Group = "group",
        };
        RespireJobEnqueueResult first;
        if (transactional)
        {
            await using var transaction = client.CreateTransaction();
            var pending = scheduled ? queue.EnqueueScheduled(transaction, "job", "first", options)
                : queue.Enqueue(transaction, RespireJobDeduplicationMode.Extend, "job", "first", Window);
            await transaction.CommitAsync();
            first = pending.Result;
        }
        else first = scheduled ? await queue.EnqueueScheduledAsync("job", "first", options)
            : await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "first", Window);
        var stream = queue.StreamKey + (scheduled ? ":scheduled" : "");
        var before = (await client.Streams.ReadAsync(stream)).Single();
        var groupedBefore = await client.SortedSets.CountAsync(queue.StreamKey + ":group-jobs");
        // Shorten metadata only, then prove renewal restores the original duration.
        // No wall-clock sleeps or tight expiry timing assumptions are needed.
        var shortenedExpiry = await Shorten(client, queue);
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            i % 2 == 0 ? queue.EnqueueAsync("job", "replacement", TimeSpan.FromMilliseconds(1)).AsTask()
                : queue.EnqueueScheduledAsync("job", "replacement", new()
                {
                    Priority = -1, Delay = TimeSpan.FromDays(1), DeduplicationWindow = TimeSpan.FromMilliseconds(1),
                }).AsTask()));
        await Assert.That(duplicates.All(result => !result.Enqueued && result.EntryId == first.EntryId)).IsTrue();
        await Assert.That(await Expiry(client, queue)).IsGreaterThan(shortenedExpiry + 3_000_000);
        var after = (await client.Streams.ReadAsync(stream)).Single();
        await Assert.That(after.GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That(after.GetString("_respire.job.deduplication")).IsEqualTo("Extend");
        await Assert.That(after.GetString("_respire.job.expires")).IsEqualTo(before.GetString("_respire.job.expires"));
        await Assert.That(after.GetString("_respire.job.group")).IsEqualTo(before.GetString("_respire.job.group"));
        await Assert.That(after.GetString("_respire.job.priority")).IsEqualTo(before.GetString("_respire.job.priority"));
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":group-jobs")).IsEqualTo(groupedBefore);
        await Assert.That(await root.Streams.CountAsync(stream)).IsEqualTo(0);

        // Default transaction calls also honor the winning Extend mode inside EXEC.
        shortenedExpiry = await Shorten(client, queue);
        await using var duplicateTransaction = client.CreateTransaction();
        var duplicate = queue.Enqueue(duplicateTransaction, "job", "transaction duplicate", default);
        var scheduledDuplicate = queue.EnqueueScheduled(duplicateTransaction, "job", "scheduled duplicate", new());
        await duplicateTransaction.CommitAsync();
        await Assert.That(duplicate.Result).IsEqualTo(new RespireJobEnqueueResult(first.EntryId, false));
        await Assert.That(scheduledDuplicate.Result).IsEqualTo(duplicate.Result);
        await Assert.That(await Expiry(client, queue)).IsGreaterThan(shortenedExpiry + 3_000_000);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DefaultGenerationCannotBeChangedByAnExtendDuplicate(bool scheduled)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var first = scheduled ? await queue.EnqueueScheduledAsync("job", "first", new())
            : await queue.EnqueueAsync("job", "first", default);
        var expiry = await Expiry(client, queue);
        await Assert.That(await client.GetStringAsync(Key(queue))).IsEqualTo(first.EntryId.Value);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "second", Window);
        await queue.EnqueueScheduledAsync("job", "third", new() { DeduplicationMode = RespireJobDeduplicationMode.Extend });
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        await Assert.That(await client.GetStringAsync(Key(queue))).IsEqualTo(first.EntryId.Value);
    }

    [Test]
    [Arguments(2, "completed")]
    [Arguments(3, "completed")]
    [Arguments(2, "dead-letter")]
    [Arguments(3, "dead-letter")]
    public async Task AcquisitionRetryAndCompletionDoNotReplaceOrRenewTheJob(int protocol, string terminal)
    {
        await using var client = await Connect(protocol);
        var queue = Queue(client);
        var first = await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "first", Window);
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        var read = client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "worker").AsTask();
        var duplicates = Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => queue.EnqueueAsync("job", "changed").AsTask()));
        await Task.WhenAll(read, duplicates);
        await Assert.That((await read).Single().GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That((await duplicates).All(result => !result.Enqueued && result.EntryId == first.EntryId)).IsTrue();
        var expiry = await Expiry(client, queue);
        RespireKey[] keys = [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due"];
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Retry"),
            keys, [Group, "worker", first.EntryId.Value, 1, 0, 0])).IsEqualTo(1);
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        var shortened = await Shorten(client, queue);
        await queue.EnqueueAsync("job", "during retry");
        await Assert.That(await Expiry(client, queue)).IsGreaterThan(shortened + 3_000_000);
        expiry = await Expiry(client, queue);
        await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Promote"), keys, [64]);
        var retry = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "worker")).Single();
        await Assert.That(retry.GetString("_respire.job.payload")).IsEqualTo("first");
        await Assert.That(retry.GetString("_respire.job.deduplication")).IsEqualTo("Extend");
        var complete = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueScripts")!
            .GetField("Complete", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        await client.Scripts.ExecuteIntegerAsync(complete, [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey],
            [Group, "worker", retry.Id.Value, 1, terminal, "test", ""]);
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
        shortened = await Shorten(client, queue);
        await Assert.That((await queue.EnqueueAsync("job", "after completion")).Enqueued).IsFalse();
        await Assert.That(await Expiry(client, queue)).IsGreaterThan(shortened + 3_000_000);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task WindowExpiryStartsANewGenerationWithoutDeletingOldPayload()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var first = await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "first", Window);
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIREAT', KEYS[1], 1)"), [Key(queue)], []);
        var second = await queue.EnqueueAsync("job", "second");
        await Assert.That(second.Enqueued).IsTrue();
        await Assert.That(second.EntryId).IsNotEqualTo(first.EntryId);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(2);
        var expiry = await Expiry(client, queue);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "third");
        await Assert.That(await Expiry(client, queue)).IsEqualTo(expiry);
    }

    [Test]
    public async Task ExpiredJobDoesNotRemoveItsExtendWindow()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        var first = await queue.EnqueueScheduledAsync("job", "first", new()
        {
            DeduplicationMode = RespireJobDeduplicationMode.Extend, DeduplicationWindow = Window,
            TimeToLive = TimeSpan.FromMinutes(1),
        });
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Promote"),
            [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due"], [64]);
        var delivery = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "worker")).Single();
        // Exercise the worker's terminal expiry branch using an already elapsed server timestamp.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("CheckExpiry"),
            [queue.StreamKey], [Group, "worker", delivery.Id.Value, 1, 1, 0])).IsEqualTo(-1);
        var shortened = await Shorten(client, queue);
        await Assert.That(await queue.EnqueueAsync("job", "after expiry"))
            .IsEqualTo(new RespireJobEnqueueResult(first.EntryId, false));
        await Assert.That(await Expiry(client, queue)).IsGreaterThan(shortened + 3_000_000);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeniedRenewalDoesNotAppendOrChangeMetadata()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "job", "first", Window);
        var expiry = await Expiry(admin, queue);
        var user = "dedup-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-set"]);
        try
        {
            await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Queue(client).EnqueueAsync("job", "second")).Throws<RespireServerException>();
            await Assert.That(async () => await Queue(client).EnqueueScheduledAsync("job", "second", new())).Throws<RespireServerException>();
            await Assert.That(await Expiry(admin, queue)).IsEqualTo(expiry);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(0);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    private ValueTask<RespireClient> Connect(int protocol = 3)
        => RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "deduplication", Encoding.UTF8.GetBytes);
    private static string Key(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData("job"u8));
    private static ValueTask<long> Expiry(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('PEXPIRETIME', KEYS[1])"), [Key(queue)], []);
    private static ValueTask<long> Shorten(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            redis.call('PEXPIRE', KEYS[1], 300000)
            return redis.call('PEXPIRETIME', KEYS[1])
            """), [Key(queue)], []);
}
