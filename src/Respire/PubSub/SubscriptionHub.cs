using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Owns a client's dedicated pub/sub connections (created on first subscription) and
/// routes incoming messages to subscription buffers. If the connection dies, reconnects with
/// backoff and resubscribes everything that is still subscribed. Ordered markers report delivery gaps.
/// </summary>
internal sealed partial class SubscriptionHub(ClientCore core, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider _recoveryClock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan DisposeConnectionPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly object _gate = new();
    private readonly object _reconnectStateGate = new();
    private readonly Queue<(RespireConnectionStateChange Change, bool ClusterSharded)> _pendingReconnectStates = [];
    private readonly ByteRouteDictionary<List<RespireSubscription>>[] _routes =
        [new(), new(), new()];
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly SemaphoreSlim _shardedControlGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private RespireConnection? _connection;
    private long _reconnectGeneration;
    private long _connectionEpoch;
    private readonly Dictionary<RespireSubscription, Dictionary<RespireChannel, DateTimeOffset>> _interrupted = [];
    private bool _publishingReconnectState;
    private volatile bool _disposed;

    private RespireSubscription CreateSubscription(
        SubscriptionKind kind, RespireChannel[] names, RespireSubscriptionOptions options)
    {
        if (core.Options.CredentialProvider is not null && core.Options.Protocol != RespProtocol.Resp3)
            throw new RespireConfigurationException("Renewable Pub/Sub credentials require Protocol = RespProtocol.Resp3; Redis forbids AUTH while subscribed in RESP2.");
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            if (!name.IsNotification || core.Cluster is null) continue;
            if (name.NotificationDatabase is not null and not 0)
                throw new ArgumentException("Redis Cluster notifications support only database 0.", nameof(names));
            throw new NotSupportedException("Notification routing across Redis Cluster primaries is not supported yet.");
        }

        if (names.Length == 0)
        {
            throw new ArgumentException("At least one channel is required.", nameof(names));
        }

        // Defensive copy (unsubscription must look up the names that were registered, not
        // whatever the caller later wrote into their array), deduplicated so one published
        // message is never delivered twice through duplicate route entries.
        var (bufferSize, overflow) = options.Resolve(core.Options);
        return new RespireSubscription(
            this,
            kind,
            [.. names.Distinct()],
            bufferSize,
            overflow);
    }

    /// <summary>
    /// Creates a subscription and activates it before handing it back, so it is already live
    /// server-side when the caller sees it — enumeration only drains the buffer.
    /// </summary>
    public async ValueTask<RespireSubscription> SubscribeAsync(
        SubscriptionKind kind,
        RespireChannel[] names,
        RespireSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        var subscription = CreateSubscription(kind, names, options);
        if (IsClusterSharded(kind))
            await ActivateShardedAsync(subscription, cancellationToken).ConfigureAwait(false);
        else
            await ActivateAsync(subscription, cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    /// <summary>
    /// Registers the subscription's routes and sends SUBSCRIBE. Failures leave the cleanup to the
    /// caller's <see cref="RespireSubscription.DisposeAsync"/>, which unsubscribes every route it
    /// takes back out.
    /// </summary>
    private async ValueTask ActivateAsync(RespireSubscription subscription, CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RespireConnection? connection = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                // Re-checked under the routing gate: disposal snapshots and completes routed
                // subscriptions under this same lock, so an activation that loses the race must
                // not register routes (and subscribe server-side) on a disposed hub.
                ObjectDisposedException.ThrowIf(_disposed, this);
                var routes = Routes(subscription.Kind);
                foreach (var name in subscription.Names)
                {
                    if (!routes.TryGetValue(name, out var list))
                    {
                        list = [];
                        // Equal byte routes may mix ordinary names and descriptors. Cache a
                        // wire name so registration order cannot leak one caller's metadata.
                        routes.Add(name.WithoutNotificationMetadata(), list);
                    }

                    list.Add(subscription);
                }
            }

            foreach (var name in subscription.Names)
            {
                await SendControlAsync(
                        connection, SubscribeVerb(subscription.Kind), SubscribeOperation(subscription.Kind), name,
                        cancellationToken, instrument: true)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            subscription.Buffer.Complete();
            RemoveRoutes(subscription);
            if (connection is not null)
            {
                // A cancelled command may already be on the wire. Closing the connection clears
                // that uncertain server-side subscription immediately and does not await another
                // response from the same stalled stream. Existing routes reconnect normally.
                AbandonConnection(connection);
            }

            throw;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>Unregisters the subscription, unsubscribing channels it was the last consumer of.</summary>
    public async ValueTask RemoveAsync(RespireSubscription subscription)
    {
        subscription.Buffer.Complete();
        var controlGate = IsClusterSharded(subscription.Kind) ? _shardedControlGate : _controlGate;
        await controlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseRoutesAsync(subscription).ConfigureAwait(false);
        }
        finally
        {
            controlGate.Release();
        }
    }

    /// <summary>Removes the subscription's routes and unsubscribes channels left without consumers.</summary>
    private async ValueTask ReleaseRoutesAsync(RespireSubscription subscription)
    {
        var releasedRoutes = RemoveRoutes(subscription);
        if (IsClusterSharded(subscription.Kind))
        {
            await ReleaseShardedRoutesAsync(releasedRoutes).ConfigureAwait(false);
            return;
        }
        var connection = _connection;
        if (_disposed || connection is not { IsConnected: true })
        {
            return;
        }

        foreach (var (kind, name) in releasedRoutes)
        {
            try
            {
                await SendControlAsync(
                        connection, UnsubscribeVerb(kind), UnsubscribeOperation(kind), name,
                        CancellationToken.None, instrument: true)
                    .ConfigureAwait(false);
            }
            catch (RespireException)
            {
                // The connection died mid-unsubscribe; the server forgets the subscription anyway.
                break;
            }
        }
    }

    private List<(SubscriptionKind Kind, RespireChannel Name)> RemoveRoutes(RespireSubscription subscription)
    {
        var releasedRoutes = new List<(SubscriptionKind Kind, RespireChannel Name)>();
        lock (_gate)
        {
            _interrupted.Remove(subscription);
            var routes = Routes(subscription.Kind);
            foreach (var name in subscription.Names)
            {
                if (routes.TryGetValue(name, out var list))
                {
                    list.Remove(subscription);
                    if (list.Count == 0)
                    {
                        routes.Remove(name);
                    }
                }

                // Released = no consumer remains, regardless of who removed the route. A failed
                // activation can leave a name subscribed server-side but never routed; deriving
                // the unsubscribe list from "route absent" (rather than "this call removed the
                // last entry") covers that too. A redundant UNSUBSCRIBE is harmless; a missing
                // one leaks a server-side subscription.
                if (!routes.ContainsKey(name))
                {
                    releasedRoutes.Add((subscription.Kind, name));
                }
            }
        }

        return releasedRoutes;
    }

    private void AbandonConnection(RespireConnection connection)
    {
        if (DetachConnection(connection) is not { } disposal)
        {
            return;
        }

        if (disposal.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveAbandonedConnectionAsync(disposal);
    }

    private Task? DetachConnection(RespireConnection connection)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_connection, connection)) return null;
            MarkInterruptedLocked();
            _connection = null;
            ++_connectionEpoch;
        }
        return connection.DisposeAsync().AsTask();
    }

    private void MarkInterruptedLocked()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        HashSet<RespireSubscription> affected = [];
        for (var i = 0; i < _routes.Length; i++)
        {
            if (IsClusterSharded((SubscriptionKind)i)) continue;
            foreach (var subscriptions in _routes[i].Values) affected.UnionWith(subscriptions);
        }
        foreach (var subscription in affected)
        {
            if (!_interrupted.TryGetValue(subscription, out var targets))
            {
                _interrupted.Add(subscription, targets = []);
            }
            foreach (var name in subscription.Names) targets.TryAdd(name, now);
        }
    }

    internal void LogGapHandlerFailure(Exception error)
        => core.Logger?.LogWarning(error, "Subscription delivery-gap handler threw");

    private async Task ObserveAbandonedConnectionAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { core.Logger?.LogDebug(ex, "Closing a failed subscription connection failed"); }
            catch { /* Cleanup remains observed even when a user logger throws. */ }
        }
    }

    private void InterruptPublishedConnection(List<Task> interruptedDisposals)
    {
        InterruptPrimaryConnections(interruptedDisposals);
        var connection = Volatile.Read(ref _connection);
        if (connection is not null && DetachConnection(connection) is { } disposal)
        {
            interruptedDisposals.Add(disposal);
        }
    }

    private static Verb SubscribeVerb(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => Verbs.PSubscribe,
        SubscriptionKind.Sharded => Verbs.SSubscribe,
        _ => Verbs.Subscribe,
    };

    private static Verb UnsubscribeVerb(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => Verbs.PUnsubscribe,
        SubscriptionKind.Sharded => Verbs.SUnsubscribe,
        _ => Verbs.Unsubscribe,
    };

    private static string SubscribeOperation(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => "PSUBSCRIBE",
        SubscriptionKind.Sharded => "SSUBSCRIBE",
        _ => "SUBSCRIBE",
    };

    private static string UnsubscribeOperation(SubscriptionKind kind) => kind switch
    {
        SubscriptionKind.Pattern => "PUNSUBSCRIBE",
        SubscriptionKind.Sharded => "SUNSUBSCRIBE",
        _ => "UNSUBSCRIBE",
    };

    private async ValueTask SendControlAsync(
        RespireConnection connection,
        Verb verb,
        string operation,
        RespireChannel name,
        CancellationToken cancellationToken,
        bool instrument,
        bool ask = false)
    {
        var telemetry = instrument
            ? RespireTelemetry.StartOperation(
                operation, connection.Host, connection.Port, core.Options.Database)
            : default;
        try
        {
            var command = new Cmd1(verb, name.AsValue());
            var reply = ask
                ? await ClusterRouter.SendAskingAsync(connection, in command, cancellationToken, operation).ConfigureAwait(false)
                : await connection.SendAsync(command, cancellationToken).ConfigureAwait(false);
            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply, operation);
                reply.Dispose();
                throw error;
            }

            reply.Dispose();
            telemetry.Complete(core, operation, connection: connection);
        }
        catch (Exception ex)
        {
            telemetry.Complete(core, operation, error: ex, connection: connection);
            throw;
        }
    }

    private async ValueTask<RespireConnection> EnsureConnectionAsync(CancellationToken cancellationToken, bool watch = true)
    {
        if (GetConnectionForCaller(watch) is { } existing)
        {
            return existing;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (GetConnectionForCaller(watch) is { } raced)
            {
                return raced;
            }

            RespireConnection? previous;
            long epoch;
            lock (_gate)
            {
                previous = _connection;
                if (previous is not null) MarkInterruptedLocked();
                epoch = ++_connectionEpoch;
            }
            RespireEndpoint endpoint;
            SentinelRouter.Generation? sentinelGeneration = null;
            if (core.Cluster is { } cluster)
            {
                endpoint = await cluster.GetPubSubEndpointAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (core.Sentinel is { } sentinel)
            {
                sentinelGeneration = await sentinel.GetGenerationAsync(cancellationToken).ConfigureAwait(false);
                endpoint = sentinelGeneration.Endpoint;
            }
            else
            {
                endpoint = core.Options.PrimaryEndpoint;
            }

            var options = core.Options.ToConnectionOptions((in RespValue value) => OnPush(epoch, in value)) with
            {
                SubscriptionConfirmationHandler = (in RespValue value) => OnSubscriptionConfirmation(epoch, in value),
                Generation = sentinelGeneration,
            };
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCancellation.Token);
            RespireConnection connection;
            try
            {
                connection = await RespireConnection.ConnectAsync(
                    endpoint.Host, endpoint.Port, options, core.Logger, connectCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, connectCancellation.Token))
            {
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
            lock (_gate)
            {
                _connection = connection;
            }
            if (watch)
            {
                if (core.Options.ReconnectPolicy is not null)
                    lock (_reconnectStateGate) _configuredConnection = connection;
                _ = WatchConnectionAsync(connection);
            }
            if (previous is not null)
            {
                // The replacement is already published. Prior cleanup must not invalidate it
                // or consume a configured recovery attempt before routes are restored.
                await ObserveAbandonedConnectionAsync(previous.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            return connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Reconnect-and-resubscribe loop, armed once per connection.</summary>
    private async Task WatchConnectionAsync(RespireConnection connection)
    {
        await connection.Closed.ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection)) MarkInterruptedLocked();
        }

        if (core.Options.ReconnectPolicy is { } policy)
        {
            StartConfiguredRecovery(connection, policy);
            return;
        }

        long reconnectGeneration;
        bool publishState;
        lock (_reconnectStateGate)
        {
            if (_disposed)
            {
                return;
            }

            reconnectGeneration = ++_reconnectGeneration;
            publishState = QueueReconnectStateLocked(new RespireConnectionStateChange(
                new RespireEndpoint(connection.Host, connection.Port),
                RespireConnectionState.Reconnecting,
                connection.CloseError));
        }

        if (publishState)
        {
            PublishReconnectStates();
        }

        var delay = TimeSpan.FromMilliseconds(250);
        while (!_disposed)
        {
            try
            {
                await _controlGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var replacement = await EnsureConnectionAsync(CancellationToken.None).ConfigureAwait(false);

                    (SubscriptionKind Kind, RespireChannel Name)[] routes;
                    lock (_gate)
                    {
                        var snapshot = new List<(SubscriptionKind Kind, RespireChannel Name)>();
                        for (var i = 0; i < _routes.Length; i++)
                        {
                            if (IsClusterSharded((SubscriptionKind)i)) continue;
                            foreach (var name in _routes[i].Names)
                            {
                                snapshot.Add(((SubscriptionKind)i, name));
                            }
                        }

                        routes = [.. snapshot];
                    }

                    foreach (var (kind, name) in routes)
                    {
                        await SendControlAsync(
                                replacement, SubscribeVerb(kind), SubscribeOperation(kind), name,
                                CancellationToken.None, instrument: false)
                            .ConfigureAwait(false);
                    }

                    // A replacement can itself fail while this watcher is resubscribing. Its watcher
                    // then owns the newer reconnect generation; this stale watcher must not announce
                    // Connected after that newer Reconnecting notification.
                    publishState = false;
                    lock (_reconnectStateGate)
                    {
                        if (reconnectGeneration == _reconnectGeneration
                            && ReferenceEquals(Volatile.Read(ref _connection), replacement)
                            && replacement.IsConnected)
                        {
                            publishState = QueueReconnectStateLocked(new RespireConnectionStateChange(
                                new RespireEndpoint(replacement.Host, replacement.Port),
                                RespireConnectionState.Connected,
                                null));
                        }
                    }
                }
                finally
                {
                    _controlGate.Release();
                }

                if (publishState)
                {
                    PublishReconnectStates();
                }

                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                core.Logger?.LogWarning(ex, "Pub/sub reconnect failed; retrying in {Delay}", delay);
                await Task.Delay(delay).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
    }

    private bool QueueReconnectStateLocked(RespireConnectionStateChange change, bool clusterSharded = false)
    {
        _pendingReconnectStates.Enqueue((change with
        {
            ReconnectSource = RespireReconnectSource.PubSub,
            SourceState = change.State,
        }, clusterSharded));
        if (_publishingReconnectState)
        {
            return false;
        }

        _publishingReconnectState = true;
        return true;
    }

    private void PublishReconnectStates()
    {
        while (true)
        {
            (RespireConnectionStateChange Change, bool ClusterSharded) observation;
            lock (_reconnectStateGate)
            {
                if (!_pendingReconnectStates.TryDequeue(out observation))
                {
                    _publishingReconnectState = false;
                    return;
                }
            }

            var change = observation.Change;
            try
            {
                if (change.NextReconnectDelay is { } delay)
                    RespireTelemetry.RecordReconnectAttempt(change.Endpoint.Host, change.Endpoint.Port,
                        change.ReconnectAttempt, delay, RespireReconnectSource.PubSub);
                if (change.ReconnectExhausted)
                    RespireTelemetry.RecordReconnectExhaustion(change.Endpoint.Host, change.Endpoint.Port, RespireReconnectSource.PubSub);
            }
            catch (Exception error)
            {
                core.Logger?.LogWarning(error, "Pub/sub recovery metric observer threw");
            }
            // Measurements describe scheduled work and survive disposal. Lifecycle events
            // still queued when disposal wins must not restore the client's subscription state.
            if (!_disposed && !core.Disposed) core.NotifySubscriptionStateChanged(change, observation.ClusterSharded);
        }
    }

    // Observe acknowledgements before FIFO completion: a message can follow the acknowledgement
    // in the same socket read, before the asynchronous resubscribe continuation runs.
    private void OnSubscriptionConfirmation(long epoch, in RespValue value, PrimarySubscriptionConnection? primary = null)
    {
        var elements = value.AsArray();
        if (elements.Length < 3) return;
        var verb = elements[0].AsSpan();
        SubscriptionKind kind;
        if (verb.SequenceEqual("subscribe"u8)) kind = SubscriptionKind.Channel;
        else if (verb.SequenceEqual("psubscribe"u8)) kind = SubscriptionKind.Pattern;
        else if (verb.SequenceEqual("ssubscribe"u8)) kind = SubscriptionKind.Sharded;
        else return;
        List<(RespireSubscription Subscription, RespireSubscriptionGap Gap)>? gaps = null;
        lock (_gate)
        {
            if (_disposed || (primary is null && epoch != _connectionEpoch)
                || !Routes(kind).TryGetValue(elements[1].AsSpan(), out var name, out var subscriptions)) return;
            if (primary is not null)
            {
                if (!_shardedOwners.TryGetValue(name, out var owner) || !ReferenceEquals(owner, primary)) return;
                primary.Confirmed.Add(name);
            }
            foreach (var subscription in subscriptions)
            {
                if (_interrupted.TryGetValue(subscription, out var targets) && targets.Remove(name, out var started))
                {
                    var ended = DateTimeOffset.UtcNow;
                    if (ended < started) ended = started;
                    var gap = new RespireSubscriptionGap(RespireSubscriptionGapReason.Reconnect, started, ended);
                    if (subscription.Buffer.WriteGap(gap)) (gaps ??= []).Add((subscription, gap));
                    if (targets.Count == 0) _interrupted.Remove(subscription);
                }
            }
        }
        if (gaps is not null)
        {
            foreach (var (subscription, gap) in gaps) subscription.NotifyGap(gap);
        }
    }

    /// <summary>Runs on the connection's receive loop — copy out of the frame, never block.</summary>
    private void OnPush(long epoch, in RespValue value)
    {
        if (epoch != Volatile.Read(ref _connectionEpoch)) return;
        var elements = value.AsArray();
        if (elements.Length < 3)
        {
            return;
        }

        var frameKind = elements[0].AsSpan();
        if (frameKind.SequenceEqual("message"u8))
        {
            Deliver(epoch, SubscriptionKind.Channel, elements[1].AsSpan(), elements[1].AsSpan(),
                isPattern: false, elements[2].AsSpan());
        }
        else if (frameKind.SequenceEqual("smessage"u8))
        {
            Deliver(epoch, SubscriptionKind.Sharded, elements[1].AsSpan(), elements[1].AsSpan(),
                isPattern: false, elements[2].AsSpan());
        }
        else if (frameKind.SequenceEqual("pmessage"u8) && elements.Length >= 4)
        {
            Deliver(epoch, SubscriptionKind.Pattern, elements[1].AsSpan(), elements[2].AsSpan(),
                isPattern: true, elements[3].AsSpan());
        }
    }

    private void Deliver(
        long epoch,
        SubscriptionKind kind,
        ReadOnlySpan<byte> routeName,
        ReadOnlySpan<byte> channel,
        bool isPattern,
        ReadOnlySpan<byte> payload)
    {
        List<(RespireSubscription Subscription, RespireSubscriptionGap Gap)>? drops = null;
        lock (_gate)
        {
            // Validate and enqueue under the same gate that advances epochs and publishes
            // reconnect markers. A route snapshot alone would leave a stale-writer window.
            if (_disposed || epoch != _connectionEpoch
                || !Routes(kind).TryGetValue(routeName, out var cachedRouteName, out var targets)) return;
            var channelName = isPattern ? RespireChannel.FromOwnedBytes(channel.ToArray()) : cachedRouteName;
            var message = new RespireMessage(channelName,
                isPattern ? cachedRouteName : (RespireChannel?)null, payload.ToArray(), core.Options.Serializer);
            foreach (var target in targets)
            {
                if (target.Buffer.Write(message) is { } gap) (drops ??= []).Add((target, gap));
            }
        }
        // User handlers and metric callbacks never run under the routing/buffer gates.
        if (drops is not null)
        {
            foreach (var (subscription, gap) in drops) subscription.NotifyDrop(gap);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_reconnectStateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Leave queued observations for the independent dispatcher to measure and drain.
        }

        lock (_gate)
            if (_observingClusterTopology) core.Cluster!.TopologyChanged -= RequestShardedRecovery;
        _lifetimeCancellation.Cancel();

        // Interrupt stalled control commands while waiting for their serialization gate. A
        // reconnect can publish a replacement after the first snapshot, so keep detaching every
        // connection that appears until the gate is ours.
        List<Task> interruptedDisposals = [];
        InterruptPublishedConnection(interruptedDisposals);
        var controlGateAcquired = false;
        var shardedControlGateAcquired = false;
        while (!controlGateAcquired || !shardedControlGateAcquired)
        {
            if (!controlGateAcquired)
                controlGateAcquired = await _controlGate.WaitAsync(DisposeConnectionPollInterval).ConfigureAwait(false);
            if (!shardedControlGateAcquired)
                shardedControlGateAcquired = await _shardedControlGate.WaitAsync(DisposeConnectionPollInterval).ConfigureAwait(false);
            if (!controlGateAcquired || !shardedControlGateAcquired)
            {
                InterruptPublishedConnection(interruptedDisposals);
            }
        }

        try
        {
            InterruptPublishedConnection(interruptedDisposals);

            List<RespireSubscription> subscriptions = [];
            lock (_gate)
            {
                _interrupted.Clear();
                foreach (var routes in _routes)
                {
                    foreach (var list in routes.Values)
                    {
                        subscriptions.AddRange(list);
                    }

                    routes.Clear();
                }
            }

            foreach (var subscription in subscriptions)
            {
                subscription.CompleteFromClientDisposal();
            }

            // Synchronize with a racing first connect: once the gate is ours, any connection an
            // in-flight EnsureConnectionAsync published is visible here and gets swept instead of
            // leaking an open socket past client disposal.
            await _connectionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                InterruptPublishedConnection(interruptedDisposals);
                await Task.WhenAll(interruptedDisposals).ConfigureAwait(false);
            }
            finally
            {
                _connectionGate.Release();
                _connectionGate.Dispose();
            }
        }
        finally
        {
            _controlGate.Release();
            _shardedControlGate.Release();
        }

        Task? recovery;
        lock (_reconnectStateGate) recovery = _configuredRecoveryDrained?.Task;
        if (recovery is not null) await recovery.ConfigureAwait(false);
        Task? shardedRecovery;
        lock (_gate) shardedRecovery = _shardedRecovery?.Task;
        if (shardedRecovery is not null) await shardedRecovery.ConfigureAwait(false);
        _lifetimeCancellation.Dispose();
    }

    private ByteRouteDictionary<List<RespireSubscription>> Routes(SubscriptionKind kind)
        => _routes[(int)kind];
}
