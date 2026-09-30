using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal sealed partial class SubscriptionHub
{
    // Identity is the router's generation, not merely host:port. A replacement at the same
    // endpoint must not inherit the retired primary's subscription transport.
    private readonly Dictionary<RespireConnectionMultiplexer, PrimarySubscriptionConnection> _primaryConnections = [];
    private readonly ByteRouteDictionary<PrimarySubscriptionConnection> _shardedOwners = new();
    private TaskCompletionSource? _shardedRecovery;
    private bool _shardedRecoveryRequested;
    private bool _observingClusterTopology;
    private RespireReconnectLimitException? _shardedExhaustion;

    private sealed class PrimarySubscriptionConnection(RespireConnectionMultiplexer owner)
    {
        internal readonly RespireConnectionMultiplexer Owner = owner;
        internal RespireConnection? Connection;
        internal readonly HashSet<RespireChannel> Confirmed = [];
        internal RespireChannel? ExpectedUnsubscribe;
    }

    private bool IsClusterSharded(SubscriptionKind kind)
        => kind == SubscriptionKind.Sharded && core.Cluster is not null;

    private async ValueTask ActivateShardedAsync(RespireSubscription subscription, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        try { await ActivateShardedCoreAsync(subscription, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(error, cancellationToken, linked.Token))
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
    }

    private async ValueTask ActivateShardedCoreAsync(RespireSubscription subscription, CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_shardedExhaustion is { } exhausted) throw exhausted;
                if (core.Options.ReconnectPolicy is not null && _shardedRecovery is { Task.IsCompleted: false })
                    throw new RespireConnectionException("Sharded pub/sub recovery is in progress. Subscribe again after recovery completes.");
                if (!_observingClusterTopology)
                {
                    core.Cluster!.TopologyChanged += RequestShardedRecovery;
                    _observingClusterTopology = true;
                }
                foreach (var name in subscription.Names)
                {
                    if (!Routes(SubscriptionKind.Sharded).TryGetValue(name, out var targets))
                        Routes(SubscriptionKind.Sharded).Add(name, targets = []);
                    targets.Add(subscription);
                }
            }
            foreach (var name in subscription.Names)
                await EnsureShardedRouteAsync(name, cancellationToken, instrument: true).ConfigureAwait(false);
            await CloseUnusedPrimariesAsync().ConfigureAwait(false);
        }
        catch
        {
            subscription.Buffer.Complete();
            // A failed activation can have accepted a control command before cancellation.
            // Close its sockets instead of waiting for a second command on a stalled stream.
            var released = RemoveRoutes(subscription);
            HashSet<PrimarySubscriptionConnection> uncertain = [];
            lock (_gate)
            {
                foreach (var (_, name) in released)
                {
                    if (_shardedOwners.TryGetValue(name, out var owner)) uncertain.Add(owner);
                    _shardedOwners.Remove(name);
                }
            }
            foreach (var primary in uncertain) await ClosePrimaryAsync(primary).ConfigureAwait(false);
            throw;
        }
        finally { _controlGate.Release(); }
    }

    // Caller owns _controlGate. Connections and acknowledgement state are published under
    // _gate so pushes, topology callbacks and disposal can safely race control commands.
    private async ValueTask EnsureShardedRouteAsync(RespireChannel name, CancellationToken cancellationToken, bool instrument)
    {
        var slot = ClusterHash.GetSlot(name.Span);
        var commandConnection = await core.Cluster!.GetConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        for (var redirect = 0; ; redirect++)
        {
            var primary = await GetPrimaryConnectionAsync(commandConnection.Multiplexer!, cancellationToken).ConfigureAwait(false);
            PrimarySubscriptionConnection? previous;
            lock (_gate)
            {
                _shardedOwners.TryGetValue(name, out previous);
                if (ReferenceEquals(previous, primary) && primary.Confirmed.Contains(name)) return;
                if (previous is not null && !ReferenceEquals(previous, primary))
                {
                    if (previous.Confirmed.Contains(name)) MarkShardedInterruptedLocked(name);
                    _shardedOwners.Remove(name);
                }
            }
            if (previous is not null && !ReferenceEquals(previous, primary))
                await UnsubscribePrimaryAsync(previous, name, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _shardedOwners.Remove(name);
                _shardedOwners.Add(name, primary);
            }
            try
            {
                await SendControlAsync(primary.Connection!, SubscribeVerb(SubscriptionKind.Sharded), "SSUBSCRIBE",
                    name, cancellationToken, instrument).ConfigureAwait(false);
                lock (_gate)
                {
                    // SUNSUBSCRIBE may follow the acknowledgement in the same socket read.
                    if (!primary.Confirmed.Contains(name))
                        throw new RespireConnectionException("Sharded subscription was removed before activation completed.");
                }
                return;
            }
            catch (RespireServerException error) when (error.Code == RespireErrorCodes.Moved && redirect < ClusterRouter.RedirectLimit)
            {
                commandConnection = await core.Cluster.GetRedirectConnectionAsync(error, primary.Connection!, cancellationToken, slot)
                    .ConfigureAwait(false);
            }
            catch
            {
                await ClosePrimaryAsync(primary).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask<PrimarySubscriptionConnection> GetPrimaryConnectionAsync(
        RespireConnectionMultiplexer owner, CancellationToken cancellationToken)
    {
        PrimarySubscriptionConnection? previous;
        lock (_gate)
        {
            _primaryConnections.TryGetValue(owner, out previous);
            if (previous?.Connection is { IsConnected: true }) return previous;
        }
        if (previous is not null) await ClosePrimaryAsync(previous).ConfigureAwait(false);
        var primary = new PrimarySubscriptionConnection(owner);
        var options = core.Options.ToConnectionOptions((in RespValue value) => OnPrimaryPush(primary, in value)) with
        {
            SubscriptionConfirmationHandler = (in RespValue value) => OnSubscriptionConfirmation(0, in value, primary),
            SubscriptionPushFilter = (in RespValue value, bool hasPendingResponse) => FilterPrimaryConfirmation(primary, in value, hasPendingResponse),
        };
        primary.Connection = await RespireConnection.ConnectAsync(owner.Host, owner.Port, options, core.Logger, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate) _primaryConnections.Add(owner, primary);
        _ = WatchPrimaryAsync(primary);
        return primary;
    }

    private void OnPrimaryPush(PrimarySubscriptionConnection primary, in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length >= 3 && elements[0].AsSpan().SequenceEqual("smessage"u8))
            DeliverCore(0, SubscriptionKind.Sharded, elements[1].AsSpan(), elements[1].AsSpan(),
                isPattern: false, elements[2].AsSpan(), primary);
    }

    private bool FilterPrimaryConfirmation(PrimarySubscriptionConnection primary, in RespValue value, bool hasPendingResponse)
    {
        var elements = value.AsArray();
        if (elements.Length < 3 || !elements[0].AsSpan().SequenceEqual("sunsubscribe"u8)) return false;
        lock (_gate)
        {
            // The expectation is installed before sending. A migration push can race ahead
            // of FIFO acceptance; keep the expectation for the eventual command reply.
            if (hasPendingResponse && primary.ExpectedUnsubscribe is { } expected && elements[1].AsSpan().SequenceEqual(expected.Span))
            {
                primary.ExpectedUnsubscribe = null;
                primary.Confirmed.Remove(expected);
                return false;
            }
            if (_shardedOwners.TryGetValue(elements[1].AsSpan(), out var name, out var owner)
                && ReferenceEquals(owner, primary))
            {
                primary.Confirmed.Remove(name);
                MarkShardedInterruptedLocked(name);
                RequestShardedRecoveryLocked(primary);
            }
        }
        return true;
    }

    private void MarkShardedInterruptedLocked(RespireChannel name)
    {
        if (_disposed || !Routes(SubscriptionKind.Sharded).TryGetValue(name, out var subscriptions)) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in subscriptions)
        {
            if (!_interrupted.TryGetValue(subscription, out var targets)) _interrupted.Add(subscription, targets = []);
            targets.TryAdd(name, now);
        }
    }

    private async Task WatchPrimaryAsync(PrimarySubscriptionConnection primary)
    {
        await primary.Connection!.Closed.ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            var affected = false;
            foreach (var name in _shardedOwners.Names)
            {
                if (_shardedOwners.TryGetValue(name, out var owner) && ReferenceEquals(owner, primary))
                {
                    MarkShardedInterruptedLocked(name);
                    affected = true;
                }
            }
            primary.Confirmed.Clear();
            if (affected) RequestShardedRecoveryLocked(primary);
        }
    }

    private async ValueTask UnsubscribePrimaryAsync(PrimarySubscriptionConnection primary, RespireChannel name, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (primary.Connection is not { IsConnected: true } || !primary.Confirmed.Contains(name)) return;
            primary.ExpectedUnsubscribe = name;
        }
        try
        {
            await SendControlAsync(primary.Connection!, UnsubscribeVerb(SubscriptionKind.Sharded), "SUNSUBSCRIBE", name,
                cancellationToken, instrument: true).ConfigureAwait(false);
        }
        catch (Exception error) when (error is RespireException or OperationCanceledException)
        {
            await ClosePrimaryAsync(primary).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) primary.ExpectedUnsubscribe = null;
        }
    }

    private async ValueTask ReleaseShardedRoutesAsync(List<(SubscriptionKind Kind, RespireChannel Name)> released)
    {
        foreach (var (_, name) in released)
        {
            PrimarySubscriptionConnection? primary;
            lock (_gate)
            {
                _shardedOwners.TryGetValue(name, out primary);
                _shardedOwners.Remove(name);
            }
            if (primary is not null && !_disposed)
                await UnsubscribePrimaryAsync(primary, name, _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        await CloseUnusedPrimariesAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseUnusedPrimariesAsync()
    {
        PrimarySubscriptionConnection[] unused;
        lock (_gate)
        {
            var used = _shardedOwners.Values.ToHashSet();
            unused = _primaryConnections.Values.Where(primary => !used.Contains(primary)).ToArray();
        }
        foreach (var primary in unused) await ClosePrimaryAsync(primary).ConfigureAwait(false);
    }

    private async ValueTask ClosePrimaryAsync(PrimarySubscriptionConnection primary)
    {
        lock (_gate)
        {
            if (_primaryConnections.TryGetValue(primary.Owner, out var current) && ReferenceEquals(current, primary))
                _primaryConnections.Remove(primary.Owner);
            foreach (var name in primary.Confirmed)
                if (_shardedOwners.TryGetValue(name, out var owner) && ReferenceEquals(owner, primary))
                    MarkShardedInterruptedLocked(name);
            primary.Confirmed.Clear();
        }
        if (primary.Connection is { } connection)
            await ObserveAbandonedConnectionAsync(connection.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    private void InterruptPrimaryConnections(List<Task> disposals)
    {
        PrimarySubscriptionConnection[] primaries;
        lock (_gate)
        {
            primaries = _primaryConnections.Values.ToArray();
            _primaryConnections.Clear();
            _shardedOwners.Clear();
            foreach (var primary in primaries) primary.Confirmed.Clear();
        }
        foreach (var primary in primaries)
            if (primary.Connection is { } connection) disposals.Add(connection.DisposeAsync().AsTask());
    }

    private void RequestShardedRecovery()
    {
        lock (_gate)
            if (_shardedOwners.Names.Any()) RequestShardedRecoveryLocked();
    }

    private void RequestShardedRecoveryLocked(PrimarySubscriptionConnection? primary = null)
    {
        if (_disposed || _shardedExhaustion is not null || !Routes(SubscriptionKind.Sharded).Names.Any()) return;
        _shardedRecoveryRequested = true;
        if (_shardedRecovery is { Task.IsCompleted: false }) return;
        var drained = _shardedRecovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Reserve the task before starting work: synchronous event-handler disposal can join
        // recovery without waiting for the dispatcher that invoked that handler.
        var endpoint = primary is null ? core.Options.PrimaryEndpoint : new RespireEndpoint(primary.Owner.Host, primary.Owner.Port);
        ThreadPool.UnsafeQueueUserWorkItem(static state => _ = state.Hub.RecoverShardedAsync(state.Drained, state.Endpoint),
            (Hub: this, Drained: drained, Endpoint: endpoint), preferLocal: false);
    }

    private async Task RecoverShardedAsync(TaskCompletionSource drained, RespireEndpoint endpoint)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        var policy = core.Options.ReconnectPolicy;
        var attempt = 0;
        Exception? failure = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt < int.MaxValue) attempt++;
                var delay = policy?.GetDelay(attempt) ?? (attempt == 1 ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, Math.Min(attempt - 2, 5)), 5000)));
                QueueConfiguredState(endpoint, RespireConnectionState.Reconnecting, failure, attempt, delay, clusterSharded: true);
                await Task.Delay(delay, _recoveryClock, cancellationToken).ConfigureAwait(false);
                await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    RespireChannel[] names;
                    lock (_gate)
                    {
                        _shardedRecoveryRequested = false;
                        names = Routes(SubscriptionKind.Sharded).Names.ToArray();
                    }
                    failure = null;
                    foreach (var name in names)
                    {
                        try { await EnsureShardedRouteAsync(name, cancellationToken, instrument: false).ConfigureAwait(false); }
                        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                        {
                            failure ??= error;
                            lock (_gate)
                                if (_shardedOwners.TryGetValue(name, out var primary))
                                    endpoint = new RespireEndpoint(primary.Owner.Host, primary.Owner.Port);
                        }
                    }
                    await CloseUnusedPrimariesAsync().ConfigureAwait(false);
                    if (failure is not null && policy?.IsExhausted(attempt) == true)
                    {
                        ExhaustShardedRecovery(endpoint, failure, attempt);
                        await CloseUnusedPrimariesAsync().ConfigureAwait(false);
                        return;
                    }
                    lock (_reconnectStateGate)
                    {
                        lock (_gate)
                        {
                            if (failure is null && !_shardedRecoveryRequested)
                            {
                                QueueConfiguredState(endpoint, RespireConnectionState.Connected, null, attempt, clusterSharded: true);
                                drained.TrySetResult();
                                return;
                            }
                        }
                    }
                    if (failure is null) attempt = 0;
                    else core.Logger?.LogWarning(failure, "Sharded pub/sub recovery attempt {Attempt} failed", attempt);
                }
                finally { _controlGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        finally { lock (_gate) drained.TrySetResult(); }
    }

    private void ExhaustShardedRecovery(RespireEndpoint endpoint, Exception failure, int attempt)
    {
        lock (_reconnectStateGate)
        {
            if (_disposed) return;
            lock (_gate)
            {
                _shardedExhaustion = new RespireReconnectLimitException(
                    $"Sharded pub/sub recovery exhausted {attempt} attempts. Recreate the client to subscribe again.");
                foreach (var subscription in Routes(SubscriptionKind.Sharded).Values.SelectMany(static list => list).Distinct())
                {
                    _interrupted.Remove(subscription);
                    subscription.CompleteFromReconnectExhaustion();
                }
                Routes(SubscriptionKind.Sharded).Clear();
                _shardedOwners.Clear();
            }
            QueueConfiguredState(endpoint, RespireConnectionState.Disconnected, failure, attempt, exhausted: true, clusterSharded: true);
        }
    }
}
