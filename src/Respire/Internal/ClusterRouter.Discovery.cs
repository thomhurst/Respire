using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly CancellationTokenSource _stopDiscovery = new();
    private readonly object _discoveryNotificationsGate = new();
    private Queue<RespireConnectionStateChange>? _discoveryNotifications;
    private bool _publishingDiscovery;
    private static long _nextDiscoveryEpisode;

    internal event Action<RespireConnectionStateChange>? DiscoveryStateChanged;

    // One cold discovery operation owns this scope; nested helpers borrow the same round.
    // A null policy allocates neither a round nor a linked cancellation source.
    private DiscoveryScope BeginDiscovery(DiscoveryRound? shared)
        => new(shared ?? (_options.ReconnectPolicy is { } policy ? new DiscoveryRound(this, policy) : null),
            shared is null);

    private readonly struct DiscoveryScope(DiscoveryRound? round, bool ownsRound) : IDisposable
    {
        internal DiscoveryRound? Round => round;
        // Retirement wrappers preserve the endpoint selected by the last BeforeCandidateAsync.
        // Repeated reports replace the pending failure; only scheduling another candidate consumes it.
        internal void Failed(Exception error)
        {
            if (ownsRound && round is not null) round.TerminalError = error;
        }
        public void Dispose()
        {
            if (ownsRound) round?.Finish();
        }
    }

    // A command creates a round only after a send is rejected before acceptance. Keeping
    // this round in the command loop prevents each retired generation starting a new budget.
    internal void RecordRetirement(ref DiscoveryRound? round, RespireConnection source, Exception error)
        => RecordRetirement(ref round, new RespireEndpoint(source.Host, source.Port), error);

    internal void RecordRetirement(ref DiscoveryRound? round, RespireEndpoint endpoint, Exception error)
    {
        if (_options.ReconnectPolicy is not { } policy) return;
        round ??= new DiscoveryRound(this, policy);
        round.Failed(endpoint, error);
    }

    // One asynchronous control flow owns a round. Nested discovery helpers borrow it only
    // through sequential awaits; master fan-out is sequential too. Concurrent callers own
    // separate rounds, even when the seed gate coalesces their physical connection work.
    // Failed records a rejected candidate; BeforeCandidateAsync consumes that failure once.
    // Success alone does not consume another attempt (for example, required master fan-out).
    // Every router entry requires an explicit round argument; null deliberately starts a new round.
    // Runtime guards reject overlapping state transitions, including mutations during a backoff wait.
    internal sealed class DiscoveryRound(ClusterRouter owner, RespireReconnectPolicy policy)
    {
        private int _inUse;
        private void Enter()
        {
            if (Interlocked.CompareExchange(ref _inUse, 1, 0) != 0)
                throw new InvalidOperationException("DiscoveryRound must have only one sequential consumer.");
        }
        private void Exit() => Volatile.Write(ref _inUse, 0);

        private Exception? _failure;
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

        // Retirement wrappers preserve the endpoint selected by the last BeforeCandidateAsync.
        // Repeated reports replace the pending failure; only scheduling another candidate consumes it.
        internal void Failed(Exception error)
        {
            Enter();
            try { _failure = error; }
            finally { Exit(); }
        }

        internal void Failed(RespireEndpoint endpoint, Exception error)
        {
            Enter();
            try
            {
                _endpoint = endpoint;
                _failure = error;
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
            finally { Exit(); }
        }

        private async ValueTask WaitAsync(TimeSpan delay, CancellationToken callerToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, owner._stopDiscovery.Token);
            try { await Task.Delay(delay, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(error.Message, error, callerToken);
            }
        }

        internal void Finish()
        {
            Enter();
            try
            {
                if (_attempts == 0 || Exhaustion is not null || Volatile.Read(ref owner._disposed) != 0) return;
                Publish(TerminalError is null ? RespireConnectionState.Connected : RespireConnectionState.Disconnected, TerminalError);
            }
            finally { Exit(); }
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
            if (_publishingDiscovery) return;
            _publishingDiscovery = true;
            ThreadPool.UnsafeQueueUserWorkItem(static router => router.PublishDiscoveryStates(), this, preferLocal: false);
        }
    }

    private void PublishDiscoveryStates()
    {
        while (true)
        {
            RespireConnectionStateChange change;
            lock (_discoveryNotificationsGate)
            {
                if (!_discoveryNotifications!.TryDequeue(out change))
                {
                    _publishingDiscovery = false;
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
    private void LogDiscoveryObserverFailure(Exception error)
    {
        try { _logger?.LogWarning(error, "Cluster discovery observer threw"); }
        catch (Exception) { /* A user logger must not terminate the notification dispatcher. */ }
    }

}
