using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>Demand-driven latency samples, weakly keyed by physical connection.</summary>
internal sealed class ReadLatencySampler<TConnection>(
    Func<TConnection, CancellationToken, ValueTask<long>> measure,
    Func<long>? clock = null) : IAsyncDisposable where TConnection : class
{
    internal const long Unknown = long.MaxValue;
    internal const long IntervalMilliseconds = 1_000;
    internal const long MaximumAgeMilliseconds = 10_000;
    internal const int MaximumConcurrentProbes = 4;
    private readonly ConditionalWeakTable<TConnection, Sample> _samples = new();
    private readonly ConditionalWeakTable<object, StrongBox<long>> _connectionFailures = new();
    private readonly object _gate = new();
    private readonly List<Task<long>> _running = [];
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private long _started;

    internal long SamplesStarted => Volatile.Read(ref _started);
    private long Now => clock?.Invoke() ?? Environment.TickCount64;

    internal bool CanConnect(object candidate)
        => !_connectionFailures.TryGetValue(candidate, out var retry) || Now >= Volatile.Read(ref retry.Value);

    internal void ConnectionFailed(object candidate)
        => Volatile.Write(ref _connectionFailures.GetValue(candidate, static _ => new()).Value, Now + IntervalMilliseconds);

    internal void ConnectionSucceeded(object candidate)
    {
        if (_connectionFailures.TryGetValue(candidate, out _)) _connectionFailures.Remove(candidate);
    }

    internal ValueTask<long> GetLatencyAsync(TConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var sample = _samples.GetValue(connection, static _ => new Sample());
        var now = Now;
        var pending = Volatile.Read(ref sample.Pending);
        TaskCompletionSource<long>? start = null;
        if (now >= Volatile.Read(ref sample.NextAttempt))
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                pending = sample.Pending;
                // No queue of health checks: callers without a sample can still select a
                // healthy connection without latency evidence when all four slots are busy.
                if (pending is null && now >= sample.NextAttempt && _running.Count < MaximumConcurrentProbes)
                {
                    Volatile.Write(ref sample.NextAttempt, now + IntervalMilliseconds);
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = sample.Pending = start.Task;
                    _running.Add(pending);
                    Interlocked.Increment(ref _started);
                }
            }
        }
        if (start is not null) _ = MeasureAsync(connection, sample, start);
        var measurement = Volatile.Read(ref sample.Measurement);
        if (measurement is not null && now - measurement.MeasuredAt < MaximumAgeMilliseconds)
            return ValueTask.FromResult(measurement.Latency);
        // A usable old sample keeps reads off the sampling path. Only cold/expired samples wait,
        // and canceling a caller detaches that caller without canceling the shared measurement.
        return pending is not null
            ? new ValueTask<long>(pending.WaitAsync(cancellationToken))
            : ValueTask.FromResult(Unknown);
    }

    private async Task MeasureAsync(TConnection connection, Sample sample, TaskCompletionSource<long> completion)
    {
        var latency = Unknown;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            var elapsed = await measure(connection, deadline.Token).ConfigureAwait(false);
            if (elapsed >= 0 && elapsed != Unknown)
            {
                var previous = Volatile.Read(ref sample.Measurement);
                latency = previous is null ? elapsed : previous.Latency + (elapsed - previous.Latency) / 4;
            }
        }
        catch
        {
            // Sampling is advisory. ACL denial, timeout, or transport failure removes latency
            // evidence; the router still decides connection and role eligibility independently.
        }
        lock (_gate)
        {
            Volatile.Write(ref sample.Measurement, latency == Unknown ? null : new Measurement(latency, Now));
            sample.Pending = null;
            _running.Remove(completion.Task);
            completion.TrySetResult(latency);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<long>[] running;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            running = _running.ToArray();
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        _samples.Clear();
        _connectionFailures.Clear();
        _stop.Dispose();
    }

    private sealed class Sample
    {
        internal Measurement? Measurement;
        internal long NextAttempt;
        internal Task<long>? Pending;
    }

    private sealed record Measurement(long Latency, long MeasuredAt);
}

internal static class ReadLatencySampler
{
    private static readonly RawCommand s_ping = new(RespCommands.Ping);

    internal static ReadLatencySampler<RespireConnection> Create() => new(MeasureAsync);

    private static async ValueTask<long> MeasureAsync(RespireConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var reply = await connection.SendAsync(s_ping, cancellationToken).ConfigureAwait(false);
        return reply.AsSpan().SequenceEqual("PONG"u8)
            ? Stopwatch.GetElapsedTime(started).Ticks
            : ReadLatencySampler<RespireConnection>.Unknown;
    }
}
