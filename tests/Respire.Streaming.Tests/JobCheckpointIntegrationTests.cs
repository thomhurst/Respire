using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class JobCheckpointIntegrationTests(RedisTestContainer redis)
{
    [Test]
    public async Task TransactionalEnqueuePreservesRuntimeAndDeduplicatesCheckpointIdentity()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await using var transaction = client.CreateTransaction();
        var pending = queue.EnqueueScheduled(transaction, "job", "payload", new() { MaximumRuntime = TimeSpan.FromSeconds(2) });
        await transaction.CommitAsync();
        var duplicate = await queue.EnqueueScheduledAsync("job", "changed", new() { MaximumRuntime = TimeSpan.FromSeconds(10) });
        await Assert.That(duplicate.Enqueued).IsFalse();
        await Assert.That(duplicate.EntryId).IsEqualTo(pending.Result.EntryId);
        var entry = (await client.Streams.RangeAsync(queue.StreamKey + ":scheduled")).Single();
        await Assert.That(entry.GetString("_respire.job.runtime")).IsEqualTo("2000");
        await Assert.That(Guid.TryParseExact(entry.GetString("_respire.job.checkpoint"), "N", out _)).IsTrue();
        var another = await queue.EnqueueScheduledAsync("another", "payload", new() { MaximumRuntime = TimeSpan.FromSeconds(2) });
        var otherEntry = (await client.Streams.RangeAsync(queue.StreamKey + ":scheduled", another.EntryId, another.EntryId)).Single();
        await Assert.That(otherEntry.GetString("_respire.job.checkpoint")).IsNotEqualTo(entry.GetString("_respire.job.checkpoint"));
        await Assert.That(async () => await queue.EnqueueScheduledAsync("invalid", "payload", new() { MaximumRuntime = TimeSpan.Zero }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SaveHonorsExpiryAndSameConsumerGeneration()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        const string group = RespireJobQueue<string>.ConsumerGroup;
        await client.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
        using var time = await client.ExecuteAsync("TIME");
        var expiry = long.Parse(time[0].AsString()) * 1000 + 60000;
        var identity = Guid.NewGuid().ToString("N");
        var key = queue.StreamKey + ":checkpoint:" + identity;
        var entry = await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "job"),
            ("_respire.job.checkpoint", identity), ("_respire.job.expires", expiry.ToString()));
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
            [group, "owner", entry.Value, 1, "live", new byte[] { 255 }, 50])).IsEqualTo(1);
        using var storedExpiry = await client.ExecuteAsync("PEXPIRETIME", [key]);
        await Assert.That(storedExpiry.AsInteger()).IsEqualTo(expiry);
        using var claimed = await client.ExecuteAsync("XCLAIM", [queue.StreamKey, group, "owner", 0, entry.Value]);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
            [group, "owner", entry.Value, 1, "stale", Array.Empty<byte>(), 0])).IsEqualTo(0);
        await Assert.That((await RespireJobCheckpoints.ReadAsync(client, queue.StreamKey, entry))!.Cursor).IsEqualTo("live");
        var expired = await client.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "expired"), ("_respire.job.expires", "1"));
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
            [group, "owner", expired.Value, 1, "expired", Array.Empty<byte>(), 0])).IsEqualTo(0);
        await Assert.That((await RespireJobCheckpoints.ReadAsync(client, queue.StreamKey, entry))!.Cursor).IsEqualTo("live");
    }

    [Test]
    public async Task DeniedSavePreservesLastCheckpointAndPendingDelivery()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        const string group = RespireJobQueue<string>.ConsumerGroup;
        await admin.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
        var identity = Guid.NewGuid().ToString("N");
        var key = queue.StreamKey + ":checkpoint:" + identity;
        var entry = await admin.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "job"), ("_respire.job.checkpoint", identity));
        await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner");
        await admin.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
            [group, "owner", entry.Value, 1, "saved", Array.Empty<byte>(), 50]);
        var user = "checkpoint-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-set"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
                [group, "owner", entry.Value, 1, "denied", Array.Empty<byte>(), 0])).Throws<RespireServerException>();
            await Assert.That((await RespireJobCheckpoints.ReadAsync(admin, queue.StreamKey, entry))!.Cursor).IsEqualTo("saved");
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, group)).Count).IsEqualTo(1);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    private static RespireScript SaveScript() => (RespireScript)typeof(RespireJob<>).Assembly
        .GetType("Respire.Streaming.JobCheckpoints")!.GetField("Save", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Test]
    public async Task EveryCompletionVariantFitsRedisLuaCompilerLimits()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var assembly = typeof(RespireJob<>).Assembly;
        var wrap = assembly.GetType("Respire.Streaming.JobCheckpoints")!
            .GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var type in new[] { "JobQueueScripts", "JobGroupControls" })
        foreach (var name in new[] { "Complete", "CompleteAndDelete", "CompleteWithMetrics", "CompleteAndDeleteWithMetrics" })
        {
            var inner = (RespireScript)assembly.GetType("Respire.Streaming." + type)!
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var wrapped = (RespireScript)wrap.Invoke(null, [inner])!;
            using var loaded = await client.ExecuteAsync("SCRIPT", ["LOAD", wrapped.Source]);
            await Assert.That(loaded.AsString().Length).IsEqualTo(40);
        }
    }

    [Test]
    public async Task DeniedCleanupCannotAcknowledgeOrLoseCheckpoint()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        const string group = RespireJobQueue<string>.ConsumerGroup;
        await admin.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
        var identity = Guid.NewGuid().ToString("N");
        var key = queue.StreamKey + ":checkpoint:" + identity;
        var entry = await admin.Streams.AddAsync(queue.StreamKey, ("_respire.job.id", "job"), ("_respire.job.checkpoint", identity));
        await admin.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner");
        await admin.Scripts.ExecuteIntegerAsync(SaveScript(), [queue.StreamKey, key],
            [group, "owner", entry.Value, 1, "saved", Array.Empty<byte>(), 50]);
        var assembly = typeof(RespireJob<>).Assembly;
        var complete = (RespireScript)assembly.GetType("Respire.Streaming.JobQueueScripts")!
            .GetField("Complete", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var script = (RespireScript)assembly.GetType("Respire.Streaming.JobCheckpoints")!
            .GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [complete])!;
        RespireKey[] keys = [queue.StreamKey, queue.EventsStreamKey, key];
        RespireValue[] arguments = [group, "owner", entry.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 0];
        var user = "cleanup-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-del"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(script, keys, arguments)).Throws<RespireServerException>();
            await Assert.That((await RespireJobCheckpoints.ReadAsync(admin, queue.StreamKey, entry))!.Cursor).IsEqualTo("saved");
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, group)).Count).IsEqualTo(1);
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That(await admin.Scripts.ExecuteIntegerAsync(script, keys, arguments)).IsEqualTo(1);
            await Assert.That(await RespireJobCheckpoints.ReadAsync(admin, queue.StreamKey, entry)).IsNull();
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RuntimeSlicesPreserveCheckpointAndFailureBudget(bool grouped)
    {
        await using var root = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new()
        {
            MaximumRuntime = TimeSpan.FromMilliseconds(250), Group = grouped ? "group" : null,
        });
        var cursors = new ConcurrentQueue<int>();
        var handler = new Handler(async (job, token) =>
        {
            var saved = await job.ReadCheckpointAsync(token);
            var cursor = saved is null ? 0 : int.Parse(saved.Cursor);
            cursors.Enqueue(cursor);
            if (cursor == 3) return RespireStreamWorkerResult.Ack;
            await Assert.That(await job.SaveCheckpointAsync((cursor + 1).ToString(), [0, 255, (byte)cursor], cursor * 25, token)).IsTrue();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RespireStreamWorkerResult.Ack;
        });
        await using var provider = Worker(client, handler, "slices", deliveryLimit: 1);
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Until(async () => await client.Streams.CountAsync(queue.EventsStreamKey) == 1, deadline.Token);
            await Assert.That(cursors.ToArray()).IsEquivalentTo(new[] { 0, 1, 2, 3 });
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
            var entries = await client.Streams.RangeAsync(queue.StreamKey);
            foreach (var entry in entries)
                await Assert.That(await RespireJobCheckpoints.ReadAsync(client, queue.StreamKey, entry.Id)).IsNull();
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [NotInParallel]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExplicitResultAfterRuntimeExpiryKeepsCompletionPolicy(int completion)
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new() { MaximumRuntime = TimeSpan.FromMilliseconds(250) });
        var calls = 0;
        RespireJob<string>? delivered = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var provider = Worker(client, new(async (job, token) =>
        {
            Interlocked.Increment(ref calls);
            delivered = job;
            await job.SaveCheckpointAsync("finished", [], cancellationToken: deadline.Token);
            var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => expired.TrySetResult());
            // Observe that the runtime elapsed without throwing cancellation from the handler.
            await expired.Task.WaitAsync(deadline.Token);
            return completion switch
            {
                0 => RespireStreamWorkerResult.Ack,
                1 => RespireStreamWorkerResult.Nack,
                2 => RespireStreamWorkerResult.DeadLetter,
                _ => job.RetryAfter(TimeSpan.FromHours(1)),
            };
        }), "late-result", deliveryLimit: completion == 3 ? 5 : 1);
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(deadline.Token);
        try
        {
            if (completion == 3)
            {
                await Until(async () => (await client.Streams.RangeAsync(queue.StreamKey + ":scheduled"))
                    .Any(entry => entry.GetString("_respire.job.attempts") == "1"), deadline.Token);
                var scheduled = (await client.Streams.RangeAsync(queue.StreamKey + ":scheduled")).Single();
                await Assert.That(scheduled.GetString("_respire.job.attempts")).IsEqualTo("1");
                using var due = await client.ExecuteAsync("ZSCORE", [queue.StreamKey + ":due", scheduled.Id.Value]);
                using var time = await client.ExecuteAsync("TIME");
                var remaining = double.Parse(due.AsString(), System.Globalization.CultureInfo.InvariantCulture)
                    - long.Parse(time[0].AsString()) * 1000;
                await Assert.That(remaining).IsGreaterThan(TimeSpan.FromMinutes(59).TotalMilliseconds);
                await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            }
            else
            {
                await Until(async () => await client.Streams.CountAsync(queue.EventsStreamKey) == 1, deadline.Token);
                await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(completion == 0 ? 0 : 1);
                await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count).IsEqualTo(0);
            }
            await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
            var checkpoint = await delivered!.ReadCheckpointAsync(deadline.Token);
            if (completion == 0) await Assert.That(checkpoint).IsNull();
            else await Assert.That(checkpoint!.Cursor).IsEqualTo("finished");
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    public async Task RecoveryOnAnotherWorkerReadsLastSaveAndFencesOldOwner()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new() { MaximumRuntime = TimeSpan.FromMinutes(1) });
        var firstSaved = new TaskCompletionSource<RespireJob<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<RespireJob<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = Worker(client, new(async (job, token) =>
        {
            await job.SaveCheckpointAsync("42", [0, 255, 42], 60, token);
            firstSaved.TrySetResult(job);
            await release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        }), "lost", idle: TimeSpan.FromHours(1));
        await using var second = Worker(client, new(async (job, token) =>
        {
            recovered.TrySetResult(job);
            await release.Task.WaitAsync(token);
            return RespireStreamWorkerResult.Ack;
        }), "replacement", idle: TimeSpan.FromMilliseconds(20));
        var firstWorker = first.GetRequiredService<IHostedService>();
        var secondWorker = second.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await firstWorker.StartAsync(deadline.Token);
        try
        {
            var stale = await firstSaved.Task.WaitAsync(deadline.Token);
            await secondWorker.StartAsync(deadline.Token);
            var current = await recovered.Task.WaitAsync(deadline.Token);
            var checkpoint = (await current.ReadCheckpointAsync(deadline.Token))!;
            await Assert.That(checkpoint.Cursor).IsEqualTo("42");
            await Assert.That(checkpoint.State.ToArray()).IsEquivalentTo(new byte[] { 0, 255, 42 });
            await Assert.That(checkpoint.Percent).IsEqualTo(60);
            await Assert.That(await stale.SaveCheckpointAsync("stale", [], cancellationToken: deadline.Token)).IsFalse();
            await Assert.That(await current.SaveCheckpointAsync("43", [], cancellationToken: deadline.Token)).IsTrue();
        }
        finally
        {
            release.TrySetResult();
            await firstWorker.StopAsync(deadline.Token);
            await secondWorker.StopAsync(deadline.Token);
        }
    }

    [Test]
    public async Task ShutdownSavesFinalCheckpointAndRequeuesWithoutFailure()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new() { MaximumRuntime = TimeSpan.FromMinutes(1) });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = Worker(client, new(async (job, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await job.SaveCheckpointAsync("shutdown", [7], 70); }
            return RespireStreamWorkerResult.Ack;
        }), "first", deliveryLimit: 1);
        var worker = first.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        await worker.StopAsync(deadline.Token);
        var resumed = new TaskCompletionSource<RespireJobCheckpoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var second = Worker(client, new(async (job, token) =>
        {
            resumed.TrySetResult(await job.ReadCheckpointAsync(token));
            return RespireStreamWorkerResult.Ack;
        }), "second", deliveryLimit: 1);
        worker = second.GetRequiredService<IHostedService>();
        await worker.StartAsync(deadline.Token);
        try
        {
            await Assert.That((await resumed.Task.WaitAsync(deadline.Token))!.Cursor).IsEqualTo("shutdown");
            await Until(async () => await client.Streams.CountAsync(queue.EventsStreamKey) == 1, deadline.Token);
            await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    public async Task CheckpointsSurviveOrdinaryRetryAndValidateLimits()
    {
        await using var client = await RespireClient.ConnectAsync(redis.IsolatedConnectionString);
        var queue = Queue(client);
        await queue.EnqueueScheduledAsync("job", "payload", new() { MaximumRuntime = TimeSpan.FromMinutes(1) });
        var calls = 0;
        var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = Worker(client, new(async (job, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Assert.That(async () => await job.SaveCheckpointAsync("cursor", new byte[4097])).Throws<ArgumentOutOfRangeException>();
                await Assert.That(async () => await job.SaveCheckpointAsync(new string('x', 1025), [])).Throws<ArgumentOutOfRangeException>();
                await Assert.That(async () => await job.SaveCheckpointAsync("cursor", [], 101)).Throws<ArgumentOutOfRangeException>();
                await job.SaveCheckpointAsync("retry", [], cancellationToken: token);
                return job.RetryAfter(TimeSpan.Zero);
            }
            await Assert.That((await job.ReadCheckpointAsync(token))!.Cursor).IsEqualTo("retry");
            passed.TrySetResult();
            return RespireStreamWorkerResult.Ack;
        }), "retry");
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try { await passed.Task.WaitAsync(deadline.Token); }
        finally { await worker.StopAsync(deadline.Token); }
    }

    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "checkpoints", Encoding.UTF8.GetBytes);

    private static ServiceProvider Worker(IRespireClient client, Handler handler, string consumer, int deliveryLimit = 5, TimeSpan? idle = null)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(handler);
        services.AddRespireJobQueue<Handler, string>("checkpoints", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerName = consumer, DeliveryLimit = deliveryLimit, ReadWait = TimeSpan.FromMilliseconds(20),
                MinimumIdleTime = idle ?? TimeSpan.FromSeconds(30), RecoveryPollInterval = TimeSpan.FromMilliseconds(20) },
            new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(10) });
        return services.BuildServiceProvider();
    }

    private static async Task Until(Func<Task<bool>> predicate, CancellationToken token)
    {
        while (!await predicate()) await Task.Delay(10, token);
    }

    public sealed class Handler(Func<RespireJob<string>, CancellationToken, ValueTask<RespireStreamWorkerResult>> handle)
        : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> message, CancellationToken cancellationToken)
            => handle(message, cancellationToken);
    }
}
