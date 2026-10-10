using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Respire.Streaming;

/// <summary>OpenTelemetry subscription names for hosted stream workers.</summary>
public static class RespireStreamWorkerTelemetry
{
    /// <summary>Subscribe with metrics.AddMeter(MeterName).</summary>
    public const string MeterName = "Respire.Streaming";

    /// <summary>Subscribe with tracing.AddSource(ActivitySourceName).</summary>
    public const string ActivitySourceName = "Respire.Streaming";
}

internal sealed class StreamWorkerTelemetry : IDisposable
{
    private readonly ActivitySource? _source;
    private readonly object _gate = new();
    private readonly Meter _meter = new(RespireStreamWorkerTelemetry.MeterName);
    private readonly KeyValuePair<string, object?> _workerTag;
    private readonly Histogram<double>? _duration;
    private readonly Counter<long>? _deadLetters;
    private readonly ObservableGauge<long>? _lagGauge;
    private readonly ObservableGauge<long>? _pendingGauge;
    private long? _lag;
    private long? _pending;
    private volatile bool _disposed;
    private volatile bool _pollingStopped;

    internal StreamWorkerTelemetry(string name)
    {
        _workerTag = new("respire.worker.name", name);
        try { _source = new ActivitySource(RespireStreamWorkerTelemetry.ActivitySourceName); }
        catch { /* A throwing ShouldListenTo callback cannot prevent startup. */ }
        try
        {
            _duration = _meter.CreateHistogram<double>("respire.stream.worker.processing.duration", "s",
                "Delivery attempt duration, including deserialization and fenced completion.");
            _deadLetters = _meter.CreateCounter<long>("respire.stream.worker.dead_letters", "{message}",
                "Confirmed dead-letter appends; uncertain replies and deleted bodies are excluded.");
            _lagGauge = _meter.CreateObservableGauge("respire.stream.worker.group.lag", () => Observe(lag: true),
                "{message}", "Latest known group lag; absent when Redis cannot determine it.");
            _pendingGauge = _meter.CreateObservableGauge("respire.stream.worker.group.pending", () => Observe(lag: false),
                "{message}", "Latest polled pending count for this registration's group.");
        }
        catch { /* Instrument publication listeners cannot prevent worker startup. */ }
    }

    internal bool NeedsGroupPoll => !_disposed && !_pollingStopped
        && (_lagGauge?.Enabled == true || _pendingGauge?.Enabled == true);

    private Measurement<long>[] Observe(bool lag)
    {
        lock (_gate)
        {
            var value = lag ? _lag : _pending;
            return !_disposed && value.HasValue ? [new(value.Value, _workerTag)] : [];
        }
    }

    internal void SetGroup(RespireStreamGroupInfo? info)
    {
        lock (_gate)
        {
            if (_disposed || _pollingStopped) return;
            _lag = info?.Lag;
            _pending = info?.Pending;
        }
    }

    internal void StopPolling()
    {
        lock (_gate)
        {
            _pollingStopped = true;
            _lag = _pending = null;
        }
    }

    internal void DeadLetter(string reason)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _deadLetters?.Add(1, _workerTag, new("respire.worker.reason", reason)); }
            catch { /* Listeners cannot change completion outcomes. */ }
        }
    }

    internal Attempt Begin(RespireStreamEntry entry, RespireStreamWorkerOptions options)
    {
        var previous = Activity.Current;
        Activity? activity = null;
        try
        {
            if (!_disposed && _source?.HasListeners() == true)
            {
                // A delivery has a remote producer parent or is a new root, never the host's ambient activity.
                Activity.Current = null;
                activity = _source.StartActivity("stream process", ActivityKind.Consumer,
                    StreamTraceContext.Extract(entry, options), tags: [_workerTag]);
                if (activity is null) RestoreCurrent(previous);
            }
        }
        catch
        {
            // ActivityStarted can throw after Start has installed Activity.Current.
            // Never dispose the host's activity if a CurrentChanged callback fails early.
            if (!ReferenceEquals(Activity.Current, previous))
                try { Activity.Current?.Dispose(); } catch { }
            RestoreCurrent(previous);
        }
        return new Attempt(this, activity, previous);
    }

    private static void RestoreCurrent(Activity? previous)
    {
        try { Activity.Current = previous; }
        catch { /* CurrentChanged callbacks must not replace worker exceptions. */ }
    }

    internal sealed class Attempt(StreamWorkerTelemetry telemetry, Activity? activity, Activity? previous) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        internal string Outcome { get; set; } = "error";

        public void Dispose()
        {
            lock (telemetry._gate)
            {
                if (!telemetry._disposed)
                {
                    try { telemetry._duration?.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds,
                        telemetry._workerTag, new("respire.worker.outcome", Outcome)); }
                    catch { /* Listeners cannot replace handler or transport exceptions. */ }
                }
            }
            try
            {
                activity?.SetTag("respire.worker.outcome", Outcome);
                if (Outcome == "error") activity?.SetStatus(ActivityStatusCode.Error);
                activity?.Dispose();
            }
            catch { /* ActivityStopped listeners cannot change worker behavior. */ }
            finally { RestoreCurrent(previous); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _lag = _pending = null;
            try { _meter.Dispose(); } catch { /* Ignore listener failures during teardown. */ }
            try { _source?.Dispose(); } catch { /* Ignore tracing failures during teardown. */ }
        }
    }
}
