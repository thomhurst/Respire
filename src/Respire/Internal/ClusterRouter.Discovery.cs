using System.Diagnostics;
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
    {
        if (_options.ReconnectPolicy is not { } policy) return;
        round ??= new DiscoveryRound(this, policy);
        round.Failed(new RespireEndpoint(source.Host, source.Port), error);
    }

    // One asynchronous control flow owns a round. Nested discovery helpers borrow it only
    // through sequential awaits; master fan-out is sequential too. Concurrent callers own
    // separate rounds, even when the seed gate coalesces their physical connection work.
    // Failed records a rejected candidate; BeforeCandidateAsync consumes that failure once.
    // Success alone does not consume another attempt (for example, required master fan-out).
    internal sealed class DiscoveryRound(ClusterRouter owner, RespireReconnectPolicy policy)
    {
#if DEBUG
        private int _waiting;
#endif
        [Conditional("DEBUG")]
        private void AssertSequential()
        {
#if DEBUG
            Debug.Assert(Volatile.Read(ref _waiting) == 0, "DiscoveryRound must have only one sequential consumer.");
#endif
        }

        private Exception? _failure;
        private RespireEndpoint _endpoint;
        private int _attempts;
        private long _episode;
        internal RespireReconnectLimitException? Exhaustion { get; private set; }
        internal Exception? TerminalError { get; set; }
        internal bool HasPendingFailure => _failure is not null;

        internal void Failed(Exception error)
        {
            AssertSequential();
            _failure = error;
        }

        internal void Failed(RespireEndpoint endpoint, Exception error)
        {
            AssertSequential();
            _endpoint = endpoint;
            _failure = error;
        }

        internal ValueTask BeforeCandidateAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
        {
            AssertSequential();
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            if (Exhaustion is { } exhausted) throw exhausted;
            if (_failure is null)
            {
                _endpoint = endpoint;
                return default;
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
            return WaitAsync(delay, cancellationToken);
        }

        private async ValueTask WaitAsync(TimeSpan delay, CancellationToken callerToken)
        {
#if DEBUG
            Debug.Assert(Interlocked.Exchange(ref _waiting, 1) == 0);
#endif
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, owner._stopDiscovery.Token);
            try { await Task.Delay(delay, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (callerToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(error.Message, error, callerToken);
            }
            finally
            {
#if DEBUG
                Volatile.Write(ref _waiting, 0);
#endif
            }
        }

        internal void Finish()
        {
            AssertSequential();
            if (_attempts == 0 || Exhaustion is not null || Volatile.Read(ref owner._disposed) != 0) return;
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
            if (change.NextReconnectDelay is not null || change.ReconnectExhausted)
                RespireTelemetry.RecordDiscoveryReconnect(change.Endpoint, "cluster-discovery",
                    change.ReconnectAttempt, change.NextReconnectDelay, _logger);
            // Measurements describe already scheduled work. Observers may synchronously dispose
            // the client, so the dispatcher is independent of discovery and is never joined.
            if (Volatile.Read(ref _disposed) != 0) continue;
            try { DiscoveryStateChanged?.Invoke(change); }
            catch (Exception error) { _logger?.LogWarning(error, "Cluster discovery observer threw"); }
        }
    }
}
