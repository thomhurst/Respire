using Microsoft.Extensions.Logging;

namespace Respire.Internal;

/// <summary>What one background refresh did.</summary>
internal enum TopologyRefreshOutcome
{
    /// <summary>The cluster was queried and returned a topology (a partial map keeps the owners of uncovered slots).</summary>
    Refreshed,
    /// <summary>No query was sent: a refresh that succeeded moments ago answered the request.</summary>
    ReusedRecent,
    /// <summary>No candidate returned a usable topology.</summary>
    Failed,
}

/// <summary>Decides when the cluster router runs a background topology refresh.</summary>
/// <remarks>
/// <para>All scheduling state lives under one lock: the earliest due time of any pending plain
/// request, the earliest due time of any pending forced request, the next periodic deadline and the
/// failure-retry deadline. Triggers merge into that state (the earliest deadline of each kind wins)
/// and then complete the current wake task. The worker re-reads the state after every wake, so a
/// spurious or late wake can never carry stale delay or force data into a later refresh.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><c>MOVED</c> requests are debounced by <see cref="MovedDebounce"/>. A later redirect keeps the
/// first deadline, so a steady stream of redirects cannot postpone discovery.</item>
/// <item>Primary-disconnect requests are forced but spaced by <see cref="PrimaryDisconnectSpacing"/>,
/// because a primary that keeps failing to reconnect reports a disconnect for every slot on every
/// attempt. Forced and plain deadlines are kept apart, so an earlier plain or periodic deadline never
/// turns into a forced refresh that skips the spacing.</item>
/// <item>A failed refresh schedules a retry with capped exponential backoff. Plain requests, the
/// periodic deadline and primary-disconnect requests wait for that retry, so a cluster that is down
/// is not probed back to back. A primary that stays down reports a disconnect on every reconnect
/// attempt, so without this its forced requests would keep refreshing one spacing apart. Only an
/// explicit forced request still runs at its own deadline.</item>
/// <item>A refresh satisfies every plain request pending when it starts. A forced request is
/// satisfied by a refresh that started at or after its deadline, or by any refresh that started
/// after the request arrived and actually queried the cluster.</item>
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
    internal const double PeriodicJitterRatio = 0.1;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitterSample;
    private readonly long _origin;
    private readonly TimeSpan? _interval;
    private TaskCompletionSource _wake = NewWake();
    private TimeSpan? _pendingDue;
    private TimeSpan? _forcedDue;
    private bool _forcedDisconnect;
    // True when an explicit forced request (not a primary disconnect) is pending; only those
    // bypass the failure backoff.
    private bool _forcedExplicit;
    // Sequence number of the latest forced request; a run records the value it started with.
    private long _forcedSequence;
    private TimeSpan? _periodicDue;
    private TimeSpan? _retryDue;
    private TimeSpan? _lastDisconnectRefresh;
    private int _consecutiveFailures;
    private volatile bool _forcedRequestPending;

    /// <param name="interval">The periodic interval; see <see cref="IsPeriodic"/>.</param>
    /// <param name="clock">Drives every deadline.</param>
    /// <param name="jitterSample">Returns a value in [0, 1) that scales the periodic jitter. Tests
    /// pass a constant to assert exact deadlines.</param>
    internal ClusterTopologyRefreshScheduler(TimeSpan? interval, TimeProvider clock, Func<double>? jitterSample = null)
    {
        _clock = clock;
        _jitterSample = jitterSample ?? Random.Shared.NextDouble;
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

    /// <summary>Adds without overflowing: a deadline past <see cref="TimeSpan.MaxValue"/> means never.</summary>
    internal static TimeSpan SaturatingAdd(TimeSpan time, TimeSpan delay)
        => delay <= TimeSpan.Zero ? time
            : time > TimeSpan.MaxValue - delay ? TimeSpan.MaxValue
            : time + delay;

    /// <summary>Requests a refresh after <paramref name="delay"/>; an explicit forced request bypasses
    /// the recent-success coalescing window and the failure backoff.</summary>
    internal void Request(TimeSpan delay, bool force = false)
    {
        lock (_gate)
        {
            var due = SaturatingAdd(Now, delay);
            if (force)
            {
                MergeForcedLocked(due, disconnect: false);
                return;
            }

            if (_retryDue is { } retry && retry > due) due = retry;
            if (_pendingDue is not { } pending || due < pending) _pendingDue = due;
            _wake.TrySetResult();
        }
    }

    /// <summary>Requests a forced refresh after a primary disconnect, spaced from the last one and
    /// never earlier than a pending failure retry.</summary>
    internal void RequestPrimaryDisconnect()
    {
        lock (_gate)
        {
            var due = Now;
            if (_lastDisconnectRefresh is { } last && SaturatingAdd(last, PrimaryDisconnectSpacing) > due)
                due = SaturatingAdd(last, PrimaryDisconnectSpacing);
            if (_retryDue is { } retry && retry > due) due = retry;
            MergeForcedLocked(due, disconnect: true);
        }
    }

    private void MergeForcedLocked(TimeSpan due, bool disconnect)
    {
        if (_forcedDue is not { } pending || due < pending) _forcedDue = due;
        _forcedDisconnect |= disconnect;
        _forcedExplicit |= !disconnect;
        _forcedSequence++;
        _forcedRequestPending = true;
        _wake.TrySetResult();
    }

    /// <summary>The worker's next step.</summary>
    /// <param name="Run">True when a refresh should run now.</param>
    /// <param name="AllowRecentResult">True when only plain debounced requests are due, so a refresh
    /// that succeeded moments ago may answer them.</param>
    /// <param name="Wait">How long to wait before asking again; null waits for a wake only.</param>
    /// <param name="Wake">Completes when a new request arrives.</param>
    /// <param name="StartedAt">When the run started, on the scheduler's clock.</param>
    /// <param name="ForcedSequence">The forced-request sequence seen when the run started; pass the
    /// decision back to <see cref="Complete"/>.</param>
    internal readonly record struct Decision(
        bool Run, bool AllowRecentResult, TimeSpan? Wait, Task Wake, TimeSpan StartedAt = default, long ForcedSequence = 0);

    /// <summary>Takes every pending request that is due, or returns how long to wait.</summary>
    internal Decision Next()
    {
        lock (_gate)
        {
            var now = Now;
            var due = Earliest(Earliest(_pendingDue, _forcedDue), Earliest(_periodicDue, _retryDue));
            if (due is { } runAt && runAt <= now)
            {
                var forcedDue = _forcedDue <= now;
                var periodicDue = _periodicDue <= now;
                var retryDue = _retryDue <= now;
                // A forced, periodic or retry refresh must query the cluster; only a plain debounced
                // request may reuse a refresh that succeeded moments ago.
                var allowRecent = !forcedDue && !periodicDue && !retryDue;
                if (forcedDue) ClearForcedLocked(now);
                _pendingDue = null;
                if (periodicDue) _periodicDue = null;
                _retryDue = null;
                return new Decision(true, allowRecent, null, Task.CompletedTask, now, _forcedSequence);
            }

            if (_wake.Task.IsCompleted) _wake = NewWake();
            TimeSpan? wait = due is { } next
                ? (next - now < MaximumTimerSegment ? next - now : MaximumTimerSegment)
                : null;
            return new Decision(false, false, wait, _wake.Task);
        }
    }

    private void ClearForcedLocked(TimeSpan refreshStartedAt)
    {
        if (_forcedDisconnect) _lastDisconnectRefresh = refreshStartedAt;
        _forcedDue = null;
        _forcedDisconnect = false;
        _forcedExplicit = false;
        _forcedRequestPending = false;
    }

    /// <summary>Arms the first periodic deadline.</summary>
    internal void Start()
    {
        lock (_gate) _periodicDue = NextPeriodicDue(Now);
    }

    /// <summary>Records the outcome of the refresh <paramref name="run"/> started, and returns the
    /// failure-retry delay, if any.</summary>
    internal TimeSpan? Complete(Decision run, TopologyRefreshOutcome outcome)
    {
        lock (_gate)
        {
            var now = Now;
            switch (outcome)
            {
                case TopologyRefreshOutcome.Refreshed:
                    // The cluster was queried after every forced request up to this sequence arrived,
                    // so the fresh map already answers them.
                    if (_forcedDue is not null && _forcedSequence <= run.ForcedSequence)
                        ClearForcedLocked(run.StartedAt);
                    _consecutiveFailures = 0;
                    _retryDue = null;
                    _periodicDue = NextPeriodicDue(now);
                    return null;

                case TopologyRefreshOutcome.ReusedRecent:
                    // Only a plain request ran. The refresh it reused already armed the periodic
                    // deadline, and forced requests still need a real query.
                    return null;

                default:
                    if (_consecutiveFailures < int.MaxValue) _consecutiveFailures++;
                    var shift = Math.Min(_consecutiveFailures - 1, 16);
                    var retryDelay = TimeSpan.FromTicks(Math.Min(
                        InitialFailureRetryDelay.Ticks << shift, MaximumFailureRetryDelay.Ticks));
                    var retryDue = SaturatingAdd(now, retryDelay);
                    _retryDue = retryDue;
                    // The retry is the next non-forced refresh. Plain requests queued while the failed
                    // refresh ran, and a periodic deadline shorter than the backoff, wait for it
                    // instead of probing an unavailable cluster again straight away. The periodic
                    // timer is re-armed when the retry completes.
                    if (_pendingDue is { } pending && pending < retryDue) _pendingDue = retryDue;
                    // A disconnect queued while the failed refresh ran waits for the retry too.
                    if (!_forcedExplicit && _forcedDue is { } forced && forced < retryDue) _forcedDue = retryDue;
                    _periodicDue = null;
                    return retryDelay;
            }
        }
    }

    private TimeSpan? NextPeriodicDue(TimeSpan now)
    {
        if (_interval is not { } interval) return null;
        // Jitter only shortens the interval, so a configured interval remains an upper bound.
        var jitter = TimeSpan.FromTicks((long)(interval.Ticks * PeriodicJitterRatio * _jitterSample()));
        return SaturatingAdd(now, interval - jitter);
    }

    private static TimeSpan? Earliest(TimeSpan? left, TimeSpan? right)
        => left is not { } l ? right : right is not { } r ? l : (l < r ? l : r);

    /// <summary>Runs refreshes until <paramref name="stop"/> is cancelled. Unexpected failures are
    /// logged and retried; they never end the loop.</summary>
    internal async Task RunAsync(
        Func<bool, CancellationToken, Task<TopologyRefreshOutcome>> refresh, ILogger? logger, CancellationToken stop)
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

                TopologyRefreshOutcome outcome;
                try { outcome = await refresh(decision.AllowRecentResult, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.TryLog(LogLevel.Debug, error, "Redis Cluster topology refresh failed");
                    outcome = TopologyRefreshOutcome.Failed;
                }

                if (Complete(decision, outcome) is { } retryDelay)
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
