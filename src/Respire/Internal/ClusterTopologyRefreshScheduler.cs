using Microsoft.Extensions.Logging;

namespace Respire.Internal;

/// <summary>Decides when the cluster router runs a background topology refresh.</summary>
/// <remarks>
/// <para>All scheduling state lives under one lock: the earliest due time of any pending request,
/// whether that request is forced, the next periodic deadline and the failure-retry deadline.
/// Triggers merge into that state (the earliest deadline wins and force is OR-ed) and then complete
/// the current wake task. The worker re-reads the state after every wake, so a spurious or late wake
/// can never carry stale delay or force data into a later refresh.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><c>MOVED</c> requests are debounced by <see cref="MovedDebounce"/>. A later redirect keeps the
/// first deadline, so a steady stream of redirects cannot postpone discovery.</item>
/// <item>Primary-disconnect requests are forced but spaced by <see cref="PrimaryDisconnectSpacing"/>,
/// because a primary that keeps failing to reconnect reports a disconnect for every slot on every
/// attempt.</item>
/// <item>A failed refresh schedules a retry with capped exponential backoff, and delays later
/// non-forced requests until that retry, so a cluster that is down is not probed back to back.</item>
/// <item>Any refresh that runs satisfies every request pending at that moment, including a
/// debounced <c>MOVED</c> request that was not yet due.</item>
/// </list>
/// </remarks>
internal sealed class ClusterTopologyRefreshScheduler
{
    internal static readonly TimeSpan MovedDebounce = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PrimaryDisconnectSpacing = TimeSpan.FromSeconds(1);
    // The first retry waits as long as the MOVED debounce: an immediate retry would pile another
    // CLUSTER SLOTS onto nodes that just stalled while foreground READONLY recovery may need them.
    internal static readonly TimeSpan InitialFailureRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumFailureRetryDelay = TimeSpan.FromSeconds(60);
    // Runtime timers cannot be armed for more than about 49.7 days. Longer waits re-arm.
    internal static readonly TimeSpan MaximumTimerSegment = TimeSpan.FromDays(24);
    // Many clients started together should not all send CLUSTER SLOTS in the same instant.
    private const double PeriodicJitterRatio = 0.1;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private readonly TimeSpan? _interval;
    private TaskCompletionSource _wake = NewWake();
    private TimeSpan? _pendingDue;
    private bool _pendingForce;
    private bool _pendingDisconnect;
    private TimeSpan? _periodicDue;
    private TimeSpan? _retryDue;
    private TimeSpan? _lastDisconnectRefresh;
    private int _consecutiveFailures;
    private volatile bool _forcedRequestPending;

    internal ClusterTopologyRefreshScheduler(TimeSpan? interval, TimeProvider clock)
    {
        _clock = clock;
        _origin = clock.GetTimestamp();
        _interval = IsPeriodic(interval) ? interval : null;
    }

    /// <summary>Null, zero and <see cref="Timeout.InfiniteTimeSpan"/> disable the periodic timer.</summary>
    internal static bool IsPeriodic(TimeSpan? interval) => interval is { } value && value > TimeSpan.Zero;

    /// <summary>Lock-free hint for hot callers: a forced refresh is already queued.</summary>
    internal bool HasPendingForcedRequest => _forcedRequestPending;

    internal int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    private TimeSpan Now => _clock.GetElapsedTime(_origin);

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Requests a refresh after <paramref name="delay"/>; a forced request bypasses the
    /// recent-success coalescing window and the failure backoff.</summary>
    internal void Request(TimeSpan delay, bool force = false)
    {
        lock (_gate)
        {
            var due = Now + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
            if (!force && _retryDue is { } retry && retry > due) due = retry;
            MergeLocked(due, force, disconnect: false);
        }
    }

    /// <summary>Requests a forced refresh after a primary disconnect, spaced from the last one.</summary>
    internal void RequestPrimaryDisconnect()
    {
        lock (_gate)
        {
            var due = Now;
            if (_lastDisconnectRefresh is { } last && last + PrimaryDisconnectSpacing > due)
                due = last + PrimaryDisconnectSpacing;
            MergeLocked(due, force: true, disconnect: true);
        }
    }

    private void MergeLocked(TimeSpan due, bool force, bool disconnect)
    {
        if (_pendingDue is not { } pending || due < pending) _pendingDue = due;
        _pendingForce |= force;
        _pendingDisconnect |= disconnect;
        if (force) _forcedRequestPending = true;
        _wake.TrySetResult();
    }

    internal readonly record struct Decision(bool Run, bool AllowRecentResult, TimeSpan? Wait, Task Wake);

    /// <summary>Takes every pending request when one is due, or returns how long to wait.</summary>
    internal Decision Next()
    {
        lock (_gate)
        {
            var now = Now;
            var due = Earliest(Earliest(_pendingDue, _periodicDue), _retryDue);
            if (due is { } runAt && runAt <= now)
            {
                // A periodic or retry refresh must query the cluster; only a plain debounced
                // request may reuse a refresh that succeeded moments ago.
                var allowRecent = !_pendingForce && !(_periodicDue <= now) && !(_retryDue <= now);
                if (_pendingDisconnect) _lastDisconnectRefresh = now;
                _pendingDue = null;
                _pendingForce = false;
                _pendingDisconnect = false;
                _forcedRequestPending = false;
                _periodicDue = null;
                _retryDue = null;
                return new Decision(true, allowRecent, null, Task.CompletedTask);
            }

            if (_wake.Task.IsCompleted) _wake = NewWake();
            TimeSpan? wait = due is { } next
                ? (next - now < MaximumTimerSegment ? next - now : MaximumTimerSegment)
                : null;
            return new Decision(false, false, wait, _wake.Task);
        }
    }

    /// <summary>Arms the first periodic deadline.</summary>
    internal void Start()
    {
        lock (_gate) _periodicDue = NextPeriodicDue(Now);
    }

    /// <summary>Records a refresh outcome and returns the failure-retry delay, if any.</summary>
    internal TimeSpan? Complete(bool success)
    {
        lock (_gate)
        {
            var now = Now;
            _periodicDue = NextPeriodicDue(now);
            if (success)
            {
                _consecutiveFailures = 0;
                _retryDue = null;
                return null;
            }

            if (_consecutiveFailures < int.MaxValue) _consecutiveFailures++;
            var shift = Math.Min(_consecutiveFailures - 1, 16);
            var retryDelay = TimeSpan.FromTicks(Math.Min(
                InitialFailureRetryDelay.Ticks << shift, MaximumFailureRetryDelay.Ticks));
            _retryDue = now + retryDelay;
            return retryDelay;
        }
    }

    private TimeSpan? NextPeriodicDue(TimeSpan now)
    {
        if (_interval is not { } interval) return null;
        // Jitter only shortens the interval, so a configured interval remains an upper bound.
        var jitter = TimeSpan.FromTicks((long)(interval.Ticks * PeriodicJitterRatio * Random.Shared.NextDouble()));
        return now + interval - jitter;
    }

    private static TimeSpan? Earliest(TimeSpan? left, TimeSpan? right)
        => left is not { } l ? right : right is not { } r ? l : (l < r ? l : r);

    /// <summary>Runs refreshes until <paramref name="stop"/> is cancelled. Unexpected failures are
    /// logged and retried; they never end the loop.</summary>
    internal async Task RunAsync(
        Func<bool, CancellationToken, Task<bool>> refresh, ILogger? logger, CancellationToken stop)
    {
        Start();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var decision = Next();
                if (!decision.Run)
                {
                    await WaitAsync(decision, stop).ConfigureAwait(false);
                    continue;
                }

                bool success;
                try { success = await refresh(decision.AllowRecentResult, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.TryLog(LogLevel.Debug, error, "Redis Cluster topology refresh failed");
                    success = false;
                }

                if (Complete(success) is { } retryDelay)
                {
                    logger.TryLog(LogLevel.Warning, null,
                        "Redis Cluster topology refresh failed {ConsecutiveFailures} consecutive time(s); retrying in {RetryDelay}",
                        ConsecutiveFailures, retryDelay);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // A faulted worker would silently stop all MOVED, disconnect and periodic refreshes.
                logger.TryLog(LogLevel.Warning, error, "Redis Cluster topology refresh worker failed; continuing");
                try { await Task.Delay(InitialFailureRetryDelay, _clock, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task WaitAsync(Decision decision, CancellationToken stop)
    {
        if (decision.Wait is not { } wait)
        {
            await decision.Wake.WaitAsync(stop).ConfigureAwait(false);
            return;
        }

        try { await decision.Wake.WaitAsync(wait, _clock, stop).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}

internal static class SafeLoggerExtensions
{
    /// <summary>Logs without letting a faulty user logger break background work.</summary>
    internal static void TryLog(this ILogger? logger, LogLevel level, Exception? error, string message,
        params object?[] args)
    {
        if (logger is null) return;
        try
        {
#pragma warning disable CA2254 // Callers pass constant templates.
            logger.Log(level, error, message, args);
#pragma warning restore CA2254
        }
        catch (Exception)
        {
            // A user logger must not terminate topology discovery.
        }
    }
}
