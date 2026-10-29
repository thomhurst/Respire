using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class JobQueueIntegrationTests
{
    [Test]
    public async Task DeserializationFailurePublishesTerminalFailureWithoutInvokingHandler()
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        await queue.EnqueueAsync("poison", "secret");
        var state = new HandlerState();
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("test", System.Text.Encoding.UTF8.GetBytes,
            _ => throw new FormatException("secret exception"), new() { DeliveryLimit = 1, ReadWait = TimeSpan.FromMilliseconds(20) });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try
        {
            await using var reader = queue.ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator();
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Kind).IsEqualTo("dead-letter");
            await Assert.That(reader.Current.Reason).IsEqualTo("processing-failed");
            await Assert.That(state.Attempts).IsEqualTo(0);
            var entry = (await client.Streams.ReadAsync(queue.EventsStreamKey)).Single();
            await Assert.That(entry.Fields.Any(field => System.Text.Encoding.UTF8.GetString(field.Value).Contains("secret"))).IsFalse();
        }
        finally { await worker.StopAsync(deadline.Token); }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task CompletionSurvivesLostReplyAndObserverRestart(int protocol, bool delete)
    {
        await using var client = await ConnectAsync(protocol);
        var queue = Queue(client);
        var delivery = await PendingJobAsync(client, queue);
        var script = JobScript(delete ? "CompleteAndDelete" : "Complete");
        // The consumer sends completion and disappears without acting on its reply.
        await using (var crashed = await ConnectAsync(protocol))
            await crashed.Scripts.ExecuteIntegerAsync(script, [queue.StreamKey, queue.EventsStreamKey],
                [RespireJobQueue<string>.ConsumerGroup, "crashed", delivery.Value, 1, "completed"]);
        // An ambiguous retry cannot publish twice after the atomic acknowledgement.
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, [queue.StreamKey, queue.EventsStreamKey],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", delivery.Value, 1, "completed"])).IsEqualTo(0);
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
            .IsEqualTo(0);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        RespireJobEvent saved;
        await using (var observer = await ConnectAsync(protocol))
        await using (var reader = Queue(observer).ReadEventsAsync(cancellationToken: deadline.Token).GetAsyncEnumerator())
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            saved = reader.Current;
            await Assert.That(saved.EntryId).IsEqualTo(delivery);
            await Assert.That(saved.Kind).IsEqualTo("completed");
            await Assert.That(saved.JobId).IsEqualTo("job");
            await Assert.That(saved.Percent).IsNull();
        }
        var second = await PendingJobAsync(client, queue, "second");
        await client.Scripts.ExecuteIntegerAsync(script, [queue.StreamKey, queue.EventsStreamKey],
            [RespireJobQueue<string>.ConsumerGroup, "crashed", second.Value, 1, "completed"]);
        await using var restarted = await ConnectAsync(protocol);
        await using var resumed = Queue(restarted).ReadEventsAsync(saved.Id, deadline.Token).GetAsyncEnumerator();
        await Assert.That(await resumed.MoveNextAsync()).IsTrue();
        await Assert.That(resumed.Current.EntryId).IsEqualTo(second);
        await Assert.That(resumed.Current.Id).IsNotEqualTo(saved.Id);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedTerminalWriteLeavesDeliveryRecoverableWithoutAnEvent(bool deadLetter)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        var delivery = await PendingJobAsync(client, queue);
        // A dead-letter failure occurs AFTER event append and must compensate it.
        var wrongTypeKey = deadLetter ? queue.DeadLetterStreamKey : queue.EventsStreamKey;
        await client.Strings.SetAsync(wrongTypeKey, "wrong type");
        RespireKey[] keys = deadLetter
            ? [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey]
            : [queue.StreamKey, queue.EventsStreamKey];
        RespireValue[] args = [RespireJobQueue<string>.ConsumerGroup, "crashed", delivery.Value, 1,
            deadLetter ? "dead-letter" : "completed", "explicit", ""];
        await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args))
            .Throws<RespireServerException>();
        await Assert.That((await client.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
            .IsEqualTo(1);
        await client.Keys.DeleteAsync(wrongTypeKey);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
        await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobScript("Complete"), keys, args)).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(1);
        if (deadLetter) await Assert.That(await client.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(1);
    }

    [Test]
    [Arguments("xack")]
    [Arguments("xadd")]
    [Arguments("xdel")]
    public async Task DeniedTerminalWritePreservesPendingDelivery(string denied)
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var queue = Queue(admin);
        var delivery = await PendingJobAsync(admin, queue);
        var username = "events-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">event-test-password", "~*", "+@all", "-" + denied]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "event-test-password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(JobScript("Complete"),
                [queue.StreamKey, queue.EventsStreamKey, queue.DeadLetterStreamKey],
                [RespireJobQueue<string>.ConsumerGroup, "crashed", delivery.Value, 1, "dead-letter", "explicit", ""]))
                .Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync(queue.DeadLetterStreamKey)).IsEqualTo(0);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test]
    public async Task FailedNativeAcknowledgementRollsBackItsAlreadyAppendedEvent()
    {
        await using var admin = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString) with { AllowAdmin = true });
        var info = await admin.Server.InfoAsync("server");
        var line = info.Split('\n').Single(value => value.StartsWith("redis_version:"));
        var version = Version.Parse(line["redis_version:".Length..].Trim().Split('-')[0]);
        if (version < new Version(8, 2)) return; // XACKDEL is exercised by the Redis 8.2+ matrix.
        var queue = Queue(admin);
        var delivery = await PendingJobAsync(admin, queue);
        var username = "ack-events-" + Guid.NewGuid().ToString("N");
        await admin.Server.AclSetUserAsync(username, ["on", ">event-test-password", "~*", "+@all", "-xackdel"]);
        try
        {
            await using var restricted = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.IsolatedConnectionString)
                with { Username = username, Password = "event-test-password" });
            await Assert.That(async () => await restricted.Scripts.ExecuteIntegerAsync(JobScript("CompleteAndDelete"),
                [queue.StreamKey, queue.EventsStreamKey],
                [RespireJobQueue<string>.ConsumerGroup, "crashed", delivery.Value, 1, "completed"]))
                .Throws<RespireServerException>();
            await Assert.That(await admin.Streams.CountAsync(queue.EventsStreamKey)).IsEqualTo(0);
            await Assert.That(await admin.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
            await Assert.That((await admin.Streams.PendingSummaryAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup)).Count)
                .IsEqualTo(1);
        }
        finally { using var removed = await admin.ExecuteAsync("ACL DELUSER", [username]); }
    }

    [Test, NotInParallel]
    public async Task EnqueueContextSurvivesDeduplicationCrashAndRetryWithoutMetricPayloads()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireStreamWorkerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var metricTags = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName) meter.EnableMeasurementEvents(instrument);
        };
        metrics.SetMeasurementEventCallback<double>((_, _, tags, _) => metricTags.Enqueue(tags.ToArray()));
        metrics.SetMeasurementEventCallback<long>((_, _, tags, _) => metricTags.Enqueue(tags.ToArray()));
        metrics.Start();
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        using var parent = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        parent.TraceStateString = "vendor=value";
        var delivery = await PendingJobAsync(client, queue, "private-job-id");
        var original = stopped.Single(activity => activity.OperationName == "job enqueue");
        await queue.EnqueueAsync("private-job-id", "replacement-secret");
        var entry = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
        await Assert.That(entry.GetString("traceparent")).IsEqualTo(original.Id);
        await Assert.That(entry.GetString("tracestate")).IsEqualTo("vendor=value");
        var state = new HandlerState { Result = RespireStreamWorkerResult.Nack };
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(state);
        services.AddRespireJobQueue<Handler, string>("test", System.Text.Encoding.UTF8.GetBytes, System.Text.Encoding.UTF8.GetString,
            new() { DeliveryLimit = 3, MinimumIdleTime = TimeSpan.FromMilliseconds(20),
                RecoveryPollInterval = TimeSpan.FromMilliseconds(10), ReadWait = TimeSpan.FromMilliseconds(10) });
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(deadline.Token);
        try { await UntilAsync(() => Task.FromResult(stopped.Count(a => a.OperationName == "job process") == 2), deadline.Token); }
        finally { await worker.StopAsync(deadline.Token); }
        var attempts = stopped.Where(activity => activity.OperationName == "job process").ToArray();
        await Assert.That(attempts.Length).IsEqualTo(2);
        foreach (var attempt in attempts)
        {
            var link = attempt.Links.Single().Context;
            await Assert.That(link.TraceId).IsEqualTo(original.TraceId);
            await Assert.That(link.SpanId).IsEqualTo(original.SpanId);
            await Assert.That(link.TraceState).IsEqualTo("vendor=value");
            await Assert.That(link.IsRemote).IsTrue();
            await Assert.That(attempt.ParentSpanId).IsEqualTo(default(ActivitySpanId));
        }
        await Assert.That(attempts[0].SpanId).IsNotEqualTo(attempts[1].SpanId);
        await Assert.That(metricTags.IsEmpty).IsFalse();
        foreach (var tags in metricTags)
            await Assert.That(tags.All(tag => tag.Key is "respire.worker.name" or "respire.worker.outcome" or "respire.worker.reason"))
                .IsTrue();
        await Assert.That((await client.Streams.ReadAsync(queue.EventsStreamKey)).Single().GetString("entry_id"))
            .IsEqualTo(delivery.Value);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnqueuePropagatesTheActiveProducerOrAmbientW3CContext(bool ambient)
    {
        await using var client = await ConnectAsync();
        Activity? serializedUnder = null;
        var queue = new RespireJobQueue<string>(client, "test", payload =>
        {
            serializedUnder = Activity.Current;
            return System.Text.Encoding.UTF8.GetBytes(payload);
        });
        var previous = Activity.Current;
        Activity.Current = null;
        using var parent = ambient ? new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        try
        {
            await queue.EnqueueAsync("job", "secret-payload");
            var entry = (await client.Streams.ReadAsync(queue.StreamKey)).Single();
            // TUnit's HTML reporter can independently sample every source.
            await Assert.That(entry.GetString("traceparent")).IsEqualTo(serializedUnder?.Id);
            if (serializedUnder != parent)
                await Assert.That(serializedUnder!.ParentSpanId).IsEqualTo(parent?.SpanId ?? default);
            await Assert.That(Activity.Current).IsSameReferenceAs(parent);
        }
        finally { parent?.Dispose(); Activity.Current = previous; }
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnsampledProducerForwardsAmbientContextWithoutCreatingAnActivity(bool ambient)
    {
        var previous = Activity.Current;
        Activity.Current = null;
        using var parent = ambient ? new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        try
        {
            // Explicitly omit the source to exercise no-listener behavior even when the test reporter samples all sources.
            var type = typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobEnqueueTrace")!;
            using var trace = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: new object?[] { null }, culture: null)!;
            await Assert.That((string)type.GetProperty("Parent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(trace)!)
                .IsEqualTo(parent?.Id ?? "");
            await Assert.That(Activity.Current).IsSameReferenceAs(parent);
        }
        finally { parent?.Dispose(); Activity.Current = previous; }
    }

    [Test, NotInParallel]
    [Arguments("sample")]
    [Arguments("started")]
    [Arguments("stopped")]
    public async Task ProducerListenerFailuresDoNotChangeEnqueueOrAmbientContext(string failure)
    {
        await using var client = await ConnectAsync();
        var queue = Queue(client);
        using var parent = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireStreamWorkerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => failure == "sample"
                ? throw new InvalidOperationException() : ActivitySamplingResult.AllData,
            ActivityStarted = _ => { if (failure == "started") throw new InvalidOperationException(); },
            ActivityStopped = _ => { if (failure == "stopped") throw new InvalidOperationException(); },
        };
        ActivitySource.AddActivityListener(listener);
        await Assert.That((await queue.EnqueueAsync("job", "secret")).Enqueued).IsTrue();
        await Assert.That((await queue.EnqueueAsync("job", "replacement")).Enqueued).IsFalse();
        await Assert.That(Activity.Current).IsSameReferenceAs(parent);
        await Assert.That(await client.Streams.CountAsync(queue.StreamKey)).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task ProducerUsesW3CEvenUnderAHierarchicalActivity()
    {
        await using var client = await ConnectAsync();
        using var parent = new Activity("legacy").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireStreamWorkerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var queue = Queue(client);
        await queue.EnqueueAsync("job", "payload");
        var producer = stopped.Single();
        await Assert.That(producer.IdFormat).IsEqualTo(ActivityIdFormat.W3C);
        await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single().GetString("traceparent")).IsEqualTo(producer.Id);
        await Assert.That(Activity.Current).IsSameReferenceAs(parent);
    }

    internal static RespireScript JobScript(string name) => (RespireScript)typeof(RespireJobQueue<string>).Assembly
        .GetType("Respire.Streaming.JobQueueScripts")!.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static async Task<RespireStreamId> PendingJobAsync(IRespireClient client, RespireJobQueue<string> queue, string id = "job")
    {
        var enqueued = await queue.EnqueueAsync(id, "secret-payload");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "crashed", new() { Count = 1 });
        return enqueued.EntryId;
    }
}
