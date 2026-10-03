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
    /// <summary>No usable latency estimate; the connection can still be eligible for reads.</summary>
    internal const long Unknown = long.MaxValue;
    // Probe cadence is independent of the selection/measurement wait budget.
    internal const long IntervalMilliseconds = 1_000;
    internal const long MaximumAgeMilliseconds = 10_000;
    internal const int MaximumConcurrentProbes = 4;
    private readonly ConditionalWeakTable<TConnection, Sample> _samples = new();
    private readonly ConditionalWeakTable<object, StrongBox<long>> _connectionFailures = new();
    private readonly object _gate = new();
    private readonly List<Task> _running = [];
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    private long _started;

    internal long SamplesStarted => Volatile.Read(ref _started);
    private long Now => clock?.Invoke() ?? Environment.TickCount64;

    internal bool HasPendingProbe(TConnection connection)
        => _samples.TryGetValue(connection, out var sample) && Volatile.Read(ref sample.Pending) is not null;

    internal bool TryReserveForValidation(TConnection connection)
    {
        var sample = _samples.GetValue(connection, static _ => new Sample());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (sample.Pending is not null || sample.Reserved) return false;
            Volatile.Write(ref sample.Reserved, true);
            return true;
        }
    }

    internal void ReleaseValidationReservation(TConnection connection)
    {
        lock (_gate)
        {
            if (_samples.TryGetValue(connection, out var sample))
                Volatile.Write(ref sample.Reserved, false);
        }
    }

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
        // ROLE owns this socket until validation completes. Exclude it without starting
        // a probe or publishing a cached estimate to a concurrent selection.
        if (Volatile.Read(ref sample.Reserved)) return ValueTask.FromResult(ReadLatencySampler.Pending);
        var now = Now;
        var pending = Volatile.Read(ref sample.Pending);
        Probe? start = null;
        if (now >= Volatile.Read(ref sample.NextAttempt))
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                // Pair this check with TryReserveForValidation under the same gate: a
                // PING cannot enter the FIFO between the pending check and ROLE.
                if (sample.Reserved) return ValueTask.FromResult(ReadLatencySampler.Pending);
                pending = sample.Pending;
                // No queue of health checks: callers without a sample can still select a
                // healthy connection without latency evidence when all four slots are busy.
                if (pending is null && now >= sample.NextAttempt && _running.Count < MaximumConcurrentProbes)
                {
                    Volatile.Write(ref sample.NextAttempt, now + IntervalMilliseconds);
                    start = new();
                    pending = sample.Pending = start.Result.Task;
                    _running.Add(start.Finished.Task);
                    Interlocked.Increment(ref _started);
                }
            }
        }
        if (start is not null) _ = MeasureAsync(connection, sample, start);
        // A fresh estimate cannot bypass an outstanding command in this connection's FIFO.
        if (pending is not null) return new ValueTask<long>(pending.WaitAsync(cancellationToken));
        var measurement = Volatile.Read(ref sample.Measurement);
        if (measurement is not null && now - measurement.MeasuredAt < MaximumAgeMilliseconds)
            return ValueTask.FromResult(measurement.Latency);
        return ValueTask.FromResult(Unknown);
    }

    private async Task MeasureAsync(TConnection connection, Sample sample, Probe probe)
    {
        var latency = Unknown;
        Task<long>? operation = null;
        try
        {
            // The deadline bounds selection's wait, not the lifetime of an accepted PING.
            // Canceling SendAsync would detach its waiter while its reply still owns a FIFO
            // position. Retain the probe slot until that command completes, so a stalled peer
            // cannot accumulate one abandoned PING per second when CommandTimeout is disabled.
            operation = measure(connection, _stop.Token).AsTask();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(ReadLatencySampler.SamplingWaitMilliseconds);
            var elapsed = await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
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
        // An unanswered probe still occupies this connection's FIFO. Distinguish it
        // from an unsampled or ACL-denied candidate that can safely accept a read.
        if (!_stop.IsCancellationRequested && operation is { IsCompleted: false }) latency = ReadLatencySampler.Pending;
        lock (_gate)
        {
            // Failure deliberately invalidates even a young estimate: do not retain a known-fast
            // ranking after contrary probe evidence. Recovery starts a new, unsmoothed estimate.
            Volatile.Write(ref sample.Measurement, latency < 0 || latency == Unknown ? null : new Measurement(latency, Now));
            probe.Result.TrySetResult(latency);
        }
        // A late reply is observed but not used as a latency estimate. Disposal cancels the
        // underlying wait and then closes the client's connections through its normal lifecycle.
        if (operation is not null)
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { }
        }
        lock (_gate)
        {
            sample.Pending = null;
            _running.Remove(probe.Finished.Task);
            probe.Finished.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
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
        internal bool Reserved;
    }

    private sealed record Measurement(long Latency, long MeasuredAt);

    private sealed class Probe
    {
        internal readonly TaskCompletionSource<long> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class ReadLatencySampler
{
    /// <summary>A probe or ROLE validation occupies the connection; exclude it from selection, unlike Unknown.</summary>
    internal const long Pending = -1;
    internal const int SamplingWaitMilliseconds = 1_000;
    private static readonly RawCommand s_ping = new(RespCommands.Ping);

    internal static ReadLatencySampler<RespireConnection> Create(Func<long>? clock = null) => new(MeasureAsync, clock);

    private static async ValueTask<long> MeasureAsync(RespireConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        // Keep observing the physical reply after selection's budget expires. Per-command
        // deadlines detach waiters, so this advisory command uses the sampler's wait budget
        // instead; the connection's receive watchdog remains active.
        using var reply = await connection.SendAsync(s_ping, cancellationToken,
            armCommandDeadline: false, pinToConnection: true).ConfigureAwait(false);
        return reply.AsSpan().SequenceEqual("PONG"u8)
            ? Stopwatch.GetElapsedTime(started).Ticks
            : ReadLatencySampler<RespireConnection>.Unknown;
    }
}
