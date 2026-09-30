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
        private bool _active;
        private bool _finishRequested;
        private bool _finished;
        private void Enter()
        {
            lock (_lifecycleGate)
            {
                if (_active || _finishRequested || _finished)
                    throw new DiscoveryRoundUsageException();
                _active = true;
            }
        }
        private void Exit()
        {
            lock (_lifecycleGate)
            {
                _active = false;
                if (!_finishRequested || _finished) return;
                _finished = true;
            }
            CompleteFinish();
        }

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
                Enter();
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

        // Retirement wrappers preserve the endpoint selected by the last BeforeCandidateAsync.
        // Repeated reports replace the pending failure; only scheduling another candidate consumes it.
        // A caller can handle a scheduling cancellation as a retryable candidate failure (for
        // example, READONLY's primary phase timeout). Clear only that same tentative terminal
        // error; unrelated terminal failures and latched exhaustion remain authoritative.
        internal void Failed(Exception error)
        {
            Enter();
            try
            {
                _failure = error;
                if (Exhaustion is null && ReferenceEquals(_terminalError, error)) _terminalError = null;
            }
            finally { Exit(); }
        }

        internal void Failed(RespireEndpoint endpoint, Exception error, RespireConnectionMultiplexer? rejectedNode = null)
        {
            Enter();
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
                _finishRequested = true;
                if (_active || _finished) return;
                _finished = true;
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

    // Called only while holding _discoveryNotificationsGate.
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
