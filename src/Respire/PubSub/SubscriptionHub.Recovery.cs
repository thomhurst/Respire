using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class SubscriptionHub
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    // All configured recovery state is guarded by _reconnectStateGate. _gate protects
    // route membership separately; ordinary recovery holds _controlGate while restoring routes.
    // When nested, acquire _reconnectStateGate before _gate, never the reverse.
    private RespireConnection? _configuredConnection;
    private TaskCompletionSource? _configuredRecoveryDrained;
    private RespireReconnectLimitException? _recoveryExhaustion;
    private ConfiguredRecoveryPhase _configuredRecoveryPhase;

    private enum ConfiguredRecoveryPhase { Idle, Recovering, Exhausted }

    private RespireConnection? GetConnectionForCaller(bool watch)
    {
        if (!watch || core.Options.ReconnectPolicy is null)
            return _connection is { IsAcceptingCommands: true } existing ? existing : null;
        lock (_reconnectStateGate)
        {
            if (_recoveryExhaustion is { } exhausted) throw exhausted;
            if (_configuredRecoveryPhase == ConfiguredRecoveryPhase.Recovering)
                throw new RespireConnectionException("Pub/sub recovery is in progress. Subscribe again after recovery completes.");
            if (_configuredConnection is not { } connection) return null;
            if (!ReferenceEquals(_connection, connection) || !connection.IsAcceptingCommands)
                throw new RespireConnectionException("Pub/sub connection closed. Automatic recovery must complete before subscribing.");
            // Return the same connection whose recovery ownership was checked. If it closes
            // immediately afterward, the caller fails on that socket; only its watcher may
            // replace it and restore existing routes with the configured delay and budget.
            return connection;
        }
    }

    private void StartConfiguredRecovery(RespireConnection connection, RespireReconnectPolicy policy)
    {
        TaskCompletionSource drained;
        lock (_reconnectStateGate)
        {
            if (_disposed || _configuredRecoveryPhase != ConfiguredRecoveryPhase.Idle
                || !ReferenceEquals(_configuredConnection, connection)) return;
            _configuredConnection = null;
            _configuredRecoveryPhase = ConfiguredRecoveryPhase.Recovering;
            _configuredRecoveryDrained = drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // Reserve ownership before starting any work. Observers are dispatched separately
        // so synchronous disposal can await this reservation without waiting on itself.
        _ = RecoverConfiguredAsync(connection, policy, drained);
    }

    private async Task RecoverConfiguredAsync(RespireConnection failed, RespireReconnectPolicy policy,
        TaskCompletionSource drained)
    {
        var endpoint = new RespireEndpoint(failed.Host, failed.Port);
        var failure = failed.CloseError;
        var attempt = 0;
        RespireConnection? restored = null;
        var cancellationToken = _lifetimeCancellation.Token;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt < int.MaxValue) attempt++;
                var delay = policy.GetDelay(attempt);
                QueueConfiguredState(endpoint, RespireConnectionState.Reconnecting, failure, attempt, delay);
                await Task.Delay(delay, _recoveryClock, cancellationToken).ConfigureAwait(false);

                await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await TryRestoreOnceAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    endpoint = result.Endpoint;
                    if (result.Connection is { } replacement)
                    {
                        restored = replacement;
                        return;
                    }
                    failure = result.Error!;
                    // Disposal can win while cleanup is awaited. It owns terminal subscription
                    // completion in that case, even if this was the last configured attempt.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (policy.IsExhausted(attempt))
                    {
                        ExhaustConfiguredRecovery(endpoint, failure, attempt);
                        return;
                    }
                    core.Logger?.LogWarning(failure, "Pub/sub recovery attempt {Attempt} failed", attempt);
                }
                finally
                {
                    _controlGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        finally
        {
            lock (_reconnectStateGate)
            {
                _configuredConnection = restored;
                if (_configuredRecoveryPhase == ConfiguredRecoveryPhase.Recovering)
                    _configuredRecoveryPhase = ConfiguredRecoveryPhase.Idle;
                if (restored is not null)
                    QueueConfiguredState(endpoint, RespireConnectionState.Connected, null, attempt);
                drained.TrySetResult();
            }
            // Closed is a completion task: if the socket dies before this call, the watcher
            // observes that completed task immediately and starts the next ordered episode.
            if (restored is not null) _ = WatchConnectionAsync(restored);
        }
    }

    private async ValueTask<(RespireConnection? Connection, RespireEndpoint Endpoint, Exception? Error)> TryRestoreOnceAsync(
        RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        // The caller holds _controlGate through cleanup and the retry/exhaustion decision.
        RespireConnection? replacement = null;
        try
        {
            // No watcher is installed until every current route is acknowledged.
            replacement = await EnsureConnectionAsync(cancellationToken, watch: false).ConfigureAwait(false);
            endpoint = new RespireEndpoint(replacement.Host, replacement.Port);
            await ResubscribeRoutesAsync(replacement, cancellationToken).ConfigureAwait(false);
            return (replacement, endpoint, null);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (replacement is not null && DetachConnection(replacement) is { } cleanup)
            {
                try { await cleanup.ConfigureAwait(false); }
                catch (Exception cleanupError)
                {
                    core.Logger?.LogWarning(cleanupError, "Failed to clean up a pub/sub replacement");
                }
            }
            return (null, endpoint, error);
        }
    }

    private async Task ResubscribeRoutesAsync(RespireConnection replacement, CancellationToken cancellationToken)
    {
        (SubscriptionKind Kind, RespireChannel Name)[] routes;
        lock (_gate)
        {
            var snapshot = new List<(SubscriptionKind, RespireChannel)>();
            for (var i = 0; i < _routes.Length; i++)
            {
                if (IsClusterSharded((SubscriptionKind)i)) continue;
                foreach (var name in _routes[i].Names) snapshot.Add(((SubscriptionKind)i, name));
            }
            routes = [.. snapshot];
        }
        foreach (var (kind, name) in routes)
            await SendControlAsync(replacement, SubscribeVerb(kind), SubscribeOperation(kind), name,
                cancellationToken, instrument: false).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!replacement.IsConnected)
            throw replacement.CloseError ?? new RespireConnectionException("Pub/sub replacement closed during resubscription.");
    }

    private void ExhaustConfiguredRecovery(RespireEndpoint endpoint, Exception failure, int attempt)
    {
        lock (_reconnectStateGate)
        {
            // Serialize exhaustion with disposal's _disposed publication, including the
            // completion reason. Cancellation can happen even after the caller's token check.
            if (_disposed) return;
            _recoveryExhaustion = new RespireReconnectLimitException(
                $"Pub/sub recovery exhausted {attempt} replacement attempts. Recreate the client to subscribe again.");
            _configuredRecoveryPhase = ConfiguredRecoveryPhase.Exhausted;
            HashSet<RespireSubscription> subscriptions = [];
            lock (_gate)
            {
                for (var i = 0; i < _routes.Length; i++)
                {
                    if (IsClusterSharded((SubscriptionKind)i)) continue;
                    foreach (var list in _routes[i].Values) subscriptions.UnionWith(list);
                    _routes[i].Clear();
                }
                foreach (var subscription in subscriptions) _interrupted.Remove(subscription);
            }
            // Completion only closes an internal buffer and signals asynchronous continuations.
            // No user callback runs here; keep the terminal reason serialized with disposal.
            foreach (var subscription in subscriptions) subscription.CompleteFromReconnectExhaustion();
            QueueConfiguredState(endpoint, RespireConnectionState.Disconnected, failure, attempt, exhausted: true);
        }
    }

    private void QueueConfiguredState(RespireEndpoint endpoint, RespireConnectionState state,
        Exception? error, int attempt, TimeSpan? delay = null, bool exhausted = false, bool clusterSharded = false)
    {
        lock (_reconnectStateGate)
        {
            if (_disposed) return;
            if (QueueReconnectStateLocked(new RespireConnectionStateChange(endpoint, state, error)
                { ReconnectAttempt = attempt, NextReconnectDelay = delay, ReconnectExhausted = exhausted }, clusterSharded))
                ThreadPool.UnsafeQueueUserWorkItem(static hub => hub.PublishReconnectStates(), this, preferLocal: false);
        }
    }
}
