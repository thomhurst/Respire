using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobDeduplicationMaintenanceTests(RedisTestContainer redis)
{
    [Test]
    [Arguments("XDEL", false, 2)]
    [Arguments("XDEL", true, 3)]
    [Arguments("XTRIM", false, 3)]
    [Arguments("XTRIM", true, 2)]
    [Arguments("DEL", false, 2)]
    [Arguments("DEL", true, 3)]
    public async Task ReclaimsManuallyRemovedEnvelopes(string removal, bool scheduled, int protocol)
    {
        await using var root = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
            with { Protocol = (RespireProtocol)protocol });
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        var entry = scheduled
            ? await queue.EnqueueScheduledAsync("job", "orphan", new() { DeduplicationMode = RespireJobDeduplicationMode.Replace })
            : await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "orphan");
        var stream = queue.StreamKey + (scheduled ? ":scheduled" : "");
        var physicalStream = client.ResolveKey(stream).ToBytes();
        RespireValue[] arguments = removal switch
        {
            "XDEL" => [physicalStream, entry.EntryId.Value],
            "XTRIM" => [physicalStream, "MAXLEN", 0],
            _ => [physicalStream],
        };
        using (await client.ExecuteAsync(removal, arguments)) { }
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(1);
        await Assert.That(await Length(client, queue)).IsEqualTo(0);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(0);
        // Cleanup leaves deduplication windows intact, even when the original job was removed.
        await Assert.That((await queue.EnqueueAsync("job", "duplicate")).Enqueued).IsFalse();
        await Assert.That(await Length(root, queue)).IsEqualTo(0);
    }

    [Test]
    public async Task SupersededBodyDoesNotProtectAMissingCurrentRevision()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "obsolete");
        var latest = await queue.EnqueueAsync("job", "current");
        using (await client.ExecuteAsync("XDEL", [queue.StreamKey, latest.EntryId.Value])) { }
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task PreservesActiveGenerationAfterItsStreamIsDeleted()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "active");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await JobReplacementIntegrationTests.Read(client, queue);
        await client.Keys.DeleteAsync(queue.StreamKey);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(0);
        await Assert.That(await Length(client, queue)).IsEqualTo(1);
        await Assert.That((await queue.EnqueueAsync("job", "duplicate")).Enqueued).IsFalse();
    }

    [Test]
    public async Task ReclaimsMissingFollowupWithoutRetiringItsActiveParent()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.KeepLastIfActive, "job", "active");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await JobReplacementIntegrationTests.Read(client, queue);
        await queue.EnqueueAsync("job", "followup");
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(0);
        await client.Keys.DeleteAsync(queue.StreamKey + ":scheduled");
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(1);
        using var values = await client.ExecuteAsync("HVALS", [Lifecycle(queue)]);
        await Assert.That(Enumerable.Range(0, values.Count).Count(i => values[i].AsString().EndsWith("|active", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That((await queue.EnqueueAsync("job", "new followup")).Enqueued).IsTrue();
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ScanLimitFailureDoesNotRemoveAnyOrphans(bool hashLimit)
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "orphan", "orphan");
        await client.Keys.DeleteAsync(queue.StreamKey);
        if (hashLimit) await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "live", "live");
        else await queue.EnqueueAsync("live", "live");
        var before = await Length(client, queue);
        var error = await Assert.That(async () => await queue.ReclaimOrphanedDeduplicationAsync(1))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("maintenance scan limit exceeded");
        await Assert.That(await Length(client, queue)).IsEqualTo(before);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WrongStreamTypeFailsBeforeRemovingFields(bool scheduled)
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "orphan");
        await client.Keys.DeleteAsync(queue.StreamKey);
        await client.Strings.SetAsync(queue.StreamKey + (scheduled ? ":scheduled" : ""), "wrong type");
        await Assert.That(async () => await queue.ReclaimOrphanedDeduplicationAsync()).Throws<RespireServerException>();
        await Assert.That(await Length(client, queue)).IsEqualTo(1);
    }

    [Test]
    public async Task DeniedCleanupPreservesAllFields()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "job", "orphan");
        await admin.Keys.DeleteAsync(queue.StreamKey);
        var user = "maintenance-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-hdel"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = user, Password = "password" });
            var error = await Assert.That(async () => await Queue(restricted).ReclaimOrphanedDeduplicationAsync())
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("NOPERM");
            await Assert.That(await Length(admin, queue)).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task ScansBeyondOnePageAndAcceptsTheExactBudget()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace, "orphan", "orphan");
        await client.Keys.DeleteAsync(queue.StreamKey);
        for (var i = 0; i < 260; i++) await queue.EnqueueAsync("live-" + i, "legacy");
        await queue.EnqueueScheduledAsync("scheduled", "pending", new() { DeduplicationMode = RespireJobDeduplicationMode.Replace });
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync(263)).IsEqualTo(1);
        await Assert.That(await Length(client, queue)).IsEqualTo(1);
        await Assert.That(await queue.ReclaimOrphanedDeduplicationAsync(262)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task RejectsNonpositiveLimits(int maximumEntries)
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        await Assert.That(async () => await Queue(client).ReclaimOrphanedDeduplicationAsync(maximumEntries))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "maintenance", Encoding.UTF8.GetBytes);
    private static string Lifecycle(RespireJobQueue<string> queue) => queue.StreamKey + ":dedup-deliveries";
    private static ValueTask<long> Length(IRespireClient client, RespireJobQueue<string> queue)
        => client.Scripts.ExecuteIntegerAsync(RespireScript.Create("return redis.call('HLEN', KEYS[1])"), [Lifecycle(queue)], []);
}
