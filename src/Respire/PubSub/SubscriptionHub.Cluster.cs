using Microsoft.Extensions.Logging;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal sealed partial class SubscriptionHub
{
    // Ordinary recovery acquires _controlGate; sharded recovery acquires _shardedControlGate.
    // Both then acquire _reconnectStateGate, then _gate.
    // Receive/topology callbacks hold only _gate and schedule work without acquiring the others.
    // Identity is the router's generation, not merely host:port. A replacement at the same
    // endpoint must not inherit the retired primary's subscription transport.
    private readonly Dictionary<RespireConnectionMultiplexer, PrimarySubscriptionConnection> _primaryConnections = [];
    private readonly HashSet<PrimarySubscriptionConnection> _askConnections = [];
    private readonly ByteRouteDictionary<PrimarySubscriptionConnection> _shardedOwners = new();
    private readonly HashSet<RespireEndpoint> _shardedRecoveryEndpoints = [];
    private TaskCompletionSource? _shardedRecovery;
    private bool _shardedRecoveryRequested;
    private bool _observingClusterTopology;
    private RespireReconnectLimitException? _shardedExhaustion;

    private sealed class PrimarySubscriptionConnection(RespireConnectionMultiplexer owner, RespireConnectionMultiplexer? askSource = null)
    {
        internal readonly RespireConnectionMultiplexer Owner = owner;
        internal readonly bool IsAskConnection = askSource is not null;
        // While a slot migrates, the router intentionally keeps the source as the slot owner.
        // Cleared under _gate once the router observes this target as the owner.
        internal RespireConnectionMultiplexer? AskSource = askSource;
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
        cancellationToken.ThrowIfCancellationRequested();
        // Recovery can hold the sharded control gate across an unanswered SSUBSCRIBE. New callers
        // must fail before joining that queue; recheck after acquisition for a racing episode.
        lock (_gate) ThrowIfShardedAdmissionUnavailableLocked();
        await _shardedControlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ThrowIfShardedAdmissionUnavailableLocked();
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
                await EnsureShardedRouteAsync(name, cancellationToken, recovering: false).ConfigureAwait(false);
            await CloseUnusedPrimariesAsync().ConfigureAwait(false);
        }
        catch (RespireServerException)
        {
            // A completed server rejection leaves FIFO state known. Undo accepted routes
            // without interrupting other subscriptions sharing their primary connection.
            subscription.Buffer.Complete();
            await ReleaseShardedRoutesAsync(RemoveRoutes(subscription)).ConfigureAwait(false);
            throw;
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
        finally { _shardedControlGate.Release(); }
    }

    private void ThrowIfShardedAdmissionUnavailableLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_shardedExhaustion is { } exhausted) throw exhausted;
        // Both configured and default recovery own the routes until their episode ends.
        if (_shardedRecovery is { Task.IsCompleted: false })
            throw new RespireConnectionException("Sharded pub/sub recovery is in progress. Subscribe again after recovery completes.");
    }

    // Caller owns _shardedControlGate. Connections and acknowledgement state are published under
    // _gate so pushes, topology callbacks and disposal can safely race control commands.
    private async ValueTask EnsureShardedRouteAsync(RespireChannel name, CancellationToken cancellationToken, bool recovering)
    {
        var slot = ClusterHash.GetSlot(name.Span);
        var commandConnection = await core.Cluster!.GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        lock (_gate)
        {
            // A confirmed ASK route stays valid while the router still names its migration
            // source (or, after migration, its target) as owner. Keep it instead of repeating
            // ASK on every recovery pass or duplicate subscription for the same channel.
            if (_shardedOwners.TryGetValue(name, out var existing) && existing.IsAskConnection
                && existing.Confirmed.Contains(name) && existing.Connection is { IsConnected: true }
                && IsRouteOwnerCurrentLocked(existing, commandConnection.Multiplexer)) return;
        }
        RespireConnectionMultiplexer? askSource = null;
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                var primary = askSource is not null
                    ? await CreatePrimaryConnectionAsync(commandConnection.Multiplexer!, askSource, cancellationToken).ConfigureAwait(false)
                    : await GetPrimaryConnectionAsync(commandConnection.Multiplexer!, cancellationToken).ConfigureAwait(false);
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
                        name, cancellationToken, instrument: !recovering, primary.IsAskConnection).ConfigureAwait(false);
                    lock (_gate)
                    {
                        // SUNSUBSCRIBE may follow the acknowledgement in the same socket read.
                        if (!primary.Confirmed.Contains(name))
                            throw new RespireConnectionException("Sharded subscription was removed before activation completed.");
                    }
                    return;
                }
                catch (RespireServerException error) when ((error.Code is RespireErrorCodes.Moved or RespireErrorCodes.Ask)
                    && redirect < ClusterRouter.RedirectLimit)
                {
                    commandConnection = await core.Cluster.GetRedirectConnectionAsync(error, primary.Connection!, cancellationToken, slot, discovery: null)
                        .ConfigureAwait(false);
                    if (primary.IsAskConnection)
                        await ClosePrimaryAsync(primary).ConfigureAwait(false);
                    askSource = error.Code == RespireErrorCodes.Ask ? primary.AskSource ?? primary.Owner : null;
                }
                catch (RespireServerException)
                {
                    if (primary.IsAskConnection)
                        await ClosePrimaryAsync(primary).ConfigureAwait(false);
                    lock (_gate)
                        if (_shardedOwners.TryGetValue(name, out var owner) && ReferenceEquals(owner, primary)
                            && !primary.Confirmed.Contains(name)) _shardedOwners.Remove(name);
                    throw;
                }
                catch
                {
                    // SendControlAsync may have written before cancellation or an observer
                    // failed. Only the completed server-error path above proves FIFO state.
                    await ClosePrimaryAsync(primary).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch
        {
            if (recovering)
                lock (_gate) _shardedRecoveryEndpoints.Add(new(commandConnection.Host, commandConnection.Port));
            throw;
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
        return await CreatePrimaryConnectionAsync(owner, askSource: null, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PrimarySubscriptionConnection> CreatePrimaryConnectionAsync(
        RespireConnectionMultiplexer owner, RespireConnectionMultiplexer? askSource, CancellationToken cancellationToken)
    {
        var primary = new PrimarySubscriptionConnection(owner, askSource);
        var options = core.Options.ToConnectionOptions((in RespValue value) => OnPrimaryPush(primary, in value));
        options = options with
        {
            SubscriptionConfirmationHandler = (in RespValue value) => OnSubscriptionConfirmation(0, in value, primary),
            SubscriptionPushFilter = (in RespValue value, bool hasPendingResponse) => FilterPrimaryConfirmation(primary, in value, hasPendingResponse),
            // ASKING and SSUBSCRIBE occupy two FIFO slots on this dedicated connection, even
            // when the client allows only one in-flight command on its shared connections.
            MaxInflightCommands = primary.IsAskConnection ? Math.Max(2, options.MaxInflightCommands) : options.MaxInflightCommands,
        };
        primary.Connection = await RespireConnection.ConnectAsync(owner.Host, owner.Port, options, core.Logger, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (primary.IsAskConnection) _askConnections.Add(primary);
            else _primaryConnections.Add(owner, primary);
        }
        _ = WatchPrimaryAsync(primary);
        return primary;
    }

    private void OnPrimaryPush(PrimarySubscriptionConnection primary, in RespValue value)
    {
        var elements = value.AsArray();
        if (elements.Length >= 3 && elements[0].AsSpan().SequenceEqual("smessage"u8))
            DeliverPrimary(primary, elements[1].AsSpan(), elements[2].AsSpan());
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
                // Redis does not distinguish a same-channel migration confirmation from
                // our reply. Either confirms removal; consume one FIFO slot, then filter
                // any duplicate so it cannot complete the next control command.
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

    // Keep generation validation on the sharded receive path. The existing regular Deliver
    // method retains its epoch-only path and signature, avoiding generation branches on
    // regular dispatch. Both paths enqueue under _gate and notify drops outside it.
    private void DeliverPrimary(PrimarySubscriptionConnection primary, ReadOnlySpan<byte> channel, ReadOnlySpan<byte> payload)
    {
        List<(RespireSubscription Subscription, RespireSubscriptionGap Gap)>? drops = null;
        lock (_gate)
        {
            if (_disposed || !Routes(SubscriptionKind.Sharded).TryGetValue(channel, out var name, out var targets)
                || !_shardedOwners.TryGetValue(name, out var owner) || !ReferenceEquals(owner, primary)
                || !primary.Confirmed.Contains(name)) return;
            var message = new RespireMessage(name, null, payload.ToArray(), core.Options.Serializer);
            foreach (var target in targets)
                if (target.Buffer.Write(message) is { } gap) (drops ??= []).Add((target, gap));
        }
        if (drops is not null)
            foreach (var (subscription, gap) in drops) subscription.NotifyDrop(gap);
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
        try { await primary.Connection!.Closed.ConfigureAwait(false); }
        catch (Exception error)
        {
            // A faulted receive loop still requires recovery. Observe its failure even when
            // a user logger throws, so the detached watcher cannot fault without a consumer.
            try { core.Logger?.LogWarning(error, "Sharded subscription connection closed with a receive failure"); }
            catch { /* A user logger must not prevent recovery after a receive failure. */ }
        }
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
            try
            {
                core.Logger?.LogDebug(error,
                    "Closing sharded subscription connection {Host}:{Port} after SUNSUBSCRIBE failed",
                    primary.Owner.Host, primary.Owner.Port);
            }
            catch { /* A user logger must not prevent uncertain socket cleanup. */ }
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
            unused = _primaryConnections.Values.Concat(_askConnections).Where(primary => !used.Contains(primary)).ToArray();
        }
        foreach (var primary in unused) await ClosePrimaryAsync(primary).ConfigureAwait(false);
    }

    private async ValueTask ClosePrimaryAsync(PrimarySubscriptionConnection primary)
    {
        lock (_gate)
        {
            if (_primaryConnections.TryGetValue(primary.Owner, out var current) && ReferenceEquals(current, primary))
                _primaryConnections.Remove(primary.Owner);
            _askConnections.Remove(primary);
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
            primaries = _primaryConnections.Values.Concat(_askConnections).ToArray();
            _primaryConnections.Clear();
            _askConnections.Clear();
            _shardedOwners.Clear();
            foreach (var primary in primaries) primary.Confirmed.Clear();
        }
        foreach (var primary in primaries)
            if (primary.Connection is { } connection) disposals.Add(connection.DisposeAsync().AsTask());
    }

    private void RequestShardedRecovery()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var name in _shardedOwners.Names)
            {
                if (_shardedOwners.TryGetValue(name, out var primary)
                    && primary.Confirmed.Contains(name)
                    && !IsRouteOwnerCurrentLocked(primary, core.Cluster!.GetKnownSlotOwner(ClusterHash.GetSlot(name.Span))))
                {
                    MarkShardedInterruptedLocked(name);
                    RequestShardedRecoveryLocked(primary);
                }
            }
        }
    }

    private void RequestShardedRecovery(long topologyVersion, RespireEndpoint[] endpoints)
        => RequestShardedRecovery();

    // An ASK route intentionally differs from the router's slot owner until migration
    // completes, so its migration source also counts as current. Once the router names the
    // target, forget the source so a later move back to it starts recovery.
    private static bool IsRouteOwnerCurrentLocked(PrimarySubscriptionConnection primary, RespireConnectionMultiplexer? knownOwner)
    {
        if (ReferenceEquals(primary.Owner, knownOwner))
        {
            primary.AskSource = null;
            return true;
        }
        return primary.AskSource is { } source && ReferenceEquals(source, knownOwner);
    }

    private void RequestShardedRecoveryLocked(PrimarySubscriptionConnection primary)
    {
        if (_disposed || _shardedExhaustion is not null || !Routes(SubscriptionKind.Sharded).Names.Any()) return;
        _shardedRecoveryEndpoints.Add(new(primary.Owner.Host, primary.Owner.Port));
        _shardedRecoveryRequested = true;
        if (_shardedRecovery is { Task.IsCompleted: false }) return;
        var drained = _shardedRecovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Reserve the task before starting work: synchronous event-handler disposal can join
        // recovery without waiting for the dispatcher that invoked that handler.
        ThreadPool.UnsafeQueueUserWorkItem(static state => _ = state.Hub.RecoverShardedAsync(state.Drained),
            (Hub: this, Drained: drained), preferLocal: false);
    }

    private async Task RecoverShardedAsync(TaskCompletionSource drained)
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
                RespireEndpoint[] affected;
                lock (_gate) affected = _shardedRecoveryEndpoints.ToArray();
                foreach (var endpoint in affected)
                    QueueConfiguredState(endpoint, RespireConnectionState.Reconnecting, failure, attempt, delay, clusterSharded: true);
                await Task.Delay(delay, _recoveryClock, cancellationToken).ConfigureAwait(false);
                await _shardedControlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                        try { await EnsureShardedRouteAsync(name, cancellationToken, recovering: true).ConfigureAwait(false); }
                        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                        {
                            failure ??= error;
                            lock (_gate)
                                if (_shardedOwners.TryGetValue(name, out var primary))
                                    _shardedRecoveryEndpoints.Add(new(primary.Owner.Host, primary.Owner.Port));
                        }
                    }
                    // An unexpected cleanup failure counts as a failed attempt. Ending the loop
                    // here would leave interrupted routes and unhealthy endpoints unrecovered.
                    try { await CloseUnusedPrimariesAsync().ConfigureAwait(false); }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested) { failure ??= error; }
                    if (failure is not null && policy?.IsExhausted(attempt) == true)
                    {
                        ExhaustShardedRecovery(failure, attempt);
                        await CloseUnusedPrimariesAsync().ConfigureAwait(false);
                        return;
                    }
                    lock (_reconnectStateGate)
                    {
                        lock (_gate)
                        {
                            if (failure is null && !_shardedRecoveryRequested)
                            {
                                // Retain even removed owners until this terminal notification clears
                                // their previously published health state; then discard the episode.
                                foreach (var endpoint in _shardedRecoveryEndpoints)
                                    QueueConfiguredState(endpoint, RespireConnectionState.Connected, null, attempt, clusterSharded: true);
                                _shardedRecoveryEndpoints.Clear();
                                drained.TrySetResult();
                                return;
                            }
                        }
                    }
                    if (failure is null) attempt = 0;
                    else
                    {
                        try { core.Logger?.LogWarning(failure, "Sharded pub/sub recovery attempt {Attempt} failed", attempt); }
                        catch { /* A user logger must not terminate the detached recovery loop. */ }
                    }
                }
                finally { _shardedControlGate.Release(); }
                // Successful passes can be superseded by topology callbacks even when
                // every await completes synchronously. Yield after releasing the control
                // gate before starting another immediate pass; retain the retry policy.
                if (failure is null)
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        finally { lock (_gate) drained.TrySetResult(); }
    }

    private void ExhaustShardedRecovery(Exception failure, int attempt)
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
                foreach (var endpoint in _shardedRecoveryEndpoints)
                    QueueConfiguredState(endpoint, RespireConnectionState.Disconnected, failure, attempt, exhausted: true, clusterSharded: true);
                _shardedRecoveryEndpoints.Clear();
            }
        }
    }
}
