using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class JobQueueIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LogsAreBoundedPrefixedAndReadableAfterCompletion(int protocol)
    {
        await using var root = await ConnectAsync(protocol);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        const string id = "job:{other}:é";
        await queue.EnqueueAsync(id, "payload");
        RespireJob<string>? delivery = null;
        await WithLogDeliveryAsync(client, new() { MaximumLogLines = 2, MaximumLogLineBytes = 4 }, async (job, token) =>
        {
            delivery = job;
            await Assert.That(await job.LogAsync("one", token)).IsTrue();
            await Assert.That(await job.LogAsync("éé", token)).IsTrue();
            await Assert.That(await job.LogAsync("last", token)).IsTrue();
            await Assert.That(async () => await job.LogAsync("ééé", token)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(async () => await job.LogAsync("\ud800", token)).Throws<EncoderFallbackException>();
            await Assert.That(async () => await job.LogAsync(null!, token)).Throws<ArgumentNullException>();
            await Assert.That(async () => await job.LogAsync("stop", new(true))).Throws<OperationCanceledException>();
            await Assert.That(await queue.GetLogsAsync(id)).IsEquivalentTo(new[] { "éé", "last" });
            await Assert.That((await queue.GetLogsAsync(id, 1)).Single()).IsEqualTo("last");
            await Assert.That((await Queue(root).GetLogsAsync(id)).Length).IsEqualTo(0);
        });
        await Assert.That(await delivery!.LogAsync("late")).IsFalse();
        await Assert.That(string.Join("|", await queue.GetLogsAsync(id))).IsEqualTo("éé|last");
        await Assert.That((await queue.GetLogsAsync("missing")).Length).IsEqualTo(0);
        await Assert.That(async () => await queue.GetLogsAsync(id, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await queue.GetLogsAsync(id, 1001)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConcurrentLogAppendsRespectTheCap()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueAsync("job", "payload");
        await WithLogDeliveryAsync(client, new() { MaximumLogLines = 3 }, async (job, token) =>
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 24)
                .Select(i => job.LogAsync(i.ToString(), token).AsTask()));
            await Assert.That(results.All(result => result)).IsTrue();
            var lines = await queue.GetLogsAsync("job", cancellationToken: token);
            await Assert.That(lines.Length).IsEqualTo(3);
            await Assert.That(lines.Distinct().Count()).IsEqualTo(3);
        });
    }

    [Test]
    public async Task LogWritesNeverExtendRetentionAndIdleListsExpire()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueAsync("job", "payload");
        var key = JobLogKeys.ForJob(queue.StreamKey, "job");
        await WithLogDeliveryAsync(client, new() { LogRetention = TimeSpan.FromSeconds(10) }, async (job, token) =>
        {
            await job.LogAsync("first", token);
            using var first = await client.ExecuteAsync("PEXPIRETIME", token, key);
            await job.LogAsync("second", token);
            using var second = await client.ExecuteAsync("PEXPIRETIME", token, key);
            await Assert.That(second.AsInteger()).IsEqualTo(first.AsInteger());
            // Advance expiry with Redis, avoiding wall-clock sleeps or timing assumptions.
            using var expire = await client.ExecuteAsync("PEXPIREAT", token, key, 1);
            await Assert.That((await queue.GetLogsAsync("job", cancellationToken: token)).Length).IsEqualTo(0);
            await job.LogAsync("new list", token);
            await Assert.That((await queue.GetLogsAsync("job", cancellationToken: token)).Single()).IsEqualTo("new list");
        });
    }

    [Test]
    public async Task ExplicitJobExpiryCapsLogLifetimeAndRejectsExpiredDelivery()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        using var time = await client.ExecuteAsync("TIME");
        var expires = long.Parse(time[0].AsString()) * 1000 + 60000;
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        var entry = await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "job"),
            ("_respire.job.payload", "payload"), ("_respire.job.expires", expires.ToString()));
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "crashed");
        var key = JobLogKeys.ForJob(queue.StreamKey, "job");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("Log"), [queue.StreamKey, key],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", entry.Value, 1, "live", 100, 86400000])).IsEqualTo(1);
        using var expiry = await client.ExecuteAsync("PEXPIRETIME", [key]);
        await Assert.That(expiry.AsInteger()).IsEqualTo(expires);

        var expired = await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "expired"),
            ("_respire.job.payload", "payload"), ("_respire.job.expires", "1"));
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "crashed");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("Log"),
            [queue.StreamKey, JobLogKeys.ForJob(queue.StreamKey, "expired")],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", expired.Value, 1, "expired", 100, 86400000])).IsEqualTo(0);
        await Assert.That((await queue.GetLogsAsync("expired")).Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("rpush")]
    [Arguments("ltrim")]
    [Arguments("pexpireat")]
    public async Task DeniedLogWriteLeavesNoPartialList(string command)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var entry = await PendingJobAsync(admin, queue);
        var username = "logs-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">log-test-password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "log-test-password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(JobScript("Log"),
                [queue.StreamKey, JobLogKeys.ForJob(queue.StreamKey, "job")],
                [RespireJobQueue<string>.ConsumerGroup, "crashed", entry.Value, 1, "line", 100, 86400000]))
                .Throws<RespireServerException>();
            await Assert.That((await queue.GetLogsAsync("job")).Length).IsEqualTo(0);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    private static async Task WithLogDeliveryAsync(IRespireClient client, RespireJobQueueOptions policy,
        Func<RespireJob<string>, CancellationToken, Task> inspect)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new HandlerState { OnHandle = async token =>
        {
            await release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        } };
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("test", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ReadWait = TimeSpan.FromMilliseconds(20), MinimumIdleTime = TimeSpan.FromMinutes(1) }, policy);
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try { await inspect(await state.Received.Task.WaitAsync(deadline.Token), deadline.Token); }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(deadline.Token);
        }
    }
}
