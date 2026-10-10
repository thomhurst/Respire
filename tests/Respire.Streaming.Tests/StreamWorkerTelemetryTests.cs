using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class StreamWorkerTests
{
    private const string WorkerName = "telemetry-control";
    private const string Parent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TelemetryRetriesKeepProducerParentAndCountConfirmedDeadLetter(bool handlerThrows)
    {
        using var metrics = new WorkerMetrics();
        var contexts = new ConcurrentQueue<ActivityContext>();
        var children = new ConcurrentQueue<ActivitySpanId>();
        var stopped = new ConcurrentQueue<Activity>();
        using var tracing = ListenToWorker(stopped.Enqueue);
        var state = new State { Handle = (_, _) =>
        {
            contexts.Enqueue(Activity.Current!.Context);
            using var child = new Activity("handler child").Start();
            children.Enqueue(child.ParentSpanId);
            if (handlerThrows) throw new InvalidOperationException("private payload");
            return ValueTask.FromResult(RespireStreamWorkerResult.Nack);
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, keyPrefix: "{worker}tenant:", options: new()
        {
            TelemetryName = WorkerName, DeadLetterStream = "dlq", DeliveryLimit = 3,
            TraceParentField = "parent", TraceStateField = "state",
            MinimumIdleTime = TimeSpan.FromMilliseconds(20), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
        });
        await fixture.View.Streams.AddAsync("events", ("payload", "private payload"), ("parent", Parent), ("state", "vendor=value"));
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(metrics.Duration.Count == 3 && stopped.Count == 3));
        await fixture.StopAsync();
        await Assert.That(contexts.Count).IsEqualTo(3);
        await Assert.That(contexts.Select(context => context.SpanId).Distinct().Count()).IsEqualTo(3);
        foreach (var activity in stopped)
        {
            await Assert.That(activity.TraceId.ToHexString()).IsEqualTo("0123456789abcdef0123456789abcdef");
            await Assert.That(activity.ParentSpanId.ToHexString()).IsEqualTo("0123456789abcdef");
            await Assert.That(activity.TraceStateString).IsEqualTo("vendor=value");
            await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Consumer);
            await Assert.That(activity.Duration).IsGreaterThan(TimeSpan.Zero);
            await Assert.That(children.Contains(activity.SpanId)).IsTrue();
            await Assert.That(activity.Status).IsEqualTo(handlerThrows ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
            await Assert.That(activity.StatusDescription).IsNull();
        }
        await Assert.That(stopped.Select(activity => activity.GetTagItem("respire.worker.outcome")))
            .IsEquivalentTo(new object?[] { "nack", "nack", "dead-letter" });
        await Assert.That(metrics.Duration.Select(sample => sample.Tags["respire.worker.outcome"]))
            .IsEquivalentTo(new object?[] { "nack", "nack", "dead-letter" });
        await Assert.That(metrics.DeadLetters.Single().Value).IsEqualTo(1);
        await Assert.That(metrics.DeadLetters.Single().Tags["respire.worker.reason"])
            .IsEqualTo(handlerThrows ? "processing-failed" : "nack");
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
        await Assert.That(await fixture.View.Streams.CountAsync("dlq")).IsEqualTo(1);
        await Assert.That(state.DisposedScopes).IsEqualTo(3);
        foreach (var sample in metrics.Duration.Concat(metrics.DeadLetters))
        {
            await Assert.That(sample.Value).IsGreaterThanOrEqualTo(0);
            await Assert.That(sample.Tags.Count).IsEqualTo(2);
            await Assert.That(sample.Tags.Values.Contains("private payload")).IsFalse();
        }
    }

    [Test, NotInParallel]
    public async Task TelemetryDeserializationFailuresKeepErrorStatusAndCompletionOutcome()
    {
        using var metrics = new WorkerMetrics();
        var stopped = new ConcurrentQueue<Activity>();
        using var tracing = ListenToWorker(stopped.Enqueue);
        await using var fixture = await Fixture.CreateAsync(deserializeFailure: true, keyPrefix: "{worker}tenant:", options: new()
        {
            TelemetryName = WorkerName, DeadLetterStream = "dlq", DeliveryLimit = 3,
            MinimumIdleTime = TimeSpan.FromMilliseconds(20), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
        });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(metrics.Duration.Count == 3 && stopped.Count == 3));
        await fixture.StopAsync();
        foreach (var activity in stopped)
        {
            await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That(activity.StatusDescription).IsNull();
        }
        await Assert.That(stopped.Select(activity => activity.GetTagItem("respire.worker.outcome")))
            .IsEquivalentTo(new object?[] { "nack", "nack", "dead-letter" });
        await Assert.That(metrics.Duration.Select(sample => sample.Tags["respire.worker.outcome"]))
            .IsEquivalentTo(new object?[] { "nack", "nack", "dead-letter" });
        await Assert.That(metrics.DeadLetters.Single().Tags["respire.worker.reason"]).IsEqualTo("processing-failed");
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
        await Assert.That(await fixture.View.Streams.CountAsync("dlq")).IsEqualTo(1);
        await Assert.That(fixture.State.WarningCount).IsEqualTo(3);
        await Assert.That(fixture.State.ScopeIds.Count).IsEqualTo(0);
        await Assert.That(fixture.State.DisposedScopes).IsEqualTo(3);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TelemetryScopeCleanupRetainsAcknowledgementAndReportsFailure(bool cleanupThrows)
    {
        using var metrics = new WorkerMetrics();
        var stopped = new ConcurrentQueue<Activity>();
        using var tracing = ListenToWorker(stopped.Enqueue);
        var failure = new InvalidOperationException("private cleanup payload");
        var state = new State { DisposeScope = () => cleanupThrows
            ? ValueTask.FromException(failure) : ValueTask.CompletedTask };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new() { TelemetryName = WorkerName });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        if (cleanupThrows)
        {
            var actual = await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(actual).IsSameReferenceAs(failure);
        }
        else
        {
            await UntilAsync(() => Task.FromResult(stopped.Count == 1));
            await fixture.StopAsync();
            await Assert.That(fixture.Service.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        }
        var activity = stopped.Single();
        await Assert.That(activity.Status).IsEqualTo(cleanupThrows ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
        await Assert.That(activity.StatusDescription).IsNull();
        await Assert.That(activity.GetTagItem("respire.worker.outcome")).IsEqualTo("ack");
        await Assert.That(metrics.Duration.Single().Tags["respire.worker.outcome"]).IsEqualTo("ack");
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
        await Assert.That(state.DisposedScopes).IsEqualTo(1);
    }

    [Test, NotInParallel]
    [Arguments("bad", "vendor=value", false)]
    [Arguments("00-00000000000000000000000000000000-0123456789abcdef-01", "vendor=value", false)]
    [Arguments("00-0123456789ABCDEF0123456789ABCDEF-0123456789abcdef-01", "vendor=value", false)]
    [Arguments(Parent, "vendor=value,vendor=duplicate", true)]
    [Arguments(Parent, "bad key=value", true)]
    [Arguments(Parent, "vendor=bad=value", true)]
    public async Task TelemetryRejectsInvalidParentOrStateWithoutRejectingMessage(string parent, string state, bool validParent)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var tracing = ListenToWorker(stopped.Enqueue);
        await using var fixture = await Fixture.CreateAsync(options: new() { TelemetryName = WorkerName });
        await fixture.View.Streams.AddAsync("events", ("payload", "0"), ("traceparent", parent), ("tracestate", state));
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(stopped.Count == 1));
        await fixture.StopAsync();
        var activity = stopped.Single();
        await Assert.That(activity.ParentSpanId != default).IsEqualTo(validParent);
        await Assert.That(activity.TraceStateString).IsNull();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    [Arguments("sample")]
    [Arguments("source")]
    [Arguments("started")]
    [Arguments("stopped")]
    [Arguments("metrics")]
    [Arguments("published")]
    [Arguments("disabled")]
    public async Task TelemetryListenerFailuresDoNotChangeRetryOrDeadLetter(string failure)
    {
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != RespireStreamWorkerTelemetry.MeterName) return;
            if (failure == "published") throw new InvalidOperationException("listener failure");
            if (failure != "disabled") listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((_, _, _, _) => { if (failure == "metrics") throw new InvalidOperationException(); });
        meter.SetMeasurementEventCallback<long>((_, _, _, _) => { if (failure == "metrics") throw new InvalidOperationException(); });
        meter.Start();
        var handlerContexts = new ConcurrentQueue<Activity?>();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source =>
            {
                if (source.Name != RespireStreamWorkerTelemetry.ActivitySourceName) return false;
                if (failure == "source") throw new InvalidOperationException();
                return true;
            },
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => failure == "sample"
                ? throw new InvalidOperationException() : failure == "disabled" ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
            ActivityStarted = _ => { if (failure == "started") throw new InvalidOperationException(); },
            ActivityStopped = _ => { if (failure == "stopped") throw new InvalidOperationException(); },
        };
        ActivitySource.AddActivityListener(tracing);
        var ambient = Activity.Current;
        var state = new State { Handle = (_, _) =>
        {
            handlerContexts.Enqueue(Activity.Current);
            throw new InvalidOperationException("private payload");
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, keyPrefix: "{worker}tenant:", options: new()
        {
            TelemetryName = WorkerName, DeadLetterStream = "dlq", DeliveryLimit = 2,
            MinimumIdleTime = TimeSpan.FromMilliseconds(20), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
        });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(async () => await fixture.View.Streams.CountAsync("dlq") == 1);
        await fixture.StopAsync();
        await Assert.That(state.ScopeIds.Count).IsEqualTo(2);
        await Assert.That(state.WarningCount).IsEqualTo(2);
        await Assert.That(fixture.Service.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
        if (failure is "started" or "sample" or "source")
            await Assert.That(handlerContexts.All(context => ReferenceEquals(context, ambient))).IsTrue();
        if (failure == "disabled")
            foreach (var activity in handlerContexts)
                if (!ReferenceEquals(activity, ambient) && activity is not null)
                    await Assert.That(activity.Duration).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(ReferenceEquals(Activity.Current, ambient)).IsTrue();
    }

    [Test, NotInParallel]
    [Arguments("duplicate")]
    [Arguments("oversized")]
    [Arguments("binary")]
    [Arguments("extraction-disabled")]
    public async Task TelemetryUntrustedParentFieldsDoNotBecomeAnActivityParent(string field)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var tracing = ListenToWorker(stopped.Enqueue);
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            TelemetryName = WorkerName, TraceParentField = field == "extraction-disabled" ? null : "traceparent",
        });
        var fields = new List<(string, RespireValue)> { ("payload", "0"), ("traceparent", field switch
        {
            "oversized" => Parent + new string('x', 1000),
            "binary" => Parent[..54] + "\n",
            _ => Parent,
        }) };
        if (field == "duplicate") fields.Add(("traceparent", Parent));
        await fixture.View.Streams.AddAsync("events", fields.ToArray());
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(stopped.Count == 1));
        await fixture.StopAsync();
        await Assert.That(stopped.Single().ParentSpanId == default).IsTrue();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task TelemetryNamesAndFieldNamesAreValidated()
    {
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("s", "g",
            new() { TelemetryName = new string('x', 129) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("s", "g",
            new() { TelemetryName = " " })).Throws<ArgumentException>();
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("s", "g",
            new() { TraceParentField = "same", TraceStateField = "same" })).Throws<ArgumentException>();
    }

    [Test, NotInParallel]
    public async Task TelemetryGroupPollReportsLagAndPendingAndStopsObservingOnShutdown()
    {
        using var metrics = new WorkerMetrics();
        var release = NewSignal();
        var state = new State { Handle = async (_, _) => { await release.Task; return RespireStreamWorkerResult.Ack; } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new()
        {
            TelemetryName = WorkerName, MetricsPollInterval = TimeSpan.FromMilliseconds(20),
        });
        for (var i = 0; i < 4; i++) await fixture.AddAsync(i);
        try
        {
            await fixture.StartAsync();
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await UntilAsync(() =>
            {
                metrics.Listener.RecordObservableInstruments();
                return Task.FromResult(metrics.Lag.Any(sample => sample.Value == 3) && metrics.Pending.Any(sample => sample.Value == 1));
            });
            foreach (var sample in metrics.Lag.Concat(metrics.Pending))
                await Assert.That(sample.Tags.Keys).IsEquivalentTo(new[] { "respire.worker.name" });
            var stopping = fixture.Service.StopAsync(CancellationToken.None);
            metrics.Lag.Clear();
            metrics.Pending.Clear();
            metrics.Listener.RecordObservableInstruments();
            await Assert.That(metrics.Lag.Count + metrics.Pending.Count).IsEqualTo(0);
            release.TrySetResult();
            await stopping.WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    public async Task TelemetryUnknownLagIsAbsentRatherThanZero()
    {
        using var metrics = new WorkerMetrics();
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            TelemetryName = WorkerName, CreateGroup = false, MetricsPollInterval = TimeSpan.FromMilliseconds(20),
        });
        for (var i = 1; i <= 3; i++)
            await fixture.View.Streams.AddAsync("events", new StreamAddOptions { Id = $"{i}-0" }, ("payload", "body"));
        await fixture.View.Streams.CreateGroupAsync("events", "workers", "2-0");
        var gate = new RespireFakeGate();
        using var fault = fixture.Server.InjectFault("XREADGROUP", RespireFakeFault.Pause(gate));
        try
        {
            await fixture.StartAsync();
            await fault.Matched.WaitAsync(Deadline);
            await UntilAsync(() =>
            {
                metrics.Listener.RecordObservableInstruments();
                return Task.FromResult(metrics.Pending.Count > 0);
            });
            await Assert.That(metrics.Lag.Count).IsEqualTo(0);
            await fixture.StopAsync();
        }
        finally { gate.Release(); }
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TelemetryQueryCancellationAndLateRepliesCannotPublishAfterDispose(bool expire)
    {
        using var metrics = new WorkerMetrics();
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            TelemetryName = WorkerName, MetricsPollInterval = TimeSpan.FromMinutes(1),
            MetricsPollTimeout = TimeSpan.FromMilliseconds(expire ? 20 : 5000),
        });
        var gate = new RespireFakeGate();
        using var fault = fixture.Server.InjectFault("XINFO", RespireFakeFault.Pause(gate, afterExecution: true));
        try
        {
            await fixture.StartAsync();
            await fault.Matched.WaitAsync(Deadline);
            fixture.Service.Dispose();
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            gate.Release();
            metrics.Listener.RecordObservableInstruments();
            await Assert.That(metrics.Lag.Count + metrics.Pending.Count).IsEqualTo(0);
            await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        }
        finally { gate.Release(); }
    }

    [Test, NotInParallel]
    public async Task TelemetryWithoutGaugeListenersDoesNotQueryGroupInfo()
    {
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            MetricsPollInterval = TimeSpan.FromMilliseconds(1),
        });
        using var fault = fixture.Server.InjectFault("XINFO", RespireFakeFault.Loading());
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(fixture.State.DisposedScopes == 1));
        await fixture.StopAsync();
        await Assert.That(fault.Matched.IsCompleted).IsFalse();
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TelemetryDeadLetterCountExcludesStaleAttemptsAndDeletedBodies(bool deleted)
    {
        using var metrics = new WorkerMetrics();
        Fixture? fixture = null;
        var state = new State { Handle = async (entry, _) =>
        {
            if (deleted) await fixture!.View.Streams.RemoveAsync("events", [entry.Id]);
            else
            {
                using var claim = await fixture!.View.Scripts.ExecuteAsync(StreamWorkerScripts.Claim, ["events"],
                    ["workers", "other", 0, "0-0", 1]);
            }
            return RespireStreamWorkerResult.DeadLetter;
        } };
        fixture = await Fixture.CreateAsync(state: state, keyPrefix: "{worker}tenant:", options: new()
        {
            TelemetryName = WorkerName, DeadLetterStream = "dlq", RecoveryPollInterval = TimeSpan.FromMinutes(1),
        });
        await using var owned = fixture;
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(metrics.Duration.Count == 1));
        await fixture.StopAsync();
        await Assert.That(metrics.DeadLetters.Count).IsEqualTo(0);
        await Assert.That(metrics.Duration.Single().Tags["respire.worker.outcome"]).IsEqualTo(deleted ? "deleted" : "stale");
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(deleted ? 0 : 1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(TimeSpan.TicksPerMillisecond - 1)]
    [Arguments(long.MaxValue)]
    public async Task TelemetryPollingOptionsAreValidated(long ticks)
    {
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("s", "g",
            new() { MetricsPollInterval = TimeSpan.FromTicks(ticks) })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("s", "g",
            new() { MetricsPollTimeout = TimeSpan.FromTicks(ticks) })).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TelemetryPollingOptionsAcceptOneMillisecond()
    {
        var services = new ServiceCollection();
        services.AddRespireStreamWorker<EntryHandler>("s", "g", new()
        {
            MetricsPollInterval = TimeSpan.FromMilliseconds(1),
            MetricsPollTimeout = TimeSpan.FromMilliseconds(1),
        });
        await Assert.That(services.Count).IsGreaterThan(0);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TelemetryBlockedMeasurementDoesNotBlockShutdownDeadline(bool deadLetter)
    {
        using var metrics = new WorkerMetrics();
        using var release = new ManualResetEventSlim();
        var entered = NewSignal();
        var canceled = NewSignal();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName && instrument.Name ==
                    (deadLetter ? "respire.stream.worker.dead_letters" : "respire.stream.worker.processing.duration"))
                    meter.EnableMeasurementEvents(instrument);
            },
        };
        void Block()
        {
            entered.TrySetResult();
            release.Wait();
        }
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Block());
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Block());
        listener.Start();
        var state = new State { Handle = async (entry, token) =>
        {
            if (entry.GetString("payload") == "0")
                return deadLetter ? RespireStreamWorkerResult.DeadLetter : RespireStreamWorkerResult.Ack;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, keyPrefix: "{worker}tenant:", options: new()
        {
            TelemetryName = WorkerName, ConsumerCount = 2, DeadLetterStream = "dlq",
            MetricsPollInterval = TimeSpan.FromMilliseconds(20),
        });
        Task? stopping = null;
        try
        {
            await fixture.AddAsync(0);
            await fixture.AddAsync(1);
            await fixture.StartAsync();
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await entered.Task.WaitAsync(Deadline);
            stopping = Task.Run(() => fixture.Service.StopAsync(new CancellationToken(canceled: true)));
            await stopping.WaitAsync(Deadline);
            await canceled.Task.WaitAsync(Deadline);
            metrics.Lag.Clear();
            metrics.Pending.Clear();
            await Task.Run(metrics.Listener.RecordObservableInstruments).WaitAsync(Deadline);
            await Assert.That(metrics.Lag.Count + metrics.Pending.Count).IsEqualTo(0);
            await Assert.That(fixture.Service.ExecuteTask!.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            if (stopping is not null) await stopping.WaitAsync(Deadline);
        }
        await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
        await Assert.That(fixture.Service.ExecuteTask.IsCompletedSuccessfully).IsTrue();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task TelemetryBlockedTeardownDoesNotDelayWorkerCancellation()
    {
        using var metrics = new WorkerMetrics();
        using var release = new ManualResetEventSlim();
        var entered = NewSignal();
        var canceled = NewSignal();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName
                    && instrument.Name == "respire.stream.worker.processing.duration")
                    meter.EnableMeasurementEvents(instrument);
            },
            MeasurementsCompleted = (_, _) =>
            {
                entered.TrySetResult();
                release.Wait();
            },
        };
        listener.Start();
        var state = new State { Handle = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(); throw; }
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new()
        {
            TelemetryName = WorkerName, ConsumerCount = 2, MetricsPollInterval = TimeSpan.FromMilliseconds(20),
        });
        Task? disposing = null;
        try
        {
            await fixture.AddAsync(0);
            await fixture.StartAsync();
            await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await UntilAsync(() =>
            {
                metrics.Listener.RecordObservableInstruments();
                return Task.FromResult(metrics.Pending.Any(sample => sample.Value == 1));
            });
            disposing = Task.Run(fixture.Service.Dispose);
            await entered.Task.WaitAsync(Deadline);
            await canceled.Task.WaitAsync(Deadline);
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            await Assert.That(disposing.IsCompleted).IsFalse();
            metrics.Lag.Clear();
            metrics.Pending.Clear();
            metrics.Listener.RecordObservableInstruments();
            await Assert.That(metrics.Duration.Count + metrics.Lag.Count + metrics.Pending.Count + metrics.DeadLetters.Count)
                .IsEqualTo(0);
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            await Assert.That(fixture.Service.ExecuteTask.IsCompletedSuccessfully).IsTrue();
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            if (disposing is not null) await disposing.WaitAsync(Deadline);
        }
    }

    [Test, NotInParallel]
    public async Task TelemetryThrowingHandlerCancellationStillTearsDownResources()
    {
        var completed = 0;
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName
                    && instrument.Name == "respire.stream.worker.processing.duration")
                    listener.EnableMeasurementEvents(instrument);
            },
            MeasurementsCompleted = (_, _) => Interlocked.Increment(ref completed),
        };
        meter.Start();
        using var tracing = ListenToWorker(_ => { });
        var entered = NewSignal();
        var release = NewSignal();
        var failure = new InvalidOperationException("callback failure");
        ActivitySource? source = null;
        var state = new State { Handle = async (_, token) =>
        {
            using var callback = token.Register(() => throw failure);
            source = Activity.Current!.Source;
            entered.TrySetResult();
            await release.Task;
            return RespireStreamWorkerResult.Ack;
        } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new() { TelemetryName = WorkerName });
        try
        {
            await fixture.AddAsync(0);
            await fixture.StartAsync();
            await entered.Task.WaitAsync(Deadline);
            var actual = await Assert.That(fixture.Service.Dispose).ThrowsExactly<AggregateException>();
            await Assert.That(actual!.InnerExceptions.Single()).IsSameReferenceAs(failure);
            await Assert.That(completed).IsEqualTo(1);
            await Assert.That(source!.HasListeners()).IsFalse();
            release.TrySetResult();
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            await Assert.That(fixture.Service.ExecuteTask.IsCompletedSuccessfully).IsTrue();
            await Assert.That(state.DisposedScopes).IsEqualTo(1);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
            fixture.Service.Dispose();
            await Assert.That(completed).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    public async Task TelemetryThrowingListenersCannotReplaceAcknowledgementFailure()
    {
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        meter.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("listener failure"));
        meter.Start();
        using var tracing = ListenToWorker(_ => throw new InvalidOperationException("listener failure"));
        await using var fixture = await Fixture.CreateAsync(options: new() { TelemetryName = WorkerName });
        using var fault = fixture.Server.InjectFault("EVALSHA", RespireFakeFault.Loading());
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline)).Throws<RespireServerException>();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        await Assert.That(fixture.State.DisposedScopes).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task TelemetryDisposalSuppressesMeasurementsFromUncooperativeHandler()
    {
        using var metrics = new WorkerMetrics();
        var release = NewSignal();
        var state = new State { Handle = async (_, _) => { await release.Task; return RespireStreamWorkerResult.Ack; } };
        await using var fixture = await Fixture.CreateAsync(state: state, options: new() { TelemetryName = WorkerName });
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        try
        {
            fixture.Service.Dispose();
            release.TrySetResult();
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            metrics.Listener.RecordObservableInstruments();
            await Assert.That(metrics.Duration.Count + metrics.Lag.Count + metrics.Pending.Count + metrics.DeadLetters.Count).IsEqualTo(0);
            await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    public async Task TelemetryQueryTimeoutClearsSamplesAndDoesNotStopWorker()
    {
        using var metrics = new WorkerMetrics();
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            TelemetryName = WorkerName, MetricsPollInterval = TimeSpan.FromMilliseconds(20),
            MetricsPollTimeout = TimeSpan.FromMilliseconds(20),
        });
        await fixture.StartAsync();
        await UntilAsync(() =>
        {
            metrics.Listener.RecordObservableInstruments();
            return Task.FromResult(metrics.Pending.Count > 0);
        });
        var gate = new RespireFakeGate();
        using var fault = fixture.Server.InjectFault("XINFO", RespireFakeFault.Pause(gate, afterExecution: true));
        try
        {
            await fault.Matched.WaitAsync(Deadline);
            await UntilAsync(() =>
            {
                metrics.Pending.Clear();
                metrics.Listener.RecordObservableInstruments();
                return Task.FromResult(metrics.Pending.IsEmpty);
            });
            await Assert.That(fixture.Service.ExecuteTask!.IsCompleted).IsFalse();
            await Assert.That(fault.ExecutionCount).IsEqualTo(1);
            gate.Release();
            await fixture.AddAsync(0);
            await UntilAsync(() => Task.FromResult(metrics.Duration.Count == 1));
            await fixture.StopAsync();
            await Assert.That(fixture.Service.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        }
        finally { gate.Release(); }
    }

    private static ActivityListener ListenToWorker(Action<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireStreamWorkerTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed record WorkerSample(double Value, Dictionary<string, object?> Tags);

    private sealed class WorkerMetrics : IDisposable
    {
        internal MeterListener Listener { get; } = new();
        internal ConcurrentQueue<WorkerSample> Duration { get; } = new();
        internal ConcurrentQueue<WorkerSample> DeadLetters { get; } = new();
        internal ConcurrentQueue<WorkerSample> Lag { get; } = new();
        internal ConcurrentQueue<WorkerSample> Pending { get; } = new();

        internal WorkerMetrics()
        {
            Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RespireStreamWorkerTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            Listener.SetMeasurementEventCallback<double>((_, value, tags, _) => Capture(Duration, value, tags));
            Listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument.Name switch
            {
                "respire.stream.worker.dead_letters" => DeadLetters,
                "respire.stream.worker.group.lag" => Lag,
                _ => Pending,
            }, value, tags));
            Listener.Start();
        }

        private static void Capture(ConcurrentQueue<WorkerSample> target, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
            if (values.GetValueOrDefault("respire.worker.name") as string == WorkerName) target.Enqueue(new(value, values));
        }

        public void Dispose() => Listener.Dispose();
    }
}
