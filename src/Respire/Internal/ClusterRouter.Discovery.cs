using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly CancellationTokenSource _stopDiscovery = new();
    private readonly object _discoveryNotificationsGate = new();
    private Queue<RespireConnectionStateChange>? _discoveryNotifications;
    private bool _publishingDiscovery;
    // Process-wide within ClusterDiscovery so events from different clients cannot share an
    // episode ID when an observer aggregates them without retaining the client instance.
    private static long _nextDiscoveryEpisode;
    internal TimeProvider DiscoveryClock { get; set; } = TimeProvider.System;
    internal TimeProvider TopologyRefreshClock { get; set; } = TimeProvider.System;
    private static readonly TimeSpan TopologyRefreshCoalescingWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumTopologyRefreshDeadline = TimeSpan.FromSeconds(60);
    private const int MovedTopologyRefreshDelayMilliseconds = 5_000;
    private readonly SemaphoreSlim _topologyRefreshSignal = new(0, 1);
    private readonly object _topologyRefreshSignalGate = new();
    private readonly object _topologyRefreshWorkerGate = new();
    private Task? _topologyRefreshWorker;
    private int _topologyRefreshDelayMilliseconds;
    private readonly object _sharedRefreshGate = new();
    // READONLY recovery and topology refresh share one flight so overlapping triggers do not
    // launch independent discovery loops. Callers read the published slot map after success.
    private Task<bool>? _sharedRefreshTask;
    private ReadOnlyRefreshFlight? _readOnlyRefreshFlight;
    private long _lastTopologyRefreshTimestamp;
    private bool _hasTopologyRefreshTimestamp;
    private int _topologyRefreshStarted;
    private int _topologyRefreshForce;

    private sealed class ReadOnlyRefreshFlight(
        CancellationTokenSource cancellation, int slot, RespireEndpoint source)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal int Slot { get; } = slot;
        internal RespireEndpoint Source { get; } = source;
        internal Task<bool>? SharedTask;
        internal int Waiters;
        internal bool Completed;
        internal IDisposable? DiscoveryLease;
    }

    private Task<bool> RefreshReadOnlySharedAsync(
        RespireServerException rejection, RespireConnection source, int slot, CancellationToken waiterToken,
        DiscoveryRound? discovery, out bool joinedDifferentRecovery, out bool joinedTopologyRefresh)
    {
        TaskCompletionSource<bool>? start = null;
        Task<bool> task;
        ReadOnlyRefreshFlight? flight;
        lock (_sharedRefreshGate)
        {
            if (_sharedRefreshTask is null)
            {
                var discoveryLease = discovery?.Hold();
                var newFlight = new ReadOnlyRefreshFlight(
                    CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token), slot,
                    new RespireEndpoint(source.Host, source.Port))
                {
                    DiscoveryLease = discoveryLease,
                };
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                newFlight.SharedTask = start.Task;
                _sharedRefreshTask = start.Task;
                _readOnlyRefreshFlight = newFlight;
            }
            task = _sharedRefreshTask;
            flight = _readOnlyRefreshFlight;
            joinedDifferentRecovery = flight is not null && !ReferenceEquals(start?.Task, task)
                && (flight.Slot != slot || flight.Source.Port != source.Port
                    || !string.Equals(flight.Source.Host, source.Host, StringComparison.OrdinalIgnoreCase));
            joinedTopologyRefresh = start is null && flight is null;
            if (flight is not null) flight.Waiters++;
        }
        if (start is not null)
            _ = CompleteSharedRefreshAsync(start, async () =>
                await RunReadOnlyRefreshAsync(rejection, source, slot, flight!.Cancellation.Token, discovery).ConfigureAwait(false), flight);
        return AwaitReadOnlyRefreshAsync(task, waiterToken, flight);
    }

    private async Task<bool> AwaitReadOnlyRefreshAsync(
        Task<bool> task, CancellationToken waiterToken, ReadOnlyRefreshFlight? flight)
    {
        try { return await task.WaitAsync(waiterToken).ConfigureAwait(false); }
        finally { ReleaseReadOnlyWaiter(flight); }
    }

    private void ReleaseReadOnlyWaiter(ReadOnlyRefreshFlight? flight)
    {
        if (flight is null) return;
        CancellationTokenSource? cancel = null;
        lock (_sharedRefreshGate)
        {
            if (flight.Waiters > 0) flight.Waiters--;
            if (ReferenceEquals(_readOnlyRefreshFlight, flight) && !flight.Completed && flight.Waiters == 0)
            {
                _readOnlyRefreshFlight = null;
                if (ReferenceEquals(_sharedRefreshTask, flight.SharedTask)) _sharedRefreshTask = null;
                cancel = flight.Cancellation;
            }
        }
        try { cancel?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task CompleteSharedRefreshAsync(TaskCompletionSource<bool> completion, Func<Task<bool>> work,
        ReadOnlyRefreshFlight? readOnlyFlight = null)
    {
        try
        {
            var result = await work().ConfigureAwait(false);
            lock (_sharedRefreshGate)
            {
                if (readOnlyFlight is null && result)
                {
                    _lastTopologyRefreshTimestamp = TopologyRefreshClock.GetTimestamp();
                    _hasTopologyRefreshTimestamp = true;
                }
                if (readOnlyFlight is not null) readOnlyFlight.Completed = true;
            }
            completion.TrySetResult(result);
        }
        catch (Exception error)
        {
            lock (_sharedRefreshGate)
                if (readOnlyFlight is not null) readOnlyFlight.Completed = true;
            completion.TrySetException(error);
        }
        finally
        {
            lock (_sharedRefreshGate)
            {
                if (ReferenceEquals(_sharedRefreshTask, completion.Task)) _sharedRefreshTask = null;
                if (readOnlyFlight is not null && ReferenceEquals(_readOnlyRefreshFlight, readOnlyFlight))
                {
                    _readOnlyRefreshFlight = null;
                }
            }
            readOnlyFlight?.DiscoveryLease?.Dispose();
            readOnlyFlight?.Cancellation.Dispose();
        }
    }

    private async Task<bool> RunReadOnlyRefreshAsync(
        RespireServerException rejection, RespireConnection source, int slot, CancellationToken cancellationToken,
        DiscoveryRound? discovery)
    {
        using var scope = BeginDiscovery(discovery);
        try
        {
            if (scope.Round is { HasPendingFailure: false } round)
                round.Failed(new RespireEndpoint(source.Host, source.Port), rejection);
            _ = await RefreshReadOnlyOwnerCoreAsync(rejection, source, slot, cancellationToken, scope.Round)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            if (error is RespireConfigurationException) throw;
            if (error is OperationCanceledException or RespireException or IOException)
            {
                try { _logger?.LogDebug(error, "Redis Cluster READONLY recovery failed"); }
                catch (Exception) { }
                return false;
            }
            throw;
        }
    }

    private async Task<bool> RefreshTopologySharedAsync(CancellationToken waiterToken, bool allowRecentSuccessfulResult = true)
    {
        while (true)
        {
            Task<bool> refresh;
            TaskCompletionSource<bool>? start = null;
            ReadOnlyRefreshFlight? readOnlyFlight;
            lock (_sharedRefreshGate)
            {
                if (allowRecentSuccessfulResult && _sharedRefreshTask is null && _hasTopologyRefreshTimestamp
                    && TopologyRefreshClock.GetElapsedTime(_lastTopologyRefreshTimestamp) < TopologyRefreshCoalescingWindow)
                    return true;
                if (_sharedRefreshTask is null)
                {
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sharedRefreshTask = start.Task;
                }
                refresh = _sharedRefreshTask;
                readOnlyFlight = _readOnlyRefreshFlight;
                if (readOnlyFlight is not null) readOnlyFlight.Waiters++;
            }

            if (start is not null) _ = CompleteSharedRefreshAsync(start, RunTopologyRefreshAsync);
            if (readOnlyFlight is null)
                return await AwaitTopologyRefreshAsync(refresh, waiterToken, null).ConfigureAwait(false);

            // A READONLY flight only repairs its initiating slot. Keep this topology request
            // pending, then start a full refresh after that flight releases the shared slot.
            try { _ = await AwaitTopologyRefreshAsync(refresh, waiterToken, readOnlyFlight).ConfigureAwait(false); }
            catch (Exception) when (!waiterToken.IsCancellationRequested) { }
            while (true)
            {
                lock (_sharedRefreshGate)
                    if (!ReferenceEquals(_sharedRefreshTask, refresh)) break;
                waiterToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            allowRecentSuccessfulResult = false;
        }
    }

    private async Task<bool> AwaitTopologyRefreshAsync(Task<bool> task, CancellationToken waiterToken,
        ReadOnlyRefreshFlight? flight)
    {
        try
        {
            return waiterToken.CanBeCanceled
                ? await task.WaitAsync(waiterToken).ConfigureAwait(false)
                : await task.ConfigureAwait(false);
        }
        finally { ReleaseReadOnlyWaiter(flight); }
    }

    private async Task<bool> RunTopologyRefreshAsync()
    {
        using var scope = BeginDiscovery(null);
        try
        {
            var round = scope.Round;
            var candidates = new List<RespireConnectionMultiplexer>();
            var seen = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
            foreach (var master in Volatile.Read(ref _masters))
            {
                if (!master.IsRetired && seen.Add(master)) candidates.Add(master);
            }
            foreach (var endpoint in _seeds)
            {
                var seedNode = GetOrCreateNode(endpoint);
                if (seen.Add(seedNode)) candidates.Add(seedNode);
            }
            var connected = candidates.Select((node, index) => (node, index))
                .Where(static candidate => candidate.node.IsConnected && !candidate.node.IsRetired)
                .Select(static candidate => candidate.index).ToArray();
            var position = connected.Length == 0 ? -1 : connected[Random.Shared.Next(connected.Length)];
            if (position > 0) candidates = candidates.Skip(position).Concat(candidates.Take(position)).ToList();
            var boundedCandidateCount = Math.Max(1, candidates.Count);
            var maxTotalTicks = MaximumTopologyRefreshDeadline.Ticks;
            var configuredCandidateTimeout = _options.CommandTimeout ?? _options.ConnectTimeout;
            var candidateTimeout = TimeSpan.FromTicks(Math.Min(configuredCandidateTimeout.Ticks,
                maxTotalTicks / boundedCandidateCount));
            var totalTimeoutTicks = candidateTimeout.Ticks * boundedCandidateCount;
            using var deadline = new CancellationTokenSource(TimeSpan.FromTicks(totalTimeoutTicks));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stopDiscovery.Token);
            foreach (var candidate in candidates)
            {
                linked.Token.ThrowIfCancellationRequested();
                using var candidateDeadline = new CancellationTokenSource(candidateTimeout);
                using var candidateToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, candidateDeadline.Token);
                try
                {
                    await EnsureRouteNodeConnectedAsync(candidate, candidateToken.Token, round).ConfigureAwait(false);
                    if (await TryLoadSlotsAsync(candidate, candidateToken.Token, requireComplete: true).ConfigureAwait(false)
                        && HasCompleteTopology())
                    {
                        SetSeed(candidate);
                        return true;
                    }
                    round?.FailedNode(candidate, new RespireConnectionException("Cluster candidate did not provide a complete topology."));
                }
                catch (OperationCanceledException) when (candidateDeadline.IsCancellationRequested && !linked.IsCancellationRequested)
                {
                    round?.FailedNode(candidate, new TimeoutException("Redis Cluster topology candidate timed out."));
                }
                catch (Exception error) when (error is not OperationCanceledException && IsDiscoveryFailure(error))
                {
                    round?.FailedNode(candidate, error);
                }
            }
            scope.SetTerminalError(new RespireConnectionException("Redis Cluster topology refresh found no complete topology."));
            try { _logger?.LogWarning("Redis Cluster topology refresh failed for all {CandidateCount} candidates", candidates.Count); }
            catch (Exception) { }
            return false;
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            try { _logger?.LogDebug(error, "Redis Cluster topology refresh failed"); }
            catch (Exception) { }
            return false;
        }
    }

    private void StartTopologyRefreshWorker()
    {
        lock (_topologyRefreshWorkerGate)
        {
            if (_topologyRefreshStarted != 0 || _stopDiscovery.IsCancellationRequested) return;
            _topologyRefreshStarted = 1;
            var interval = _options.ClusterTopologyRefreshInterval;
            Volatile.Write(ref _topologyRefreshWorker, Task.Run(async () =>
            {
                while (!_stopDiscovery.IsCancellationRequested)
                {
                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token);
                    var signal = _topologyRefreshSignal.WaitAsync(waitCancellation.Token);
                    var timer = interval is { } delay && delay > TimeSpan.Zero
                        ? DelayTopologyRefreshIntervalAsync(delay, waitCancellation.Token)
                        : Task.Delay(Timeout.InfiniteTimeSpan, waitCancellation.Token);
                    var completed = await Task.WhenAny(timer, signal).ConfigureAwait(false);
                    await waitCancellation.CancelAsync().ConfigureAwait(false);
                    try { await Task.WhenAll(timer, signal).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
                    if (_stopDiscovery.IsCancellationRequested) return;
                    TakeTopologyRefreshSignal(
                        ReferenceEquals(completed, signal) || signal.IsCompletedSuccessfully,
                        out var wasSignaled, out var delayMilliseconds, out var force);
                    if (wasSignaled && delayMilliseconds > 0 && !force)
                        force = await WaitForTopologyRefreshDelayAsync(delayMilliseconds).ConfigureAwait(false);
                    if (_stopDiscovery.IsCancellationRequested) return;
                    try
                    {
                        await RefreshTopologySharedAsync(_stopDiscovery.Token,
                            allowRecentSuccessfulResult: wasSignaled && !force).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        try { _logger?.LogDebug(error, "Periodic Redis Cluster topology refresh failed"); }
                        catch (Exception) { }
                    }
                }
            }));
        }
    }

    private async Task DelayTopologyRefreshIntervalAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var segment = TimeSpan.FromDays(24);
        while (interval > segment)
        {
            await Task.Delay(segment, TopologyRefreshClock, cancellationToken).ConfigureAwait(false);
            interval -= segment;
        }
        await Task.Delay(interval, TopologyRefreshClock, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForTopologyRefreshDelayAsync(int delayMilliseconds)
    {
        var debounce = TimeSpan.FromMilliseconds(delayMilliseconds);
        var started = TopologyRefreshClock.GetTimestamp();
        while (delayMilliseconds > 0)
        {
            var elapsed = TopologyRefreshClock.GetElapsedTime(started);
            var remaining = debounce - elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token);
            var delay = Task.Delay(remaining, TopologyRefreshClock, waitCancellation.Token);
            var signal = _topologyRefreshSignal.WaitAsync(waitCancellation.Token);
            var completed = await Task.WhenAny(delay, signal).ConfigureAwait(false);
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(delay, signal).ConfigureAwait(false); }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested) { }
            if (_stopDiscovery.IsCancellationRequested) return false;
            TakeTopologyRefreshSignal(
                ReferenceEquals(completed, signal) || signal.IsCompletedSuccessfully,
                out var wasSignaled, out var nextDelay, out var force);
            if (!wasSignaled) return false;
            if (force) return true;
            if (nextDelay == 0) return false;
            // Repeated MOVED signals keep the first debounce deadline; they cannot
            // postpone discovery indefinitely while routing remains stale.
        }
        return false;
    }

    internal void SignalTopologyRefresh(int delayMilliseconds = 0, bool force = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_topologyRefreshSignalGate)
        {
            if (delayMilliseconds == 0) _topologyRefreshDelayMilliseconds = 0;
            else if (_topologyRefreshDelayMilliseconds == 0) _topologyRefreshDelayMilliseconds = delayMilliseconds;
            if (force) _topologyRefreshForce = 1;
            try { _topologyRefreshSignal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private void TakeTopologyRefreshSignal(
        bool observedSignal, out bool wasSignaled, out int delayMilliseconds, out bool force)
    {
        lock (_topologyRefreshSignalGate)
        {
            // A new signal can arrive after WaitAsync consumes its permit but before this lock.
            // Drain that permit before clearing the coalesced state, or its stale wake can skip
            // the remaining MOVED delay on the next wait.
            var queuedSignal = _topologyRefreshSignal.Wait(0);
            wasSignaled = observedSignal || queuedSignal;
            delayMilliseconds = wasSignaled ? _topologyRefreshDelayMilliseconds : 0;
            force = wasSignaled && _topologyRefreshForce != 0;
            if (wasSignaled)
            {
                _topologyRefreshDelayMilliseconds = 0;
                _topologyRefreshForce = 0;
            }
        }
    }

    internal event Action<RespireConnectionStateChange>? DiscoveryStateChanged;

    // One cold discovery operation owns this scope; nested helpers borrow the same round.
    // A null policy allocates neither a round nor a linked cancellation source.
    private DiscoveryScope BeginDiscovery(DiscoveryRound? shared)
        => new(shared ?? (_options.ReconnectPolicy is { } policy ? new DiscoveryRound(this, policy) : null),
            shared is null);

    private readonly struct DiscoveryScope(DiscoveryRound? round, bool ownsRound) : IDisposable
    {
        internal DiscoveryRound? Round => round;
        // An owning scope reports only discovery failures, not subsequent application errors.
        internal void SetTerminalError(Exception error)
        {
            if (ownsRound && round is not null) round.TerminalError = error;
        }
        public void Dispose()
        {
            if (ownsRound) round?.Finish();
        }
    }

    // A command creates a round only after retirement or an explicit server rejection.
    // Keep it through redirects and later retirement so neither starts a fresh retry budget.
    internal void RecordRejection(ref DiscoveryRound? round, RespireConnection source, Exception error)
        => RecordRejection(ref round, new RespireEndpoint(source.Host, source.Port), error);

    internal void RecordRejection(ref DiscoveryRound? round, RespireEndpoint endpoint, Exception error)
    {
        if (_options.ReconnectPolicy is not { } policy) return;
        round ??= new DiscoveryRound(this, policy);
        round.Failed(endpoint, error);
    }

    internal sealed class DiscoveryRoundUsageException() : InvalidOperationException(
        "DiscoveryRound must have only one sequential consumer and cannot be reused after finishing.");

    // One asynchronous control flow owns a round. Nested discovery helpers borrow it only
    // through sequential awaits; master fan-out is sequential too. Concurrent callers own
    // separate rounds, even when the seed gate coalesces their physical connection work.
    // Failed records a rejected candidate; BeforeCandidateAsync consumes that failure once.
    // Success alone does not consume another attempt (for example, required master fan-out).
    // Every router entry requires an explicit round argument; null deliberately starts a new round.
    // Runtime guards reject overlapping state transitions, including mutations during a backoff wait.
    // Finish is non-throwing and idempotent; an outstanding transition publishes the deferred finish.
    internal sealed class DiscoveryRound(ClusterRouter owner, RespireReconnectPolicy policy)
    {
        private readonly object _lifecycleGate = new();
        private enum Lifecycle { Idle, InTransition, FinishPending, Done }
        private Lifecycle _lifecycle;
        private int _sharedHolds;
        private bool _finishRequested;

        internal IDisposable Hold()
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle is Lifecycle.FinishPending or Lifecycle.Done || _finishRequested)
                    throw new DiscoveryRoundUsageException();
                _sharedHolds++;
            }
            return new DiscoveryRoundHold(this);
        }

        private sealed class DiscoveryRoundHold(DiscoveryRound round) : IDisposable
        {
            private DiscoveryRound? _round = round;
            public void Dispose() => Interlocked.Exchange(ref _round, null)?.ReleaseHold();
        }

        private void ReleaseHold()
        {
            bool finish;
            lock (_lifecycleGate)
            {
                if (_sharedHolds == 0) return;
                _sharedHolds--;
                finish = _sharedHolds == 0 && _finishRequested;
            }
            if (finish) Finish();
        }
        private void Enter(Exception? originalError = null)
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.Idle)
                {
                    // Error bookkeeping must not replace the failure it is already unwinding.
                    // Reject the mutation without changing shared state or losing the original stack.
                    if (originalError is not null) ExceptionDispatchInfo.Capture(originalError).Throw();
                    throw new DiscoveryRoundUsageException();
                }
                _lifecycle = Lifecycle.InTransition;
            }
        }
        private void Exit()
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.FinishPending)
                {
                    _lifecycle = Lifecycle.Idle;
                    return;
                }
                _lifecycle = Lifecycle.Done;
            }
            CompleteFinish();
        }

        // Only the sequential consumer reads or writes retry/outcome fields. The lifecycle gate
        // excludes overlapping consumers and hands a concurrent Finish to the active transition.
        private Exception? _failure;
        private HashSet<RespireConnectionMultiplexer>? _rejectedNodes;
        internal bool HasRejected(RespireConnectionMultiplexer node) => _rejectedNodes?.Contains(node) == true;
        internal void FailedNode(RespireConnectionMultiplexer node, Exception error)
            => Failed(Endpoint(node), error, node);
        private RespireEndpoint _endpoint;
        private int _attempts;
        private long _episode;
        internal RespireReconnectLimitException? Exhaustion { get; private set; }
        private Exception? _terminalError;
        internal Exception? TerminalError
        {
            get => _terminalError;
            set
            {
                Enter(value);
                try { _terminalError = value; }
                finally { Exit(); }
            }
        }
        internal bool HasPendingFailure => _failure is not null;
        internal Exception? PendingFailure => _failure;

        internal void RecordCommandFailure(Exception error, bool discoveryPending)
        {
            // Retirement rejects a command before admission, including when the command's
            // redirect cap prevents another retry. Application errors after admission do not
            // change the outcome of an otherwise successful discovery episode.
            if (discoveryPending || error is RespireConnectionRetiredException) TerminalError = error;
        }

        internal void RecordCommandFailure(Exception error, bool discoveryPending, int? commandSlot, bool noRedirect = false)
        {
            // A route can reject the final send after selection succeeded. Reaching the command's
            // redirect cap ends recovery unsuccessfully even when the policy still permits retries.
            // NoRedirect and unkeyed READONLY deliberately remain ordinary command errors.
            var routingRejected = !noRedirect && error is RespireServerException rejection && CanRecover(rejection, commandSlot);
            RecordCommandFailure(error, discoveryPending || routingRejected);
        }

        // Retirement wrappers preserve the endpoint selected by the last BeforeCandidateAsync.
        // Repeated reports replace the pending failure; only scheduling another candidate consumes it.
        // A caller can handle a scheduling cancellation as a retryable candidate failure (for
        // example, READONLY's primary phase timeout). Clear only that same tentative terminal
        // error; unrelated terminal failures and latched exhaustion remain authoritative.
        internal void Failed(Exception error)
        {
            Enter(error);
            try
            {
                _failure = error;
                if (Exhaustion is null && ReferenceEquals(_terminalError, error)) _terminalError = null;
            }
            finally { Exit(); }
        }

        internal void Failed(RespireEndpoint endpoint, Exception error, RespireConnectionMultiplexer? rejectedNode = null)
        {
            Enter(error);
            try
            {
                _endpoint = endpoint;
                _failure = error;
                // Track generation identity, not its address: a replacement at that address
                // remains eligible. Allocate only after an actual cold-path rejection.
                if (rejectedNode is not null)
                    (_rejectedNodes ??= new(ReferenceEqualityComparer.Instance)).Add(rejectedNode);
                if (Exhaustion is null && ReferenceEquals(_terminalError, error)) _terminalError = null;
            }
            finally { Exit(); }
        }

        internal async ValueTask BeforeCandidateAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
                if (Exhaustion is { } exhausted) throw exhausted;
                if (_failure is null)
                {
                    _endpoint = endpoint;
                    return;
                }
                if (policy.IsExhausted(_attempts))
                {
                    Exhaustion = new RespireReconnectLimitException(
                        $"Redis Cluster discovery exhausted {_attempts} fallback attempts.", _failure);
                    Publish(RespireConnectionState.Disconnected, _failure, exhausted: true);
                    throw Exhaustion;
                }
                var failure = _failure;
                _failure = null;
                _endpoint = endpoint;
                // Unlimited policies must not wrap the backoff index during long-lived recovery.
                if (_attempts < int.MaxValue) _attempts++;
                if (_episode == 0) _episode = Interlocked.Increment(ref _nextDiscoveryEpisode);
                var delay = policy.GetDelay(_attempts);
                Publish(RespireConnectionState.Reconnecting, failure, delay);
                await WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Cancellation, disposal and exhaustion terminate this candidate scheduling attempt.
                // Record the error before Exit so a racing Finish cannot publish success.
                _terminalError ??= error;
                throw;
            }
            finally { Exit(); }
        }

        private async ValueTask WaitAsync(TimeSpan delay, CancellationToken callerToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, owner._stopDiscovery.Token);
            try { await Task.Delay(delay, owner.DiscoveryClock, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(error.Message, error, callerToken);
            }
        }

        internal void Finish()
        {
            // A finish request during an asynchronous transition is handed to Exit.
            // Mark completion under the gate, then publish outside it exactly once.
            lock (_lifecycleGate)
            {
                if (_lifecycle is Lifecycle.Done or Lifecycle.FinishPending) return;
                if (_sharedHolds > 0)
                {
                    _finishRequested = true;
                    return;
                }
                if (_lifecycle == Lifecycle.InTransition)
                {
                    _lifecycle = Lifecycle.FinishPending;
                    return;
                }
                _lifecycle = Lifecycle.Done;
            }
            CompleteFinish();
        }

        private void CompleteFinish()
        {
            // No scheduled fallback means no discovery episode was started; physical
            // connection health still reports the initial candidate's failure.
            if (_attempts == 0 || Exhaustion is not null || Volatile.Read(ref owner._disposed) != 0) return;
            // Pending failure is retry bookkeeping, not the operation's outcome: another
            // caller may have connected the shared seed before we acquired its gate.
            // Owning scopes and command recovery record terminal errors when selection fails.
            Publish(TerminalError is null ? RespireConnectionState.Connected : RespireConnectionState.Disconnected, TerminalError);
        }

        private void Publish(RespireConnectionState state, Exception? error, TimeSpan? delay = null, bool exhausted = false)
            => owner.QueueDiscoveryState(new RespireConnectionStateChange(_endpoint, state, error)
            {
                ReconnectSource = RespireReconnectSource.ClusterDiscovery,
                SourceState = state,
                ReconnectEpisodeId = _episode,
                ReconnectAttempt = _attempts,
                NextReconnectDelay = delay,
                ReconnectExhausted = exhausted,
            });
    }

    private void QueueDiscoveryState(RespireConnectionStateChange change)
    {
        lock (_discoveryNotificationsGate)
        {
            (_discoveryNotifications ??= new()).Enqueue(change);
            StartDiscoveryPublisher();
        }
    }

    // Called only while holding _discoveryNotificationsGate. One drainer preserves event and
    // measurement order for each episode even when observers enqueue further discovery work.
    private void StartDiscoveryPublisher()
    {
        if (_publishingDiscovery || _discoveryNotifications is not { Count: > 0 }) return;
        _publishingDiscovery = true;
        ThreadPool.UnsafeQueueUserWorkItem(static router => router.PublishDiscoveryStates(), this, preferLocal: false);
    }

    private void PublishDiscoveryStates()
    {
        try
        {
            while (true)
            {
                RespireConnectionStateChange change;
                lock (_discoveryNotificationsGate)
                {
                    if (!_discoveryNotifications!.TryDequeue(out change))
                    {
                        return;
                    }
                }
                try
                {
                    if (change.NextReconnectDelay is not null || change.ReconnectExhausted)
                        RespireTelemetry.RecordDiscoveryReconnect(change.Endpoint, "cluster-discovery",
                            change.ReconnectAttempt, change.NextReconnectDelay, _logger);
                }
                catch (Exception error) { LogDiscoveryObserverFailure(error); }
                // Measurements describe already scheduled work. Observers may synchronously dispose
                // the client, so the dispatcher is independent of discovery and is never joined.
                if (Volatile.Read(ref _disposed) != 0) continue;
                try { DiscoveryStateChanged?.Invoke(change); }
                catch (Exception error) { LogDiscoveryObserverFailure(error); }
            }
        }
        finally
        {
            lock (_discoveryNotificationsGate)
            {
                _publishingDiscovery = false;
                // Enqueue can race the final empty check. Hand off any queued observations
                // before relinquishing the gate, including after an unexpected drain failure.
                StartDiscoveryPublisher();
            }
        }
    }
    private void LogDiscoveryObserverFailure(Exception error)
    {
        try { _logger?.LogWarning(error, "Cluster discovery observer threw"); }
        catch (Exception) { /* A user logger must not terminate the notification dispatcher. */ }
    }

}
