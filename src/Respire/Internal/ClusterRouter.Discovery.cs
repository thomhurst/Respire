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
    // A redirect-driven refresh reuses a refresh that succeeded within the same window as the
    // MOVED debounce: both express "do not repeat discovery for one burst of redirects".
    private static readonly TimeSpan TopologyRefreshCoalescingWindow = ClusterTopologyRefreshScheduler.MovedDebounce;
    // Bounds one refresh pass. It is independent of ClusterTopologyRefreshInterval; a pass that
    // outlives a short interval simply coalesces the next trigger.
    private static readonly TimeSpan MaximumTopologyRefreshDeadline = TimeSpan.FromSeconds(60);
    // With many known nodes an even share of the deadline can be shorter than one round trip.
    private static readonly TimeSpan MinimumTopologyCandidateTimeout = TimeSpan.FromSeconds(2);
    private readonly object _topologyRefreshWorkerGate = new();
    private Task? _topologyRefreshWorker;
    private int _topologyRefreshStarted;
    private readonly object _sharedRefreshGate = new();
    // READONLY recovery and topology refresh share one flight so overlapping triggers do not
    // launch independent discovery loops. A flight is unpublished, under this gate, before its
    // task completes, so a caller that resumes from that task always finds the slot free.
    private RefreshFlight? _sharedRefresh;
    private long _lastTopologyRefreshTimestamp;
    private bool _hasTopologyRefreshTimestamp;

    internal enum RefreshFlightKind
    {
        /// <summary>Full topology discovery. A partial <c>CLUSTER SLOTS</c> map updates the slots it
        /// covers and keeps the current owners of the rest.</summary>
        Topology,
        /// <summary>Repairs the owner of one slot after a <c>READONLY</c> rejection.</summary>
        ReadOnly,
    }

    /// <summary>One physical refresh shared by every caller that joins it.</summary>
    /// <remarks>Waiters, <see cref="Completed"/> and <see cref="Abandoned"/> are guarded by
    /// <c>_sharedRefreshGate</c>. A READONLY flight is cancelled when its last waiter leaves;
    /// a topology flight runs until it finishes or the router is disposed.</remarks>
    internal sealed class RefreshFlight
    {
        private RefreshFlight(RefreshFlightKind kind, int slot, RespireEndpoint source,
            CancellationTokenSource? cancellation, IDisposable? discoveryLease)
        {
            Kind = kind;
            Slot = slot;
            Source = source;
            Cancellation = cancellation;
            DiscoveryLease = discoveryLease;
        }

        internal static RefreshFlight ForTopology() => new(RefreshFlightKind.Topology, -1, default, null, null);

        internal static RefreshFlight ForReadOnly(int slot, RespireEndpoint source,
            CancellationTokenSource cancellation, IDisposable? discoveryLease)
            => new(RefreshFlightKind.ReadOnly, slot, source, cancellation, discoveryLease);

        internal RefreshFlightKind Kind { get; }
        internal int Slot { get; }
        internal RespireEndpoint Source { get; }
        internal CancellationTokenSource? Cancellation { get; }
        internal IDisposable? DiscoveryLease { get; }
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Task => Completion.Task;
        internal int Waiters;
        internal bool Completed;
        internal bool Abandoned;

        internal bool Repairs(int slot, RespireConnection source)
            => Kind == RefreshFlightKind.ReadOnly && Slot == slot && Source.Port == source.Port
                && string.Equals(Source.Host, source.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A READONLY caller's view of the shared flight it started or joined.</summary>
    /// <param name="Flight">The flight to await through <see cref="AwaitSharedRefreshAsync"/>.</param>
    /// <param name="NeedsOwnSlotRecovery">True when the flight was started by someone else for a
    /// different slot, source or for full discovery, so it may leave this caller's slot stale.</param>
    private readonly record struct ReadOnlyRefreshJoin(RefreshFlight Flight, bool NeedsOwnSlotRecovery);

    private ReadOnlyRefreshJoin JoinReadOnlyRefresh(
        RespireServerException rejection, RespireConnection source, int slot, DiscoveryRound? discovery)
    {
        RefreshFlight flight;
        var started = false;
        lock (_sharedRefreshGate)
        {
            if (_sharedRefresh is null)
            {
                _sharedRefresh = RefreshFlight.ForReadOnly(slot, new RespireEndpoint(source.Host, source.Port),
                    CancellationTokenSource.CreateLinkedTokenSource(_stopDiscovery.Token), discovery?.Hold());
                started = true;
            }
            flight = _sharedRefresh;
            flight.Waiters++;
        }
        if (started)
        {
            _ = CompleteSharedRefreshAsync(flight, () => RunReadOnlyRefreshAsync(
                rejection, source, slot, flight.Cancellation!.Token, discovery));
        }
        return new ReadOnlyRefreshJoin(flight, !started && !flight.Repairs(slot, source));
    }

    private async Task<bool> AwaitSharedRefreshAsync(
        RefreshFlight flight, CancellationToken waiterToken, DiscoveryRound? discovery)
    {
        var canceled = false;
        try
        {
            return waiterToken.CanBeCanceled
                ? await flight.Task.WaitAsync(waiterToken).ConfigureAwait(false)
                : await flight.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waiterToken.IsCancellationRequested)
        {
            canceled = true;
            throw;
        }
        finally
        {
            var abandoned = ReleaseWaiter(flight);
            // The READONLY flight still runs for other waiters, or has already finished. Either
            // way it records its own outcome, so this caller's cancellation must not replace it.
            if (canceled && !abandoned && flight.Kind == RefreshFlightKind.ReadOnly)
                discovery?.LeftSurvivingSharedFlight();
        }
    }

    /// <summary>Returns true when this waiter was the last one and abandoned a running flight.</summary>
    private bool ReleaseWaiter(RefreshFlight flight)
    {
        lock (_sharedRefreshGate)
        {
            if (flight.Waiters > 0) flight.Waiters--;
            if (flight.Kind != RefreshFlightKind.ReadOnly || flight.Completed || flight.Waiters != 0) return false;
            flight.Abandoned = true;
            if (ReferenceEquals(_sharedRefresh, flight)) _sharedRefresh = null;
        }
        try { flight.Cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    private async Task CompleteSharedRefreshAsync(RefreshFlight flight, Func<Task<bool>> work)
    {
        var result = false;
        Exception? failure = null;
        try { result = await work().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }

        // Unpublish once, before completing the task. A topology request that waited behind a
        // READONLY flight then starts its own full refresh without polling for the slot.
        lock (_sharedRefreshGate)
        {
            flight.Completed = true;
            if (ReferenceEquals(_sharedRefresh, flight)) _sharedRefresh = null;
            // Only a full refresh proves the whole map is current. A READONLY repair must not
            // suppress a topology refresh requested right after it.
            if (failure is null && result && flight.Kind == RefreshFlightKind.Topology)
            {
                _lastTopologyRefreshTimestamp = _topologyRefreshClock.GetTimestamp();
                _hasTopologyRefreshTimestamp = true;
            }
        }

        if (failure is null) flight.Completion.TrySetResult(result);
        else flight.Completion.TrySetException(failure);
        flight.Cancellation?.Dispose();
        flight.DiscoveryLease?.Dispose();
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
            if (scope.Round is { } round) round.TerminalError = error;
            if (error is RespireConfigurationException) throw;
            if (error is OperationCanceledException or RespireException or IOException)
            {
                _logger.TryLog(LogLevel.Debug, error, "Redis Cluster READONLY recovery failed");
                return false;
            }
            throw;
        }
    }

    private async Task<TopologyRefreshOutcome> RefreshTopologySharedAsync(
        CancellationToken waiterToken, bool allowRecentSuccessfulResult = true)
    {
        while (true)
        {
            RefreshFlight flight;
            var started = false;
            lock (_sharedRefreshGate)
            {
                if (allowRecentSuccessfulResult && _sharedRefresh is null && _hasTopologyRefreshTimestamp
                    && _topologyRefreshClock.GetElapsedTime(_lastTopologyRefreshTimestamp) < TopologyRefreshCoalescingWindow)
                    return TopologyRefreshOutcome.ReusedRecent;
                if (_sharedRefresh is null)
                {
                    _sharedRefresh = RefreshFlight.ForTopology();
                    started = true;
                }
                flight = _sharedRefresh;
                flight.Waiters++;
            }

            if (started) _ = CompleteSharedRefreshAsync(flight, RunTopologyRefreshAsync);
            if (flight.Kind == RefreshFlightKind.Topology)
            {
                return await AwaitSharedRefreshAsync(flight, waiterToken, discovery: null).ConfigureAwait(false)
                    ? TopologyRefreshOutcome.Refreshed
                    : TopologyRefreshOutcome.Failed;
            }

            // A READONLY flight only repairs its initiating slot. Its outcome does not answer this
            // request, so wait for it to unpublish itself and then run a full refresh. This waiter
            // also keeps the flight from being cancelled while it is the only one left.
            try { _ = await AwaitSharedRefreshAsync(flight, waiterToken, discovery: null).ConfigureAwait(false); }
            catch (Exception error) when (!waiterToken.IsCancellationRequested)
            {
                _logger.TryLog(LogLevel.Debug, error, "Redis Cluster READONLY recovery failed before topology refresh");
            }
            allowRecentSuccessfulResult = false;
        }
    }

    /// <summary>A topology source to try: an existing transport, or an address whose transport is
    /// created only if the refresh actually reaches it.</summary>
    internal readonly record struct TopologyRefreshCandidate(
        RespireConnectionMultiplexer? Node, RespireEndpoint Endpoint, bool IsConfiguredSeed)
    {
        internal bool IsConnected => Node is { IsConnected: true, IsRetired: false };
    }

    private async Task<bool> RunTopologyRefreshAsync()
    {
        using var scope = BeginDiscovery(null);
        try
        {
            var round = scope.Round;
            var candidates = OrderTopologyRefreshCandidates(GetTopologyRefreshCandidates());
            var configuredCandidateTimeout = _options.CommandTimeout ?? _options.ConnectTimeout;
            var clock = _topologyRefreshClock;
            var refreshStarted = clock.GetTimestamp();
            var appliedPartialTopology = false;
            using var deadline = new CancellationTokenSource(MaximumTopologyRefreshDeadline, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stopDiscovery.Token);
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var candidate = candidates[candidateIndex];
                var budget = GetTopologyCandidateBudget(
                    MaximumTopologyRefreshDeadline - clock.GetElapsedTime(refreshStarted),
                    candidates.Count - candidateIndex, configuredCandidateTimeout);
                // Backoff may use at most an even share, so later candidates keep their time.
                if (round is not null)
                    await round.BeforeCandidateAsync(candidate.Endpoint, linked.Token, budget.EvenShare).ConfigureAwait(false);
                // Measure the I/O timeout after any backoff so the wait cannot consume it.
                budget = GetTopologyCandidateBudget(
                    MaximumTopologyRefreshDeadline - clock.GetElapsedTime(refreshStarted),
                    candidates.Count - candidateIndex, configuredCandidateTimeout);
                if (budget.Timeout <= TimeSpan.Zero) break;
                var node = candidate.Node ?? GetOrCreateNode(candidate.Endpoint, observe: false);
                if (node.IsRetired) continue;
                using var candidateDeadline = new CancellationTokenSource(budget.Timeout, clock);
                using var candidateToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, candidateDeadline.Token);
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, candidateToken.Token, discovery: null).ConfigureAwait(false);
                    // Apply partial maps while continuing through known candidates. A later node
                    // may provide the complete map needed to replace stale routes during failover.
                    var load = await TryLoadSlotsAsync(node, candidateToken.Token, keepUncoveredOwners: true)
                        .ConfigureAwait(false);
                    if (load.Loaded)
                    {
                        SetSeed(node);
                        if (load.CoversAllSlots) return true;
                        appliedPartialTopology = true;
                        continue;
                    }
                    round?.FailedNode(node, new RespireConnectionException("Cluster candidate did not provide a topology."));
                }
                catch (OperationCanceledException) when (candidateDeadline.IsCancellationRequested && !linked.IsCancellationRequested)
                {
                    round?.FailedNode(node, new TimeoutException("Redis Cluster topology candidate timed out."));
                }
                catch (Exception error) when (error is not OperationCanceledException && IsDiscoveryFailure(error))
                {
                    round?.FailedNode(node, error);
                }
            }
            if (appliedPartialTopology) return true;
            scope.SetTerminalError(new RespireConnectionException("Redis Cluster topology refresh found no topology."));
            _logger.TryLog(LogLevel.Debug, null,
                "Redis Cluster topology refresh failed for all {CandidateCount} candidates", candidates.Count);
            return false;
        }
        catch (Exception error)
        {
            scope.SetTerminalError(error);
            _logger.TryLog(LogLevel.Debug, error, "Redis Cluster topology refresh failed");
            return false;
        }
    }

    /// <summary>Splits what is left of the refresh deadline across the remaining candidates.</summary>
    /// <returns><c>EvenShare</c> caps reconnect backoff. <c>Timeout</c> is the candidate's I/O
    /// timeout: the even share, raised to a usable floor, never above the configured command
    /// timeout or the time left.</returns>
    internal static (TimeSpan EvenShare, TimeSpan Timeout) GetTopologyCandidateBudget(
        TimeSpan timeLeft, int candidatesLeft, TimeSpan configuredCandidateTimeout)
    {
        if (timeLeft <= TimeSpan.Zero) return (TimeSpan.Zero, TimeSpan.Zero);
        var evenShare = TimeSpan.FromTicks(timeLeft.Ticks / Math.Max(1, candidatesLeft));
        var floor = configuredCandidateTimeout < MinimumTopologyCandidateTimeout
            ? configuredCandidateTimeout
            : MinimumTopologyCandidateTimeout;
        var share = evenShare > floor ? evenShare : floor;
        var timeout = share < configuredCandidateTimeout ? share : configuredCandidateTimeout;
        return (evenShare, timeout < timeLeft ? timeout : timeLeft);
    }

    private List<TopologyRefreshCandidate> GetTopologyRefreshCandidates()
    {
        var candidates = new List<TopologyRefreshCandidate>();
        var seenNodes = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
        var seenEndpoints = new HashSet<RespireEndpoint>(RespireEndpointComparer.Instance);
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Configured seeds are retained across topology snapshots, so this creates each
            // seed transport at most once. They are not observed as routed slot owners.
            foreach (var endpoint in _seeds)
            {
                var seed = _identities.GetOrCreate(endpoint);
                if (seenNodes.Add(seed) && seenEndpoints.Add(endpoint))
                    candidates.Add(new TopologyRefreshCandidate(seed, endpoint, IsConfiguredSeed: true));
            }
            foreach (var master in _masters)
            {
                if (!master.IsRetired && seenNodes.Add(master))
                {
                    var endpoint = Endpoint(master);
                    seenEndpoints.Add(endpoint);
                    candidates.Add(new TopologyRefreshCandidate(master, endpoint, IsConfiguredSeed: false));
                }
            }
            // Replicas are fallbacks for when every primary and seed has failed, for example after
            // a failover. Reuse a transport that already exists; otherwise create one only if the
            // refresh reaches this candidate, so healthy refreshes do not build and retire replica
            // transports on every pass.
            foreach (var replica in _replicas)
            {
                foreach (var endpoint in replica.Aliases.Prepend(replica.Endpoint))
                {
                    if (!seenEndpoints.Add(endpoint)) continue;
                    var existing = _identities.TryGet(endpoint);
                    if (existing is null)
                        candidates.Add(new TopologyRefreshCandidate(null, endpoint, IsConfiguredSeed: false));
                    else if (!existing.IsRetired && seenNodes.Add(existing))
                        candidates.Add(new TopologyRefreshCandidate(existing, endpoint, IsConfiguredSeed: false));
                }
            }
        }
        return candidates;
    }

    /// <summary>Orders candidates: one connected node, then disconnected configured seeds, then the
    /// remaining connected nodes, then the remaining disconnected nodes.</summary>
    /// <remarks>Connected nodes usually answer fastest and are tried first, starting at a random one
    /// to spread load. Disconnected configured seeds go right after the first attempt: they are the
    /// user's recovery path, and several stalled connected nodes must not use up the deadline
    /// before a healthy seed is tried. The remaining disconnected nodes and lazy replica fallbacks go
    /// last, starting at a random one: each attempt has a minimum timeout, so with many stalled
    /// fallbacks the deadline can run out before the end of the list, and a fixed order would miss
    /// the same usable fallback on every pass.</remarks>
    internal static List<TopologyRefreshCandidate> OrderTopologyRefreshCandidates(
        List<TopologyRefreshCandidate> candidates, Random? random = null)
    {
        random ??= Random.Shared;
        var connected = new List<TopologyRefreshCandidate>();
        var disconnectedSeeds = new List<TopologyRefreshCandidate>();
        var disconnected = new List<TopologyRefreshCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.IsConnected) connected.Add(candidate);
            else if (candidate.IsConfiguredSeed) disconnectedSeeds.Add(candidate);
            else disconnected.Add(candidate);
        }

        var ordered = new List<TopologyRefreshCandidate>(candidates.Count);
        var start = connected.Count == 0 ? 0 : random.Next(connected.Count);
        if (connected.Count > 0) ordered.Add(connected[start]);
        ordered.AddRange(disconnectedSeeds);
        for (var offset = 1; offset < connected.Count; offset++)
            ordered.Add(connected[(start + offset) % connected.Count]);
        var fallbackStart = disconnected.Count == 0 ? 0 : random.Next(disconnected.Count);
        for (var offset = 0; offset < disconnected.Count; offset++)
            ordered.Add(disconnected[(fallbackStart + offset) % disconnected.Count]);
        return ordered;
    }

    // Started once, from SetSeed: every path that first connects the router publishes a connected
    // seed there. Create stays lazy. The volatile check keeps later calls lock-free.
    private void StartTopologyRefreshWorker()
    {
        if (Volatile.Read(ref _topologyRefreshStarted) != 0 || _stopDiscovery.IsCancellationRequested) return;
        lock (_topologyRefreshWorkerGate)
        {
            if (_topologyRefreshStarted != 0 || _stopDiscovery.IsCancellationRequested) return;
            _topologyRefreshStarted = 1;
            Volatile.Write(ref _topologyRefreshWorker, Task.Run(() => _topologyRefresh.RunAsync(
                (allowRecentResult, stop) => RefreshTopologySharedAsync(stop, allowRecentResult),
                _logger, _stopDiscovery.Token)));
        }
    }

    /// <summary>Requests a full topology refresh. A forced request bypasses the recent-success window.</summary>
    internal void SignalTopologyRefresh(bool force = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _topologyRefresh.Request(TimeSpan.Zero, force);
    }

    /// <summary>Schedules a debounced refresh after a <c>MOVED</c> redirect. Later redirects keep the
    /// first deadline, so a stream of redirects cannot postpone discovery.</summary>
    internal void SignalMovedTopologyRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _topologyRefresh.Request(ClusterTopologyRefreshScheduler.MovedDebounce);
    }

    /// <summary>Requests a forced refresh after a primary disconnect, at most once per spacing window.</summary>
    internal void SignalPrimaryDisconnectRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _topologyRefresh.RequestPrimaryDisconnect();
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
        // Set when this round's caller was cancelled while a shared READONLY flight it had joined
        // kept running for other waiters, or had already finished. That flight owns the outcome.
        // A plain field is enough: it is written in AwaitSharedRefreshAsync's finally block and read
        // by RecordCommandFailure, and both run in the round owner's own sequential async flow (the
        // await between them publishes the write). The shared flight itself never touches it.
        private bool _leftSurvivingSharedFlight;
        internal void LeftSurvivingSharedFlight() => _leftSurvivingSharedFlight = true;
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

        private bool IsCancellationOwnedBySharedFlight(Exception error, CancellationToken callerToken)
            => _leftSurvivingSharedFlight && error is OperationCanceledException && callerToken.IsCancellationRequested;

        internal void RecordCommandFailure(Exception error, bool discoveryPending, CancellationToken callerToken = default)
        {
            // A canceled waiter does not own the result of a shared recovery flight. The
            // flight records its own terminal outcome when its work completes.
            if (IsCancellationOwnedBySharedFlight(error, callerToken)) return;
            // Retirement rejects a command before admission, including when the command's
            // redirect cap prevents another retry. Application errors after admission do not
            // change the outcome of an otherwise successful discovery episode.
            if (discoveryPending || error is RespireConnectionRetiredException) TerminalError = error;
        }

        internal void RecordCommandFailure(Exception error, bool discoveryPending, int? commandSlot,
            bool noRedirect = false, CancellationToken callerToken = default)
        {
            if (IsCancellationOwnedBySharedFlight(error, callerToken)) return;
            // A route can reject the final send after selection succeeded. Reaching the command's
            // redirect cap ends recovery unsuccessfully even when the policy still permits retries.
            // NoRedirect and unkeyed READONLY deliberately remain ordinary command errors.
            var routingRejected = !noRedirect && error is RespireServerException rejection && CanRecover(rejection, commandSlot);
            RecordCommandFailure(error, discoveryPending || routingRejected, callerToken);
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

        internal async ValueTask BeforeCandidateAsync(RespireEndpoint endpoint, CancellationToken cancellationToken,
            TimeSpan? maximumDelay = null)
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
                if (maximumDelay is { } maximum && delay > maximum) delay = maximum;
                Publish(RespireConnectionState.Reconnecting, failure, delay);
                if (delay > TimeSpan.Zero)
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
