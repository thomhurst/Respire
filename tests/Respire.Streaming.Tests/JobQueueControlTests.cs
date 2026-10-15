using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
public class JobQueueControlTests(RedisTestContainer redis)
{
    private const string Group = RespireJobQueue<string>.ConsumerGroup;

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PauseIsDurableIdempotentAndEmitsTypedEvents(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
            with { Protocol = (RespireProtocol)protocol });
        var queue = Queue(client.WithKeyPrefix("tenant:"));
        await Assert.That(await queue.ResumeAsync()).IsFalse();
        await Assert.That(await queue.PauseAsync()).IsTrue();
        await Assert.That(await queue.PauseAsync()).IsFalse();
        await using var other = await RespireClient.ConnectAsync(redis.ConnectionString);
        var restarted = Queue(other.WithKeyPrefix("tenant:"));
        await Assert.That((await restarted.GetStateAsync()).IsPaused).IsTrue();
        await Assert.That(await restarted.ResumeAsync()).IsTrue();
        await Assert.That(await restarted.ResumeAsync()).IsFalse();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
        foreach (var kind in new[] { "paused", "resumed" })
        {
            await Assert.That(await events.MoveNextAsync()).IsTrue();
            await Assert.That(events.Current.Kind).IsEqualTo(kind);
            await Assert.That(events.Current.JobId).IsEqualTo("");
            await Assert.That(events.Current.EntryId.Value).IsEqualTo("0-0");
            await Assert.That(events.Current.Attempt).IsEqualTo(0);
        }
        await Assert.That(await client.WithKeyPrefix("tenant:").Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(2);
        await Assert.That((await Queue(client).GetStateAsync()).IsPaused).IsFalse();
    }

    [Test]
    [Arguments("Read")]
    [Arguments("Replay")]
    [Arguments("Claim")]
    public async Task PausedAdmissionDoesNotReadReplayOrRecover(string operation)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync("job", "payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        if (operation != "Read") await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        await queue.PauseAsync();
        using (var reply = await Admit(client, queue, operation))
        {
            await Assert.That(reply[1].Count).IsEqualTo(0);
            await Assert.That(reply[2].AsInteger()).IsEqualTo(1);
        }
        var pending = await client.Streams.PendingAsync(queue.StreamKey, Group);
        await Assert.That(pending.Length).IsEqualTo(operation == "Read" ? 0 : 1);
        if (pending.Length > 0) await Assert.That(pending[0].DeliveryCount).IsEqualTo(1);
        await queue.ResumeAsync();
        using var resumed = await Admit(client, queue, operation);
        await Assert.That(resumed[1].Count).IsEqualTo(1);
        await Assert.That(resumed[1][0][2].AsString()).IsEqualTo(operation == "Read" ? "1" : "2");
    }

    [Test]
    public async Task ConcurrentClientsShareOneBudgetAndDeniedClaimsDoNotConsumeRetries()
    {
        await using var first = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var second = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(first);
        await queue.SetRateLimitAsync(new() { PermitLimit = 3, Window = TimeSpan.FromHours(1) });
        await first.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        for (var i = 0; i < 20; i++) await queue.EnqueueAsync(i.ToString(), "payload");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var result = await Admit(i % 2 == 0 ? first : second, queue, "Read");
            return result[1].Count;
        }));
        await Assert.That(results.Sum()).IsEqualTo(3);
        await Assert.That((await queue.GetStateAsync()).RateLimitHits).IsEqualTo(17);
        var pending = await first.Streams.PendingAsync(queue.StreamKey, Group);
        await Assert.That(pending.Length).IsEqualTo(3);
        foreach (var entry in pending) await Assert.That(entry.DeliveryCount).IsEqualTo(1);
        // A policy update must not reset the current window's usage.
        await queue.SetRateLimitAsync(new() { PermitLimit = 3, Window = TimeSpan.FromHours(2) });
        using (var denied = await Admit(first, queue, "Read")) await Assert.That(denied[1].Count).IsEqualTo(0);
        await queue.SetRateLimitAsync(null);
        using var allowed = await Admit(second, queue, "Read");
        await Assert.That(allowed[1].Count).IsEqualTo(1);
        await Assert.That(allowed[1][0][2].AsString()).IsEqualTo("1");
    }

    [Test]
    public async Task EmptyReadsDoNotConsumePermitsAndWindowRefills()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        await queue.SetRateLimitAsync(new() { PermitLimit = 1, Window = TimeSpan.FromMilliseconds(150) });
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        using (var empty = await Admit(client, queue, "Read")) await Assert.That(empty[1].Count).IsEqualTo(0);
        await queue.EnqueueAsync("one", "payload");
        using (var first = await Admit(client, queue, "Read")) await Assert.That(first[1].Count).IsEqualTo(1);
        await queue.EnqueueAsync("two", "payload");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Until(async () =>
        {
            using var result = await Admit(client, queue, "Read");
            return result[1].Count == 1;
        }, deadline.Token);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("Replay")]
    [Arguments("Claim")]
    public async Task RateDeniedRecoveryKeepsOwnershipAndDeliveryCount(string operation)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        await client.Streams.CreateGroupAsync(queue.StreamKey, Group, RespireStreamId.Beginning);
        await queue.EnqueueAsync("pending", "payload");
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, Group, "owner");
        await queue.SetRateLimitAsync(new() { PermitLimit = 1, Window = TimeSpan.FromHours(1) });
        await queue.EnqueueAsync("permit", "payload");
        using (await Admit(client, queue, "Read")) { }
        using var denied = await Admit(client, queue, operation);
        await Assert.That(denied[1].Count).IsEqualTo(0);
        await Assert.That(denied[3].AsInteger()).IsGreaterThan(0);
        foreach (var entry in await client.Streams.PendingAsync(queue.StreamKey, Group))
        {
            await Assert.That(entry.Consumer).IsEqualTo("owner");
            await Assert.That(entry.DeliveryCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ConflictingStartupPolicyCannotOverwriteSharedConfiguration()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        await queue.SetRateLimitAsync(new() { PermitLimit = 2 });
        var script = ControlScript("Configure");
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [queue.ControlKey], [2, 1000, "initialize"])).IsEqualTo(0);
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(script, [queue.ControlKey], [3, 1000, "initialize"]))
            .Throws<RespireServerException>();
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(script, [queue.ControlKey], [2, 2000, "initialize"]))
            .Throws<RespireServerException>();
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [queue.ControlKey], [2, 1000, "initialize"])).IsEqualTo(0);
    }

    [Test]
    [Arguments("hset")]
    [Arguments("hdel")]
    [Arguments("xadd")]
    public async Task DeniedPauseDoesNotChangeStateOrAppendAnEvent(string command)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with { AllowAdmin = true });
        var user = "controls-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(user, ["on", ">password", "~*", "+@all", "-" + command]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString)
                with { Username = user, Password = "password" });
            await Assert.That(async () => await Queue(restricted).PauseAsync()).Throws<RespireServerException>();
            await Assert.That((await Queue(admin).GetStateAsync()).IsPaused).IsFalse();
            await Assert.That(await admin.Streams.CountAsync(Queue(admin).EventsStreamKey)).IsEqualTo(0);
        }
        finally { await admin.Server.AclDeleteUsersAsync([user]); }
    }

    [Test]
    public async Task FailedEventAppendRollsBackPause()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        using (await client.ExecuteAsync("SET", [queue.EventsStreamKey, "wrong-type"])) { }
        await Assert.That(async () => await queue.PauseAsync()).Throws<RespireServerException>();
        await Assert.That((await queue.GetStateAsync()).IsPaused).IsFalse();
    }

    [Test, NotInParallel]
    public async Task IndependentWorkersRespectPauseAndRatePolicyAndPublishMetrics()
    {
        var metrics = new ConcurrentQueue<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName
                && instrument.Name.StartsWith("respire.job.queue.")) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => metrics.Enqueue((instrument.Name, value)));
        listener.Start();
        await using var first = await RespireClient.ConnectAsync(redis.ConnectionString);
        await using var second = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(first);
        await queue.PauseAsync();
        for (var i = 0; i < 8; i++) await queue.EnqueueAsync(i.ToString(), "payload");
        var state = new HandlerState();
        await using var provider1 = Worker(first, state, "one", new() { PermitLimit = 2, Window = TimeSpan.FromHours(1) });
        await using var provider2 = Worker(second, state, "two");
        var workers = new[] { provider1.GetRequiredService<IHostedService>(), provider2.GetRequiredService<IHostedService>() };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var worker in workers) await worker.StartAsync(deadline.Token);
        try
        {
            await Until(() =>
            {
                listener.RecordObservableInstruments();
                return Task.FromResult(metrics.Any(sample => sample == ("respire.job.queue.paused", 1)));
            }, deadline.Token);
            await Assert.That(state.Jobs.Count).IsEqualTo(0);
            await queue.ResumeAsync();
            await Until(async () => (await queue.GetStateAsync()).RateLimitHits > 0 && state.Jobs.Count == 2
                && metrics.Any(sample => sample == ("respire.job.queue.rate_limit_hits", 1)), deadline.Token);
            await Assert.That(metrics.Any(sample => sample == ("respire.job.queue.rate_limit_hits", 1))).IsTrue();
            await queue.PauseAsync();
            await queue.SetRateLimitAsync(null);
            await Task.Delay(100, deadline.Token);
            await Assert.That(state.Jobs.Count).IsEqualTo(2);
            await queue.ResumeAsync();
            await Until(async () => (await first.Streams.ReadAsync(queue.EventsStreamKey)).Count(e => e.GetString("kind") == "completed") == 8,
                deadline.Token);
            var events = await first.Streams.ReadAsync(queue.EventsStreamKey);
            foreach (var entry in events.Where(e => e.GetString("kind") == "completed"))
                await Assert.That(entry.GetString("attempt")).IsEqualTo("1");
            await Assert.That(state.Jobs.Distinct().Count()).IsEqualTo(8);
            await Assert.That(await first.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
        }
        finally { foreach (var worker in workers) await worker.StopAsync(deadline.Token); }
    }

    [Test]
    public async Task PauseLetsInFlightHandlerFinishWithoutClaimingTheRestOfABatch()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = Queue(client);
        await queue.EnqueueAsync("first", "payload");
        await queue.EnqueueAsync("second", "payload");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new HandlerState { Handle = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        await using var provider = Worker(client, state, "drain");
        var worker = provider.GetRequiredService<IHostedService>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StartAsync(deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await queue.PauseAsync();
            release.TrySetResult();
            await Until(async () => (await client.Streams.PendingSummaryAsync(queue.StreamKey, Group)).Count == 0, deadline.Token);
            await Task.Delay(100, deadline.Token);
            await Assert.That(state.Jobs.Count).IsEqualTo(1);
            await queue.ResumeAsync();
            await Until(() => Task.FromResult(state.Jobs.Count == 2), deadline.Token);
        }
        finally { release.TrySetResult(); await worker.StopAsync(deadline.Token); }
    }

    private static ServiceProvider Worker(IRespireClient client, HandlerState state, string name, RespireJobRateLimit? rate = null)
    {
        var services = new ServiceCollection().AddSingleton(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("controls", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
            new() { ConsumerName = name, BatchSize = 10, DeliveryLimit = 1, ReadWait = TimeSpan.FromMilliseconds(20) },
            new() { SchedulingPollInterval = TimeSpan.FromMilliseconds(20), RateLimit = rate });
        return services.BuildServiceProvider();
    }

    public sealed class HandlerState
    {
        public ConcurrentQueue<string> Jobs { get; } = new();
        public Func<CancellationToken, Task> Handle { get; init; } = _ => Task.CompletedTask;
    }

    public sealed class Handler(HandlerState state) : IRespireStreamHandler<RespireJob<string>>
    {
        public async ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            state.Jobs.Enqueue(job.Id);
            await state.Handle(cancellationToken);
            return RespireStreamWorkerResult.Ack;
        }
    }

    private static RespireJobQueue<string> Queue(IRespireClient client) => new(client, "controls", Encoding.UTF8.GetBytes);
    private static async Task Until(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition()) await Task.Delay(10, token);
    }

    internal static ValueTask<RespireResult> Admit(IRespireClient client, RespireJobQueue<string> queue, string operation)
    {
        RespireValue[] args = operation == "Replay" ? [Group, "owner", 1, "0-0"]
            : operation == "Claim" ? [Group, "recovery", 0, "0-0", 1] : [Group, "owner"];
        return client.Scripts.ExecuteAsync(ControlScript(operation), [queue.StreamKey, queue.ControlKey], args);
    }

    private static RespireScript ControlScript(string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobQueueControls")!.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
