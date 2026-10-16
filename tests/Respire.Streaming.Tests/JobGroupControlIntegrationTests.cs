using System.Collections.Concurrent;
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
public class JobGroupControlIntegrationTests(RedisTestContainer redis)
{
    private const string ConsumerGroup = RespireJobQueue<string>.ConsumerGroup;
    private const string ApplicationGroup = "tenant:{foreign}:é/東京\0😀";

    [Test]
    [Arguments(2, "tenant:")]
    [Arguments(3, "{tenant}:")]
    public async Task IndependentClientsShareConcurrencyAndStaleReleaseCannotFreeAnotherPermit(int protocol, string prefix)
    {
        await using var first = await Connect(protocol);
        await using var second = await Connect(protocol);
        var firstClient = first.WithKeyPrefix(prefix);
        var secondClient = second.WithKeyPrefix(prefix);
        var queue = Queue(firstClient);
        var keys = JobGroupIntegrationTests.Keys(queue).Select(key => firstClient.ResolveKey(key).ToBytes()).ToArray();
        await Assert.That(keys.Select(key => ClusterHash.GetSlot(key)).Distinct().Count()).IsEqualTo(1);
        await queue.SetGroupOptionsAsync(ApplicationGroup, new() { MaxConcurrency = 2 });
        for (var i = 0; i < 8; i++) await queue.EnqueueScheduledAsync("job-" + i, "p", new() { Group = ApplicationGroup });
        await firstClient.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            (Consumer: "consumer-" + i, Entries: await Read(i % 2 == 0 ? firstClient : secondClient, queue, "consumer-" + i))));
        var admitted = reads.Where(read => read.Entries.Length != 0).ToArray();
        await Assert.That(admitted.Length).IsEqualTo(2);
        await Assert.That(await firstClient.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(6);
        var entry = admitted[0].Entries.Single();
        await Assert.That(await Release(secondClient, queue, "wrong-owner", entry.Id, 1)).IsEqualTo(0);
        await Assert.That(await Release(secondClient, queue, admitted[0].Consumer, entry.Id, 2)).IsEqualTo(0);
        await Assert.That((await Read(secondClient, queue, "replacement")).Length).IsEqualTo(0);
        await Assert.That(await Release(secondClient, queue, admitted[0].Consumer, entry.Id, 1)).IsEqualTo(1);
        await Assert.That((await Read(secondClient, queue, "replacement")).Length).IsEqualTo(1);
        await Assert.That(await Release(firstClient, queue, admitted[0].Consumer, entry.Id, 1)).IsEqualTo(0);
        await Assert.That((await Read(firstClient, queue, "extra")).Length).IsEqualTo(0);
        await Assert.That(await first.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
    }

    [Test]
    public async Task PauseSurvivesReconnectAndDoesNotBlockAnotherGroup()
    {
        await using var first = await Connect();
        var queue = Queue(first);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await Assert.That(await queue.ResumeGroupAsync("a")).IsFalse();
        await Assert.That(await queue.PauseGroupAsync("a")).IsTrue();
        await Assert.That(await queue.PauseGroupAsync("a")).IsFalse();
        // Changing limits does not implicitly resume a paused group.
        await queue.SetGroupOptionsAsync("a", null);
        await queue.EnqueueScheduledAsync("paused", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("other", "p", new() { Group = "b" });
        await first.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await using var restarted = await Connect();
        var next = await Read(restarted, queue, "restarted", 2);
        await Assert.That(next.Length).IsEqualTo(1);
        await Assert.That(next.Single().GetString("_respire.job.id")).IsEqualTo("other");
        await Assert.That((await Read(restarted, queue, "restarted", 4)).Length).IsEqualTo(0);
        using (var parked = await restarted.ExecuteAsync("ZCARD", [queue.StreamKey + ":group-parked"]))
            await Assert.That(parked.AsInteger()).IsEqualTo(1);
        var controller = Queue(restarted);
        await Assert.That(await controller.ResumeGroupAsync("a")).IsTrue();
        await Assert.That(await controller.ResumeGroupAsync("a")).IsFalse();
        await Assert.That((await Read(restarted, queue, "restarted")).Single().GetString("_respire.job.id")).IsEqualTo("paused");
    }

    [Test]
    public async Task InspectionRateStateMatchesAdmissionAtTheBurstBoundary()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        // An indivisible window exercises the same interval rounding in both scripts.
        await queue.SetGroupOptionsAsync("a", new() { RateLimit = new() { PermitLimit = 2, Window = TimeSpan.FromMilliseconds(3600001) } });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        for (var i = 0; i < 3; i++) await queue.EnqueueScheduledAsync("job-" + i, "p", new() { Group = "a" });
        for (var i = 0; i < 3; i++)
        {
            var info = await RespireJobGroups.GetAsync(client, queue.StreamKey, "a");
            await Assert.That(info.IsRateLimited).IsEqualTo(i == 2);
            await Assert.That(info.IsEligible).IsEqualTo(i < 2);
            await Assert.That((await Read(client, queue, "reader")).Length).IsEqualTo(i < 2 ? 1 : 0);
        }
        await queue.SetGroupOptionsAsync("a", null);
        await Assert.That((await RespireJobGroups.GetAsync(client, queue.StreamKey, "a")).IsRateLimited).IsFalse();
        await Assert.That((await Read(client, queue, "reader")).Length).IsEqualTo(1);
    }

    [Test]
    public async Task SharedRateBurstDoesNotBlockOtherGroupsAndRemovingPolicyWakesTheGroup()
    {
        await using var first = await Connect();
        await using var second = await Connect();
        var queue = Queue(first);
        await queue.SetGroupOptionsAsync("a", new() { RateLimit = new() { PermitLimit = 2, Window = TimeSpan.FromHours(1) } });
        await first.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await Assert.That((await Read(first, queue, "empty")).Length).IsEqualTo(0);
        for (var i = 0; i < 6; i++) await queue.EnqueueScheduledAsync("limited-" + i, "p", new() { Group = "a" });
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Read(i % 2 == 0 ? first : second, queue, "reader-" + i)));
        await Assert.That(results.Sum(entries => entries.Length)).IsEqualTo(2);
        await queue.EnqueueScheduledAsync("other", "p", new() { Group = "b" });
        await Assert.That((await Read(second, queue, "other", 2)).Single().GetString("_respire.job.id")).IsEqualTo("other");
        await queue.SetGroupOptionsAsync("a", null);
        await Assert.That((await Read(second, queue, "unlimited", 4)).Length).IsEqualTo(4);
    }

    [Test]
    public async Task ExpiredAbandonedLeaseAllowsANewDeliveryAndOldReleaseCannotFreeIt()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("abandoned", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("next", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("last", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var abandoned = (await Read(client, queue, "crashed")).Single();
        // Age only this test's lease and owner member. No wall-clock timing race is needed.
        using var leases = await client.ExecuteAsync("ZRANGE", [queue.StreamKey + ":group-leases", 0, -1]);
        var member = leases[0].AsString();
        var separator = member.IndexOf('|');
        var expired = member[..(separator + 1)] + "0000000000000000" + member[(separator + 17)..];
        using (await client.ExecuteAsync("ZREM", [queue.StreamKey + ":group-leases", member])) { }
        using (await client.ExecuteAsync("ZADD", [queue.StreamKey + ":group-leases", 0, expired])) { }
        using (await client.ExecuteAsync("HSET", [queue.StreamKey + ":group-owners", abandoned.Id.Value, expired])) { }
        await Assert.That((await Read(client, queue, "replacement")).Single().GetString("_respire.job.id")).IsEqualTo("next");
        await Assert.That(await Release(client, queue, "crashed", abandoned.Id, 1)).IsEqualTo(0);
        await Assert.That((await Read(client, queue, "extra")).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("concurrency")]
    [Arguments("rate")]
    [Arguments("paused")]
    public async Task ShortenedLeaseRenewalUpdatesParkedDeadlineWithoutBypassingOtherLimits(string limit)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        RespireJobRateLimit? rate = limit == "rate" ? new() { PermitLimit = 1, Window = TimeSpan.FromHours(2) } : null;
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1, RateLimit = rate, LeaseDuration = TimeSpan.FromHours(1) });
        await queue.EnqueueScheduledAsync("first", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("next", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var first = (await Read(client, queue, "owner")).Single();
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1, RateLimit = rate, LeaseDuration = TimeSpan.FromMinutes(1) });
        if (limit == "paused") await queue.PauseGroupAsync("a");
        await Assert.That((await Read(client, queue, "waiting")).Length).IsEqualTo(0);
        using var oldDeadline = await client.ExecuteAsync("ZSCORE", [queue.StreamKey + ":group-parked", "61|"]);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("Renew"), JobGroupIntegrationTests.Keys(queue),
            ["owner", first.Id.Value, 1, ConsumerGroup])).IsEqualTo(60000);
        using var owner = await client.ExecuteAsync("HGET", [queue.StreamKey + ":group-owners", first.Id.Value]);
        var expiry = long.Parse(owner.AsString().Split('|')[1], System.Globalization.CultureInfo.InvariantCulture);
        using var deadline = await client.ExecuteAsync("ZSCORE", [queue.StreamKey + ":group-parked", "61|"]);
        if (limit == "concurrency")
        {
            await Assert.That(deadline.AsDouble()).IsEqualTo((double)expiry);
            await Assert.That(deadline.AsDouble()).IsLessThan(oldDeadline.AsDouble());
        }
        else await Assert.That(deadline.AsDouble()).IsEqualTo(oldDeadline.AsDouble());
        // The shortened lease still owns capacity until its new deadline.
        await Assert.That((await Read(client, queue, "waiting")).Length).IsEqualTo(0);
    }

    [Test]
    public async Task ReleasedNackIsRecoveredAndItsReplacementPermitIsReleased()
    {
        // Keep the native regression active even when the shared test server is Redis 7.
        await using var native = await ContainerStartRetry.StartAsync(
            () => new Testcontainers.Redis.RedisBuilder("redis:8.8-alpine").Build(), [6379]);
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(native.GetConnectionString()));
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("failed", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("next", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var failed = (await Read(client, queue, "owner")).Single();
        await client.Scripts.ExecuteIntegerAsync(Script("Nack"),
            [queue.StreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners"],
            [ConsumerGroup, "owner", failed.Id.Value, 1, 0]);
        using (var pending = await client.ExecuteAsync("XPENDING", [queue.StreamKey, ConsumerGroup, "-", "+", 1]))
            await Assert.That(pending[0][2].AsInteger()).IsEqualTo(-1);
        await Assert.That(await Release(client, queue, "owner", failed.Id, 1)).IsEqualTo(1);
        var claim = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueControls")!
            .GetField("ClaimGrouped", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using (var recovered = await client.Scripts.ExecuteAsync(claim,
            [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey], [ConsumerGroup, "replacement", 60000, "0-0", 1]))
        {
            await Assert.That(recovered[1].Count).IsEqualTo(1);
            await Assert.That(recovered[1][0][0].AsString()).IsEqualTo(failed.Id.Value);
            await Assert.That(recovered[1][0][2].AsString()).IsEqualTo("2");
        }
        await Assert.That((await Read(client, queue, "waiting")).Length).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("Complete"),
            [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners"],
            [ConsumerGroup, "replacement", failed.Id.Value, 2, "completed"])).IsEqualTo(1);
        await Assert.That(await Release(client, queue, "replacement", failed.Id, 2)).IsEqualTo(1);
        await Assert.That((await Read(client, queue, "waiting")).Single().GetString("_respire.job.id")).IsEqualTo("next");
    }

    [Test]
    public async Task RateEligibilityAutomaticallyWakesParkedGroup()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { RateLimit = new() { PermitLimit = 1, Window = TimeSpan.FromMilliseconds(250) } });
        await queue.EnqueueScheduledAsync("first", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("next", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await Read(client, queue, "first");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        RespireStreamEntry[] next = [];
        await Until(async () => (next = await Read(client, queue, "second")).Length != 0, deadline.Token);
        await Assert.That(next.Single().GetString("_respire.job.id")).IsEqualTo("next");
        using var parked = await client.ExecuteAsync("ZCARD", [queue.StreamKey + ":group-parked"]);
        await Assert.That(parked.AsInteger()).IsEqualTo(0);
    }

    [Test]
    public async Task RecoveryHonorsLiveLeasesAndPauseAndFencesEarlierAttemptWithSameConsumer()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("job", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var original = (await Read(client, queue, "owner")).Single();
        using (var live = await Claim()) await Assert.That(live[1].Count).IsEqualTo(0);
        await queue.PauseGroupAsync("a");
        await Release(client, queue, "owner", original.Id, 1);
        using (var paused = await Claim()) await Assert.That(paused[1].Count).IsEqualTo(0);
        using (var replay = await client.Scripts.ExecuteAsync(
            (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueControls")!
                .GetField("ReplayGrouped", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
            [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey], [ConsumerGroup, "owner", 1, "0-0"]))
        {
            await Assert.That(replay[1].Count).IsEqualTo(0);
            await Assert.That(replay[0].AsString()).IsEqualTo("0-0");
        }
        await Assert.That((await client.Streams.PendingAsync(queue.StreamKey, ConsumerGroup)).Single().DeliveryCount).IsEqualTo(1);
        await queue.ResumeGroupAsync("a");
        using (var recovered = await Claim())
        {
            await Assert.That(recovered[1].Count).IsEqualTo(1);
            await Assert.That(recovered[1][0][2].AsString()).IsEqualTo("2");
        }
        await Assert.That(await Complete(1)).IsEqualTo(0);
        await Assert.That(await Release(client, queue, "owner", original.Id, 1)).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(1);
        await Assert.That(await Complete(2)).IsEqualTo(1);
        await Assert.That(await Release(client, queue, "owner", original.Id, 2)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(1);

        ValueTask<RespireResult> Claim() => client.Scripts.ExecuteAsync(
            (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobQueueControls")!
                .GetField("ClaimGrouped", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!,
            [.. JobGroupIntegrationTests.Keys(queue), queue.ControlKey], [ConsumerGroup, "owner", 0, "0-0", 1]);
        ValueTask<long> Complete(long attempt) => client.Scripts.ExecuteIntegerAsync(Script("Complete"),
            [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners"],
            [ConsumerGroup, "owner", original.Id.Value, attempt, "completed"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiryAcknowledgementCanReleaseItsLeaseExactlyOnce(bool deleteAcknowledged)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("expired", "p", new() { Group = "a" });
        await queue.EnqueueScheduledAsync("next", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var expired = (await Read(client, queue, "owner")).Single();
        // CheckExpiry receives the envelope's absolute deadline from the worker; use an elapsed
        // deadline directly so this test isolates the fenced acknowledgement without a clock race.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(Script("CheckExpiry"),
            [queue.StreamKey, queue.StreamKey + ":group-leases", queue.StreamKey + ":group-owners"],
            [ConsumerGroup, "owner", expired.Id.Value, 1, 1, deleteAcknowledged ? 1 : 0])).IsEqualTo(-1);
        await Assert.That(await Release(client, queue, "owner", expired.Id, 1)).IsEqualTo(1);
        await Assert.That(await Release(client, queue, "owner", expired.Id, 1)).IsEqualTo(0);
        await Assert.That((await Read(client, queue, "next")).Single().GetString("_respire.job.id")).IsEqualTo("next");
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
    }

    [Test]
    public async Task GroupOptionsRejectInvalidLimitsAndNamesBeforeWriting()
    {
        await using var client = await Connect();
        var queue = Queue(client);
        foreach (var options in new RespireJobGroupOptions[]
        {
            new() { MaxConcurrency = 0 }, new() { MaxConcurrency = -1 },
            new() { LeaseDuration = TimeSpan.Zero }, new() { LeaseDuration = TimeSpan.FromMilliseconds(99) },
            new() { LeaseDuration = TimeSpan.FromDays(30) },
            new() { RateLimit = new() { PermitLimit = 0 } },
            new() { RateLimit = new() { Window = TimeSpan.Zero } },
        })
            await Assert.That(async () => await queue.SetGroupOptionsAsync("a", options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await queue.SetGroupOptionsAsync(null!, new())).Throws<ArgumentNullException>();
        await Assert.That(async () => await queue.PauseGroupAsync("")).Throws<ArgumentException>();
        await Assert.That(async () => await queue.ResumeGroupAsync("")).Throws<ArgumentException>();
        await Assert.That(await client.Keys.ExistsAsync(queue.StreamKey + ":group-policy")).IsFalse();
    }

    [Test]
    [Arguments(":group-policy")]
    [Arguments(":group-leases")]
    [Arguments(":group-owners")]
    [Arguments(":group-parked")]
    public async Task WrongControlKeyTypeCannotPartiallyPromoteAJob(string suffix)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("job", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        await client.Strings.SetAsync(queue.StreamKey + suffix, "wrong type");
        await Assert.That(async () => await Read(client, queue, "owner")).Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("hset")]
    [Arguments("hdel")]
    [Arguments("zadd")]
    [Arguments("zrem")]
    public async Task DeniedLeaseMutationLeavesJobStaged(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1 });
        await queue.EnqueueScheduledAsync("job", "p", new() { Group = "a" });
        await admin.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var user = "group-control-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Read(restricted, queue, "owner")).Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("reserve")]
    public async Task RuntimeSelectionFailureRestoresRateAndLeaseState(string failurePoint)
    {
        await using var client = await Connect();
        var queue = Queue(client);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1,
            RateLimit = new() { PermitLimit = 1, Window = TimeSpan.FromHours(1) } });
        await queue.EnqueueScheduledAsync("job", "p", new() { Group = "a" });
        await client.Streams.CreateGroupAsync(queue.StreamKey, ConsumerGroup, RespireStreamId.Beginning);
        var source = JobSchedulingIntegrationTests.Script("ReadGroups").Source;
        var broken = RespireScript.Create(failurePoint == "cursor"
            ? source.Replace("redis.pcall('SET', cursor, prefix)", "redis.pcall('SET', cursor, prefix, 'PX', -1)")
            : source.Replace("redis.call('ZADD', leases, 0, member)", "error('injected lease write failure')"));
        await Assert.That(async () => await JobGroupIntegrationTests.Read(client, queue, "owner", script: broken))
            .Throws<RespireServerException>();
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(0);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey + ":scheduled")).IsEqualTo(1);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count).IsEqualTo(0);
        using (var leases = await client.ExecuteAsync("ZCARD", [queue.StreamKey + ":group-leases"]))
            await Assert.That(leases.AsInteger()).IsEqualTo(0);
        using (var owners = await client.ExecuteAsync("HLEN", [queue.StreamKey + ":group-owners"]))
            await Assert.That(owners.AsInteger()).IsEqualTo(0);
        using (var tat = await client.ExecuteAsync("HEXISTS", [queue.StreamKey + ":group-policy", "61|tat"]))
            await Assert.That(tat.AsInteger()).IsEqualTo(0);
        await Assert.That((await Read(client, queue, "owner")).Single().GetString("_respire.job.id")).IsEqualTo("job");
    }

    [Test]
    [Arguments("ack")]
    [Arguments("nack")]
    [Arguments("retry")]
    [Arguments("dead-letter")]
    [Arguments("throw")]
    [Arguments("renew")]
    [NotInParallel]
    public async Task HostedTransitionsReleaseCapacityForFollowingJobs(string outcome)
    {
        await using var first = await Connect();
        await using var second = await Connect();
        var queue = Queue(first);
        await queue.SetGroupOptionsAsync("a", new() { MaxConcurrency = 1,
            LeaseDuration = outcome == "renew" ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1) });
        var jobCount = outcome == "renew" ? 2 : 6;
        for (var i = 0; i < jobCount; i++) await queue.EnqueueScheduledAsync("job-" + i, "p", new() { Group = "a" });
        var state = new HandlerState { Outcome = outcome };
        await using var firstHost = Host(first, state, "first");
        await using var secondHost = Host(second, state, "second");
        var workers = new[] { firstHost.GetRequiredService<IHostedService>(), secondHost.GetRequiredService<IHostedService>() };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            foreach (var worker in workers) await worker.StartAsync(deadline.Token);
            var expectedCalls = jobCount * (outcome is "nack" or "retry" or "throw" ? 2 : 1);
            await Until(async () =>
            {
                if (state.Calls.Values.Sum() < expectedCalls
                    || (await first.Streams.PendingSummaryAsync(queue.StreamKey, ConsumerGroup)).Count != 0
                    || await first.Streams.CountAsync(queue.StreamKey + ":scheduled") != 0) return false;
                // Acknowledgement precedes the handler's finally/release operation. Wait for
                // that transition too before asserting that no concurrency permit leaked.
                using var remaining = await first.ExecuteAsync("ZCARD", [queue.StreamKey + ":group-leases"]);
                return remaining.AsInteger() == 0;
            }, deadline.Token);
            await Assert.That(state.Calls.Values.Sum()).IsEqualTo(expectedCalls);
            await Assert.That(state.MaximumActive).IsEqualTo(1);
            await Assert.That(state.Calls.Count).IsEqualTo(jobCount);
            foreach (var (id, calls) in state.Calls)
                await Assert.That((await queue.GetLogsAsync(id)).Length).IsEqualTo(calls);
            using var leases = await first.ExecuteAsync("ZCARD", [queue.StreamKey + ":group-leases"]);
            await Assert.That(leases.AsInteger()).IsEqualTo(0);
            await Assert.That(await first.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(outcome == "dead-letter" ? jobCount : 0);
        }
        finally { foreach (var worker in workers) await worker.StopAsync(CancellationToken.None); }
    }

    private static ServiceProvider Host(IRespireClient client, HandlerState state, string consumer)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("group-controls", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerName = consumer, ConsumerCount = 2, DeliveryLimit = 3, MinimumIdleTime = TimeSpan.FromMilliseconds(100),
                RecoveryPollInterval = TimeSpan.FromMilliseconds(20), ReadWait = TimeSpan.FromMilliseconds(20) },
            new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(20) });
        return services.BuildServiceProvider();
    }

    public sealed class HandlerState
    {
        public string Outcome { get; init; } = "ack";
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public int Active;
        public int MaximumActive;
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref state.Active);
            int observed;
            do { observed = Volatile.Read(ref state.MaximumActive); }
            while (active > observed && Interlocked.CompareExchange(ref state.MaximumActive, active, observed) != observed);
            try
            {
                var call = state.Calls.AddOrUpdate(job.Id, 1, (_, previous) => previous + 1);
                await Assert.That(await job.LogAsync("attempt " + call, cancellationToken)).IsTrue();
                // NACK must outlive MinimumIdleTime to exercise native XNACK on Redis 8.8+.
                await Task.Delay(state.Outcome == "renew" ? 6500 : state.Outcome == "nack" ? 250 : 10, cancellationToken);
                if (call != 1) return RespireStreamWorkerResult.Ack;
                return state.Outcome switch
                {
                    "nack" => RespireStreamWorkerResult.Nack,
                    "retry" => job.RetryAfter(TimeSpan.Zero),
                    "dead-letter" => RespireStreamWorkerResult.DeadLetter,
                    "throw" => throw new InvalidOperationException("Test handler failure"),
                    _ => RespireStreamWorkerResult.Ack,
                };
            }
            finally { Interlocked.Decrement(ref state.Active); }
        }
    }

    private static async Task Until(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition()) await Task.Delay(10, cancellationToken);
    }

    private ValueTask<RespireClient> Connect(int protocol = 3) => RespireClient.ConnectAsync(
        RespireOptions.Parse(redis.ConnectionString) with { Protocol = (RespireProtocol)protocol });
    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "group-controls", Encoding.UTF8.GetBytes);
    private static Task<RespireStreamEntry[]> Read(IRespireClient client, RespireJobQueue<string> queue, string consumer, int count = 1)
        => JobGroupIntegrationTests.Read(client, queue, consumer, count);
    private static ValueTask<long> Release(IRespireClient client, RespireJobQueue<string> queue, string consumer, RespireStreamId id, long attempt)
        => client.Scripts.ExecuteIntegerAsync(Script("Release"), JobGroupIntegrationTests.Keys(queue), [consumer, id.Value, attempt]);
    private static RespireScript Script(string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobGroupControls")!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
