using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal sealed partial class SubscriptionHub
{
    private sealed class ClusterNotificationNode(RespireEndpoint endpoint)
    {
        internal readonly RespireEndpoint Endpoint = endpoint;
        // Guards Routes, their consumer lists and Retired. Writers hold the hub's _gate and then
        // this gate; push delivery holds only this gate, so one primary's receive loop never
        // waits for another primary's deliveries or for control-plane work on other nodes.
        internal readonly object Gate = new();
        internal readonly ByteRouteDictionary<List<RespireSubscription>>[] Routes = [new(), new(), new()];
        internal RespireConnection? Connection;
        internal long Epoch;
        // Candidate pushes may be delivered while the previous epoch keeps owning recovery.
        internal long PendingEpoch;
        // Highest epoch handed to a connection. Failed replacements do not reuse their epoch.
        internal long IssuedEpoch;
        // Set once, under _gate and Gate. A retired node has left _notificationNodes and is
        // never revived; a later route for its endpoint creates a new node.
        internal volatile bool Retired;
        internal DateTimeOffset? InterruptedAt;

        internal bool IsEmpty
        {
            get
            {
                foreach (var routes in Routes)
                    if (!routes.IsEmpty) return false;
                return true;
            }
        }
    }

    private sealed record NotificationTopology(long Version, RespireEndpoint[] Endpoints, bool Authoritative);

    // Everything the hub tracks for one live cluster notification subscription. The entry is
    // created with its first route and removed in one place when the subscription ends, so
    // no per-subscription bookkeeping can outlive it. Guarded by _gate.
    private sealed class NotificationSubscriptionState
    {
        // Endpoints whose routes this subscription owns. It can be empty while reconciliation
        // still retries the subscription's only route.
        internal readonly HashSet<RespireEndpoint> Coverage = [];
        // Consecutive reconciliation failures, counted against the endpoint that failed.
        internal int FailedAttempts;
        internal RespireEndpoint? FailingEndpoint;
        // Start of the outage for a route that a node replay rejected and that has not been
        // restored yet.
        internal DateTimeOffset? ReplayRejectedAt;
    }

    // Lock order: _gate, then a node's Gate, then ClientCore._stateGate. Never await while
    // holding any of them. NotifyClusterSubscriptionStateChanged only records state under
    // _stateGate and publishes on the thread pool, so no user handler runs under these locks.
    // The collections below use _gate. _controlGate serializes activation, reconciliation,
    // recovery and removal, which is what makes node creation and epoch issuance race-free.
    private readonly Dictionary<RespireEndpoint, ClusterNotificationNode> _notificationNodes = [];
    private readonly Dictionary<RespireSubscription, NotificationSubscriptionState> _notificationSubscriptions = [];
    // Per-endpoint terminal and retry state. These stay separate from ClusterNotificationNode
    // because they must outlive a retired node (see #690 for the planned state enum).
    private readonly HashSet<RespireEndpoint> _notificationDisconnectedEndpoints = [];
    private readonly HashSet<RespireEndpoint> _notificationExhaustedEndpoints = [];
    // Endpoints whose topology reconciliation failed with an outage. Their recovery is reported
    // only after a later pass acknowledges the route, not when a socket merely reconnects.
    private readonly HashSet<RespireEndpoint> _notificationRetryingEndpoints = [];
    private long _notificationTopologyVersion;
    private NotificationTopology? _latestNotificationTopology;

    // Test seams for the route bookkeeping invariants.
    internal int ClusterNotificationNodeCount
    {
        get { lock (_gate) return _notificationNodes.Count; }
    }

    internal int ClusterNotificationCoverageCount
    {
        get { lock (_gate) return _notificationSubscriptions.Count; }
    }

    internal bool HasClusterNotificationReconciliationState(RespireSubscription subscription)
    {
        lock (_gate)
            return _notificationSubscriptions.TryGetValue(subscription, out var state) && state.FailedAttempts != 0;
    }

    internal bool IsClusterNotificationEndpointRetrying(RespireEndpoint endpoint)
    {
        lock (_gate) return _notificationRetryingEndpoints.Contains(endpoint);
    }

    // Test seam: marks an endpoint as waiting for a failed reconciliation route.
    internal void MarkClusterNotificationEndpointRetrying(RespireEndpoint endpoint)
    {
        lock (_gate) _notificationRetryingEndpoints.Add(endpoint);
    }

    // Test seam: routes one channel frame through the endpoint's current node, as its receive
    // loop would. Returns false when the endpoint has no node.
    internal bool TryDeliverClusterNotification(
        RespireEndpoint endpoint, ReadOnlySpan<byte> channel, ReadOnlySpan<byte> payload)
    {
        ClusterNotificationNode? node;
        lock (_gate)
        {
            if (!_notificationNodes.TryGetValue(endpoint, out node)) return false;
        }
        DeliverNotification(node, Volatile.Read(ref node.Epoch), SubscriptionKind.Channel,
            channel, channel, false, payload);
        return true;
    }

    private async ValueTask ActivateClusterNotificationsAsync(
        RespireSubscription subscription, CancellationToken cancellationToken)
    {
        long observedTopologyVersion;
        lock (_gate) observedTopologyVersion = _notificationTopologyVersion;
        var desired = await GetNotificationCoverageAsync(subscription, cancellationToken).ConfigureAwait(false);
        var touched = new HashSet<RespireEndpoint>();
        var uncertain = new HashSet<RespireEndpoint>();
        try
        {
            foreach (var (endpoint, names) in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // An explicit subscription retries an endpoint whose recovery was exhausted.
                var node = await EnsureNotificationNodeAsync(endpoint, cancellationToken, retryExhausted: true)
                    .ConfigureAwait(false);
                touched.Add(endpoint);
                foreach (var name in names)
                {
                    try
                    {
                        await AddNotificationRouteAsync(node, subscription, name, trackCoverage: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (!ContainsServerRejection(error)) uncertain.Add(endpoint);
                        throw;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            // A topology change published while these routes were acknowledged is reconciled
            // asynchronously only after this activation releases the control gate. Apply it
            // here so SubscribeAsync never completes while a current owner is unsubscribed.
            while (true)
            {
                NotificationTopology? latest;
                lock (_gate) latest = _latestNotificationTopology;
                if (latest is null || latest.Version <= observedTopologyVersion) break;
                observedTopologyVersion = latest.Version;
                // Use the caller's token too, so a stalled new primary cannot outlive cancellation.
                using var activation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _lifetimeCancellation.Token);
                await ReconcileNotificationSubscriptionAsync(subscription, latest.Version, latest.Endpoints,
                    latest.Authoritative, new StrongBox<RespireEndpoint?>(), activation.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            // Every failure rolls back, including cancellation: a cancelled SubscribeAsync must
            // not leave routes behind. Rollback needs no token to stay bounded, because each
            // UNSUBSCRIBE uses the command timeout and uncertain sockets are closed, not awaited.
            lock (_gate)
            {
                if (_notificationSubscriptions.TryGetValue(subscription, out var state)) touched.UnionWith(state.Coverage);
            }
            await RollbackNotificationActivationAsync(subscription, touched, uncertain).ConfigureAwait(false);
            lock (_gate) EndNotificationSubscriptionLocked(subscription);
            throw;
        }
    }

    // Subscription state outlives failed routes so reconciliation keeps retrying the
    // subscription. Remove it only when the subscription ends: activation failure, removal,
    // or exhaustion.
    private void EndNotificationSubscriptionLocked(RespireSubscription subscription)
        => _notificationSubscriptions.Remove(subscription);

    // Adds a consumer to a node route. Returns true when the route is new on that node and
    // still needs a SUBSCRIBE. Requires _gate.
    private static bool AddNotificationRouteLocked(
        ClusterNotificationNode node, RespireSubscription subscription, RespireChannel name)
    {
        // Route tables key on the channel bytes; metadata is dropped once, here.
        var routeName = name.WithoutNotificationMetadata();
        lock (node.Gate)
        {
            var routes = node.Routes[(int)subscription.Kind];
            var added = !routes.TryGetValue(routeName, out var consumers);
            if (added) routes.Add(routeName, consumers = []);
            if (!consumers.Contains(subscription)) consumers.Add(subscription);
            return added;
        }
    }

    // Removes a consumer from a node route. Returns true when that was the route's last
    // consumer and the route was removed. Requires _gate.
    private static bool RemoveNotificationRouteLocked(
        ClusterNotificationNode node, SubscriptionKind kind, RespireSubscription subscription, RespireChannel name)
    {
        lock (node.Gate)
        {
            var routes = node.Routes[(int)kind];
            if (!routes.TryGetValue(name, out var consumers) || !consumers.Remove(subscription)) return false;
            if (consumers.Count != 0) return false;
            routes.Remove(name);
            return true;
        }
    }

    // Retires a node so no stale push or watcher acts on it again. Returns false when it was
    // already retired. Requires _gate.
    private bool TryRetireNotificationNodeLocked(ClusterNotificationNode node)
    {
        lock (node.Gate)
        {
            if (node.Retired) return false;
            node.Retired = true;
            Interlocked.Increment(ref node.Epoch);
        }
        if (_notificationNodes.TryGetValue(node.Endpoint, out var current) && ReferenceEquals(current, node))
            _notificationNodes.Remove(node.Endpoint);
        return true;
    }

    private ClusterNotificationNode? TryGetNotificationNode(RespireEndpoint endpoint)
    {
        lock (_gate) return _notificationNodes.TryGetValue(endpoint, out var node) ? node : null;
    }

    // Adds a route for the subscription and sends SUBSCRIBE when the node does not carry it
    // yet. A definite server rejection removes the provisional route again, because Redis did
    // not install it. Any other failure leaves it in place: the command may have reached Redis,
    // so callers treat the endpoint as uncertain and close or roll back its socket.
    private async ValueTask AddNotificationRouteAsync(
        ClusterNotificationNode node, RespireSubscription subscription, RespireChannel name,
        bool trackCoverage, CancellationToken cancellationToken)
    {
        bool subscribe;
        RespireConnection? connection;
        lock (_gate)
        {
            subscribe = AddNotificationRouteLocked(node, subscription, name);
            if (trackCoverage)
            {
                if (!_notificationSubscriptions.TryGetValue(subscription, out var state))
                    _notificationSubscriptions.Add(subscription, state = new NotificationSubscriptionState());
                state.Coverage.Add(node.Endpoint);
            }
            connection = node.Connection;
        }
        if (!subscribe) return;
        try
        {
            if (connection is null)
                throw new RespireConnectionException($"Cluster notification connection to {node.Endpoint} is unavailable.");
            await SendControlAsync(connection, SubscribeVerb(subscription.Kind),
                SubscribeOperation(subscription.Kind), name, cancellationToken, instrument: true)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (ContainsServerRejection(error))
        {
            lock (_gate) RemoveNotificationRouteLocked(node, subscription.Kind, subscription, name);
            throw;
        }
    }

    private async ValueTask RollbackNotificationActivationAsync(
        RespireSubscription subscription, HashSet<RespireEndpoint> touched,
        HashSet<RespireEndpoint>? uncertain = null)
    {
        foreach (var endpoint in touched)
        {
            if (TryGetNotificationNode(endpoint) is not { } node) continue;
            if (uncertain?.Contains(endpoint) != true)
            {
                await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false);
                continue;
            }

            // The failed SUBSCRIBE may still own the head of this connection's reply stream, so
            // a follow-up UNSUBSCRIBE would wait behind it for a full command timeout while the
            // control gate is held. Close the socket instead: an emptied node retires, and a
            // shared node reconnects through its watcher and replays only the surviving routes,
            // publishing a reconnect gap to the subscriptions that share it.
            RespireConnection? connectionToClose;
            bool retired;
            lock (_gate)
            {
                foreach (var name in subscription.Names)
                    RemoveNotificationRouteLocked(node, subscription.Kind, subscription, name);
                if (_notificationSubscriptions.TryGetValue(subscription, out var state)) state.Coverage.Remove(endpoint);
                retired = node.IsEmpty && TryRetireNotificationNodeLocked(node);
                connectionToClose = node.Connection;
            }
            // A watcher may already have reported this endpoint as reconnecting. It exits once
            // the node retires, so forget that state here.
            if (retired) core.ClearClusterSubscriptionState(endpoint);
            if (connectionToClose is not null)
            {
                try { await connectionToClose.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { TryLogDebug(error, "Closing a rolled back cluster notification connection failed"); }
            }
        }
    }

    private async ValueTask<Dictionary<RespireEndpoint, List<RespireChannel>>> GetNotificationCoverageAsync(
        RespireSubscription subscription, CancellationToken cancellationToken,
        RespireEndpoint[]? primarySnapshot = null)
    {
        var cluster = core.Cluster ?? throw new InvalidOperationException("Cluster notification routing requires Redis Cluster.");
        RespireEndpoint[] primaries = [];
        if (subscription.Names.Any(static name => name.RoutingScope == RespireChannelRoutingScope.AllPrimaries))
            primaries = primarySnapshot ?? await cluster.GetPrimaryEndpointsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<RespireEndpoint, List<RespireChannel>> desired = [];
        foreach (var name in subscription.Names)
        {
            IEnumerable<RespireEndpoint> endpoints = name.RoutingScope switch
            {
                RespireChannelRoutingScope.AllPrimaries => primaries,
                RespireChannelRoutingScope.KeyOwner when name.RoutingSlot is { } slot
                    => [await cluster.GetSlotOwnerEndpointAsync(slot, cancellationToken).ConfigureAwait(false)],
                _ => throw new ArgumentException("Cluster notification descriptor has no valid routing scope.", nameof(subscription)),
            };
            foreach (var endpoint in endpoints)
            {
                if (!desired.TryGetValue(endpoint, out var names)) desired.Add(endpoint, names = []);
                if (!names.Contains(name)) names.Add(name);
            }
        }
        return desired;
    }

    // Callers hold _controlGate. That serializes node creation and connection replacement for
    // every endpoint, so two callers never race to create or replace the same node.
    private async ValueTask<ClusterNotificationNode> EnsureNotificationNodeAsync(
        RespireEndpoint endpoint, CancellationToken cancellationToken, bool retryExhausted = false)
    {
        ClusterNotificationNode? existing;
        lock (_gate)
        {
            if (!retryExhausted && _notificationExhaustedEndpoints.Contains(endpoint))
                throw new RespireConnectionException(
                    $"Cluster notification recovery is exhausted for {endpoint}; subscribe again or recreate the client to retry.");
            _notificationNodes.TryGetValue(endpoint, out existing);
            if (existing?.Connection is { IsConnected: true }) return existing;
            if (existing?.Connection is { IsConnected: false } && existing.InterruptedAt is null)
                existing.InterruptedAt = DateTimeOffset.UtcNow;
        }
        // Nodes in _notificationNodes are never retired, so an existing node is reused as is.
        var node = existing ?? new ClusterNotificationNode(endpoint);
        var epoch = Math.Max(node.IssuedEpoch, Volatile.Read(ref node.Epoch)) + 1;
        node.IssuedEpoch = epoch;
        var (connection, rejected) = await ConnectAndReplayNotificationNodeAsync(node, epoch, cancellationToken)
            .ConfigureAwait(false);
        await CommitNotificationReplacementAsync(node, connection, epoch, rejected).ConfigureAwait(false);
        PublishNotificationNodeRecovery(node);
        // Start watching only after recovery is published. If this socket already closed,
        // the watcher then reports the new outage after Connected instead of having it
        // cleared by the recovery publication above.
        _ = WatchNotificationNodeAsync(node, connection, epoch);
        return node;
    }

    // Opens a replacement connection for the node and replays every saved route on it. The
    // node itself is not changed: candidate pushes are accepted through PendingEpoch, and on
    // failure the replacement is closed so the previous epoch's watcher stays in charge.
    // Returns the routes the server rejected; the socket stays healthy for the others.
    private async ValueTask<(RespireConnection Connection, List<(SubscriptionKind Kind, RespireChannel Name)>? Rejected)>
        ConnectAndReplayNotificationNodeAsync(ClusterNotificationNode node, long epoch, CancellationToken cancellationToken)
    {
        var endpoint = node.Endpoint;
        var options = core.Options.ToConnectionOptions(
            (in RespValue value) => OnNotificationPush(node, epoch, in value)) with
        {
            SubscriptionConfirmationHandler = (in RespValue _) => { },
        };
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token);
        var connection = await RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port, options,
            core.Logger, connectCancellation.Token).ConfigureAwait(false);
        Volatile.Write(ref node.PendingEpoch, epoch);
        List<(SubscriptionKind Kind, RespireChannel Name)>? rejected = null;
        try
        {
            foreach (var (kind, name) in SnapshotNotificationRoutes(node))
            {
                try
                {
                    await SendControlAsync(connection, SubscribeVerb(kind), SubscribeOperation(kind), name,
                        cancellationToken, instrument: false).ConfigureAwait(false);
                }
                catch (Exception error) when (ContainsServerRejection(error))
                {
                    // A rejection (for example NOPERM after an ACL change) is specific to this
                    // route and leaves the socket healthy. Keep replaying the others.
                    TryLogWarning(error, "Cluster notification route {Route} was rejected by {Host}:{Port}",
                        name.ToString(), endpoint.Host, endpoint.Port);
                    (rejected ??= []).Add((kind, name));
                }
            }
        }
        catch
        {
            Volatile.Write(ref node.PendingEpoch, 0);
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { TryLogDebug(error, "Closing a failed cluster notification replacement failed"); }
            throw;
        }
        return (connection, rejected);
    }

    // Makes a fully replayed connection current. The previous epoch stays visible to its
    // watcher until every saved route has replayed; every replacement gets a fresh IssuedEpoch.
    // Routes the server rejected are dropped locally so they match the server, and topology
    // reconciliation retries them for their owning subscriptions under the reconnect policy.
    private async ValueTask CommitNotificationReplacementAsync(
        ClusterNotificationNode node, RespireConnection connection, long epoch,
        List<(SubscriptionKind Kind, RespireChannel Name)>? rejected)
    {
        bool retiredDuringReplay;
        lock (_gate)
        {
            retiredDuringReplay = node.Retired;
            if (!retiredDuringReplay)
            {
                Interlocked.Exchange(ref node.Epoch, epoch);
                node.Connection = connection;
                _notificationNodes[node.Endpoint] = node;
                _notificationExhaustedEndpoints.Remove(node.Endpoint);
            }
            Volatile.Write(ref node.PendingEpoch, 0);
        }
        if (retiredDuringReplay)
        {
            // Unreachable while every retirement holds _controlGate, as its callers and this
            // method's do. Kept as a release-build guard, because reviving a retired node would
            // leak its socket and deliver to subscriptions that already ended.
            Debug.Fail("A cluster notification node was retired while its replacement replayed.");
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { TryLogDebug(error, "Closing a replacement for a retired cluster notification node failed"); }
            throw new RespireConnectionException($"Cluster notification routes for {node.Endpoint} were removed during recovery.");
        }
        if (rejected is null) return;
        lock (_gate)
        {
            var interruptedAt = node.InterruptedAt ?? DateTimeOffset.UtcNow;
            foreach (var (kind, name) in rejected)
            {
                List<RespireSubscription> consumers;
                lock (node.Gate)
                {
                    var routes = node.Routes[(int)kind];
                    if (!routes.TryGetValue(name, out var found)) continue;
                    consumers = found;
                    routes.Remove(name);
                }
                foreach (var subscription in consumers)
                {
                    // A consumer without state has already ended; nothing will retry it.
                    if (!_notificationSubscriptions.TryGetValue(subscription, out var state)) continue;
                    if (state.ReplayRejectedAt is not { } started || interruptedAt < started)
                        state.ReplayRejectedAt = interruptedAt;
                }
            }
        }
        ScheduleNotificationReconciliation();
    }

    // Publishes the reconnect gap for every replayed route and, unless a failed
    // reconciliation route on this endpoint is still missing, the endpoint's recovery.
    private void PublishNotificationNodeRecovery(ClusterNotificationNode node)
    {
        bool recovered;
        bool reconciliationPending;
        lock (_gate)
        {
            var wasInterrupted = node.InterruptedAt is not null;
            // Always clear the disconnected marker, even for an interrupted node.
            var wasDisconnected = _notificationDisconnectedEndpoints.Remove(node.Endpoint);
            recovered = wasInterrupted || wasDisconnected;
            // A reconciliation route that failed on this endpoint (and closed its socket) is
            // still missing. Keep the endpoint reconnecting until a later pass acknowledges it;
            // ClearNotificationReconciliationRetries reports the recovery then.
            reconciliationPending = _notificationRetryingEndpoints.Contains(node.Endpoint);
        }
        PublishNotificationReconnectGaps(node);
        if (recovered && !reconciliationPending)
            core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                node.Endpoint, RespireConnectionState.Connected, null));
    }

    private void OnNotificationPush(ClusterNotificationNode node, long epoch, in RespValue value)
    {
        if (!IsCurrentNotificationEpoch(node, epoch)) return;
        var elements = value.AsArray();
        if (elements.Length < 3) return;
        var frame = elements[0].AsSpan();
        if (frame.SequenceEqual("message"u8))
            DeliverNotification(node, epoch, SubscriptionKind.Channel, elements[1].AsSpan(), elements[1].AsSpan(), false, elements[2].AsSpan());
        else if (frame.SequenceEqual("pmessage"u8) && elements.Length >= 4)
            DeliverNotification(node, epoch, SubscriptionKind.Pattern, elements[1].AsSpan(), elements[2].AsSpan(), true, elements[3].AsSpan());
    }

    private void DeliverNotification(ClusterNotificationNode node, long epoch, SubscriptionKind kind,
        ReadOnlySpan<byte> routeName, ReadOnlySpan<byte> channel, bool pattern, ReadOnlySpan<byte> payload)
    {
        List<(RespireSubscription Subscription, RespireSubscriptionGap Gap)>? drops = null;
        // The node gate, not the hub-wide _gate: primaries deliver independently, and control
        // operations on other nodes never block this receive loop. Validate and enqueue under
        // the gate that retirement, route changes and reconnect gaps use, as Deliver does.
        lock (node.Gate)
        {
            if (_disposed || !IsCurrentNotificationEpoch(node, epoch) || node.Retired
                || !node.Routes[(int)kind].TryGetValue(routeName, out var cachedName, out var targets)) return;
            // Copy only after a route matched, so a frame for a route that was just removed
            // allocates nothing. Only this node's receive loop and rare route writers contend
            // for this gate, so the copy does not hold up other primaries.
            var message = new RespireMessage(
                pattern ? RespireChannel.FromOwnedBytes(channel.ToArray()) : cachedName,
                pattern ? cachedName : (RespireChannel?)null,
                payload.ToArray(), core.Options.Serializer);
            foreach (var target in targets)
                if (target.Buffer.Write(message) is { } gap) (drops ??= []).Add((target, gap));
        }
        if (drops is not null)
            foreach (var (subscription, gap) in drops) subscription.NotifyDrop(gap);
    }

    private static bool IsCurrentNotificationEpoch(ClusterNotificationNode node, long epoch)
        => Volatile.Read(ref node.Epoch) == epoch || Volatile.Read(ref node.PendingEpoch) == epoch;

    // Fire-and-forget by design: one watcher per connection. Every failure is observed here,
    // so no exception escapes as an unobserved task fault, including during disposal.
    private async Task WatchNotificationNodeAsync(
        ClusterNotificationNode node, RespireConnection connection, long epoch)
    {
        try
        {
            await WatchNotificationNodeCoreAsync(node, connection, epoch).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception error)
        {
            TryLogWarning(error, "Cluster notification recovery for {Host}:{Port} stopped unexpectedly",
                node.Endpoint.Host, node.Endpoint.Port);
        }
    }

    private async Task WatchNotificationNodeCoreAsync(
        ClusterNotificationNode node, RespireConnection connection, long epoch)
    {
        await connection.Closed.ConfigureAwait(false);
        var attempt = 1;
        var nextDelay = NotificationReconnectDelay(attempt);
        var lastError = connection.CloseError;
        lock (_gate)
        {
            if (_disposed || node.Retired || Volatile.Read(ref node.Epoch) != epoch || node.IsEmpty) return;
            if (node.InterruptedAt is null) node.InterruptedAt = DateTimeOffset.UtcNow;
            // Publish the first attempt under _gate, so a concurrent release cannot retire the
            // node and clear its state between this check and the event.
            core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                node.Endpoint, RespireConnectionState.Reconnecting, lastError)
            {
                ReconnectAttempt = attempt,
                NextReconnectDelay = nextDelay,
            });
        }
        while (!_disposed && !node.Retired)
        {
            try
            {
                RespireTelemetry.RecordReconnectAttempt(node.Endpoint.Host, node.Endpoint.Port,
                    attempt, nextDelay, RespireReconnectSource.PubSub);
            }
            catch (Exception telemetryError)
            {
                TryLogWarning(telemetryError, "Cluster notification reconnect telemetry listener threw");
            }
            if (attempt > 1)
                core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                    node.Endpoint, RespireConnectionState.Reconnecting, lastError)
                {
                    ReconnectAttempt = attempt,
                    NextReconnectDelay = nextDelay,
                });
            try
            {
                await Task.Delay(nextDelay, _recoveryClock, _lifetimeCancellation.Token).ConfigureAwait(false);
                await _controlGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_disposed || node.Retired || Volatile.Read(ref node.Epoch) != epoch) return;
                    _ = await EnsureNotificationNodeAsync(node.Endpoint, _lifetimeCancellation.Token).ConfigureAwait(false);
                    return;
                }
                finally { _controlGate.Release(); }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                lastError = error;
                if (core.Options.ReconnectPolicy?.IsExhausted(attempt) == true)
                {
                    await ExhaustNotificationNodeAsync(node, epoch, error, attempt).ConfigureAwait(false);
                    return;
                }
                if (attempt < int.MaxValue) attempt++;
                nextDelay = NotificationReconnectDelay(attempt);
                TryLogWarning(error, "Cluster notification reconnect failed for {Host}:{Port}; retrying in {Delay}",
                    node.Endpoint.Host, node.Endpoint.Port, nextDelay);
            }
        }
    }

    private async ValueTask ExhaustNotificationNodeAsync(
        ClusterNotificationNode node, long expectedEpoch, Exception error, int attempt)
    {
        RespireSubscription[] subscriptions;
        List<RespireConnection> close = [];
        List<(ClusterNotificationNode Node, RespireConnection Connection, SubscriptionKind Kind, RespireChannel Name)> unsubscribe = [];
        List<RespireEndpoint> collateralEndpoints = [];
        List<(ClusterNotificationNode Node, RespireConnection Connection, SubscriptionKind Kind, RespireChannel Name,
            CancellationTokenSource Timeout, Task Ack)> cleanupAcks = [];
        await _controlGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (node.Retired || Volatile.Read(ref node.Epoch) != expectedEpoch) return;
                lock (node.Gate)
                    subscriptions = node.Routes.SelectMany(static routes => routes.Values)
                        .SelectMany(static consumers => consumers).Distinct().ToArray();
                TryRetireNotificationNodeLocked(node);
                _notificationDisconnectedEndpoints.Add(node.Endpoint);
                _notificationExhaustedEndpoints.Add(node.Endpoint);
                // Disconnected is terminal now. A pending reconciliation retry for this
                // endpoint must not later report it as recovered.
                _notificationRetryingEndpoints.Remove(node.Endpoint);
                // A subscription is ended as a whole: a cluster-wide one without this primary
                // would silently miss its events. Its routes on other nodes are released too.
                var otherNodes = _notificationNodes.Values.ToArray();
                foreach (var subscription in subscriptions)
                {
                    EndNotificationSubscriptionLocked(subscription);
                    foreach (var other in otherNodes)
                    {
                        foreach (var name in subscription.Names)
                        {
                            if (RemoveNotificationRouteLocked(other, subscription.Kind, subscription, name)
                                && other.Connection is { IsConnected: true } otherConnection)
                                unsubscribe.Add((other, otherConnection, subscription.Kind, name));
                        }
                    }
                }
                foreach (var other in otherNodes)
                {
                    if (other.IsEmpty && TryRetireNotificationNodeLocked(other))
                    {
                        collateralEndpoints.Add(other.Endpoint);
                        if (other.Connection is { } otherConnection) close.Add(otherConnection);
                    }
                }
            }

            // Enqueue removals while activation is gated so later subscriptions cannot be
            // overtaken. They run concurrently; their acknowledgements are awaited only after
            // the control gate is released.
            foreach (var (other, connection, kind, name) in unsubscribe)
            {
                var timeout = new CancellationTokenSource(core.Options.CommandTimeout ?? core.Options.ConnectTimeout);
                var ack = SendControlAsync(connection, UnsubscribeVerb(kind), UnsubscribeOperation(kind), name,
                    timeout.Token, instrument: true).AsTask();
                cleanupAcks.Add((other, connection, kind, name, timeout, ack));
            }
            if (node.Connection is { } nodeConnection) close.Add(nodeConnection);
        }
        finally { _controlGate.Release(); }

        core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
            node.Endpoint, RespireConnectionState.Disconnected, error)
        {
            ReconnectAttempt = attempt,
            ReconnectExhausted = true,
        });
        foreach (var endpoint in collateralEndpoints) core.ClearClusterSubscriptionState(endpoint);
        try { RespireTelemetry.RecordReconnectExhaustion(
            node.Endpoint.Host, node.Endpoint.Port, RespireReconnectSource.PubSub); }
        catch (Exception telemetryError)
        {
            TryLogWarning(telemetryError, "Cluster notification reconnect exhaustion telemetry listener threw");
        }
        foreach (var subscription in subscriptions) subscription.CompleteFromReconnectExhaustion();

        foreach (var (other, connection, kind, name, timeout, ack) in cleanupAcks)
        {
            try
            {
                await ack.ConfigureAwait(false);
                // The gate was released before this acknowledgement. With a small in-flight
                // limit the UNSUBSCRIBE may even have been queued after a newer SUBSCRIBE for
                // the same route. If the route is live again, reconnect so the watcher replays it.
                lock (_gate)
                {
                    bool live;
                    lock (other.Gate) live = other.Routes[(int)kind].TryGetValue(name, out _);
                    if (!other.Retired && ReferenceEquals(other.Connection, connection) && live)
                        close.Add(connection);
                }
            }
            catch (Exception cleanupError)
            {
                TryLogDebug(cleanupError, "Cluster notification unsubscribe failed during exhaustion cleanup");
                // Closing a still-shared socket makes its watcher reconnect and replay every live
                // route, including one acquired after the gate was released, with a delivery gap.
                close.Add(connection);
            }
            finally { timeout.Dispose(); }
        }

        foreach (var connection in close.Distinct())
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception closeError) { TryLogDebug(closeError, "Closing an exhausted cluster notification connection failed"); }
        }
    }

    private void PublishNotificationReconnectGaps(ClusterNotificationNode node)
    {
        List<(RespireSubscription Subscription, RespireSubscriptionGap Gap)> gaps = [];
        lock (_gate)
        {
            if (node.InterruptedAt is not { } started) return;
            var ended = DateTimeOffset.UtcNow;
            if (ended < started) ended = started;
            // Write gaps under the node gate so they are ordered with this node's deliveries.
            lock (node.Gate)
            {
                HashSet<RespireSubscription> subscriptions = [];
                foreach (var routes in node.Routes)
                    foreach (var targets in routes.Values)
                        subscriptions.UnionWith(targets);
                // Every subscription with a replayed route learns about the outage now, even
                // when another of its routes was rejected and is still being retried. That
                // rejected route reports its own, longer gap once reconciliation restores it.
                foreach (var subscription in subscriptions)
                {
                    var gap = new RespireSubscriptionGap(RespireSubscriptionGapReason.Reconnect, started, ended);
                    if (subscription.Buffer.WriteGap(gap)) gaps.Add((subscription, gap));
                }
            }
            node.InterruptedAt = null;
        }
        foreach (var (subscription, gap) in gaps) subscription.NotifyGap(gap);
    }

    private (SubscriptionKind Kind, RespireChannel Name)[] SnapshotNotificationRoutes(ClusterNotificationNode node)
    {
        lock (_gate)
        {
            var snapshot = new List<(SubscriptionKind, RespireChannel)>();
            for (var i = 0; i < node.Routes.Length; i++)
                foreach (var name in node.Routes[i].Names) snapshot.Add(((SubscriptionKind)i, name));
            return [.. snapshot];
        }
    }

    private async ValueTask ReleaseNotificationRoutesAsync(
        ClusterNotificationNode node, RespireSubscription subscription,
        IReadOnlyCollection<RespireChannel>? names = null, bool removeCoverage = true)
    {
        List<RespireChannel> released = [];
        RespireConnection? connection;
        lock (_gate)
        {
            connection = node.Connection;
            foreach (var name in names ?? subscription.Names)
                if (RemoveNotificationRouteLocked(node, subscription.Kind, subscription, name)) released.Add(name);
            // Only the subscription's end removes its state entry; see EndNotificationSubscriptionLocked.
            if (removeCoverage && _notificationSubscriptions.TryGetValue(subscription, out var state))
                state.Coverage.Remove(node.Endpoint);
        }
        if (connection is { IsConnected: true } && released.Count != 0)
        {
            // Pipeline the removals under one deadline instead of one round trip each.
            using var timeout = new CancellationTokenSource(core.Options.CommandTimeout ?? core.Options.ConnectTimeout);
            var acks = new Task[released.Count];
            for (var index = 0; index < released.Count; index++)
                acks[index] = SendControlAsync(connection, UnsubscribeVerb(subscription.Kind),
                    UnsubscribeOperation(subscription.Kind), released[index], timeout.Token, instrument: true).AsTask();
            try { await Task.WhenAll(acks).ConfigureAwait(false); }
            catch (Exception error) when (error is RespireException or OperationCanceledException)
            {
                // The server may still hold a route this node no longer tracks. Closing the
                // socket clears it; a shared node reconnects and replays only its live routes.
                TryLogDebug(error, "Cluster notification unsubscribe failed");
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception closeError) { TryLogDebug(closeError, "Closing an uncertain cluster notification connection failed"); }
            }
        }
        RespireConnection? connectionToDispose = null;
        lock (_gate)
        {
            if (removeCoverage && node.IsEmpty && TryRetireNotificationNodeLocked(node))
                connectionToDispose = node.Connection;
        }
        if (connectionToDispose is not null)
        {
            core.ClearClusterSubscriptionState(node.Endpoint);
            // Route state is already committed; a failed close must not abort callers that
            // still have to complete or unsubscribe other routes.
            try { await connectionToDispose.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { TryLogDebug(error, "Closing a retired cluster notification connection failed"); }
        }
    }

    internal void NotifyTopologyChanged(long version, RespireEndpoint[] endpoints, bool authoritative)
    {
        if (_disposed) return;
        // Publish the version and its snapshot together so activation never observes a newer
        // version without the endpoints that belong to it.
        lock (_gate)
        {
            if (version <= _notificationTopologyVersion) return;
            _latestNotificationTopology = new NotificationTopology(version, endpoints, authoritative);
            Volatile.Write(ref _notificationTopologyVersion, version);
        }
        if (authoritative)
        {
            // An endpoint absent from a complete map has left the cluster. Forget its terminal
            // state so a primary later started at that address is treated as a new node.
            List<RespireEndpoint> departed = [];
            lock (_gate)
            {
                _notificationExhaustedEndpoints.RemoveWhere(endpoint => Array.IndexOf(endpoints, endpoint) < 0);
                foreach (var endpoint in _notificationDisconnectedEndpoints.Union(_notificationRetryingEndpoints))
                    if (Array.IndexOf(endpoints, endpoint) < 0) departed.Add(endpoint);
                foreach (var endpoint in departed)
                {
                    _notificationDisconnectedEndpoints.Remove(endpoint);
                    _notificationRetryingEndpoints.Remove(endpoint);
                }
            }
            foreach (var endpoint in departed) core.ClearClusterSubscriptionState(endpoint);
        }
        _ = ReconcileNotificationsAsync(version, endpoints, authoritative);
    }

    private async Task ReconcileNotificationsAsync(
        long version, RespireEndpoint[]? endpoints, bool authoritative, int attempt = 0,
        RespireSubscription? onlySubscription = null)
    {
        var retryFullPass = false;
        List<(RespireSubscription Subscription, TimeSpan Delay)>? subscriptionRetries = null;
        try
        {
            // Held across network awaits on purpose: reconciliation must not interleave with
            // activation, recovery or removal of the same routes.
            await _controlGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || version != Volatile.Read(ref _notificationTopologyVersion)) return;
                RespireSubscription[] subscriptions;
                lock (_gate)
                {
                    if (onlySubscription is { } selected)
                        subscriptions = _notificationSubscriptions.ContainsKey(selected) ? [selected] : [];
                    else
                        subscriptions = [.. _notificationSubscriptions.Keys];
                }
                List<(RespireSubscription Subscription, RespireEndpoint? Endpoint, Exception Error, int Attempt)>? failures = null;
                foreach (var subscription in subscriptions)
                {
                    // Reconcile each subscription independently so one failing endpoint cannot
                    // keep every other subscription on the previous topology.
                    var failingEndpoint = new StrongBox<RespireEndpoint?>();
                    try
                    {
                        if (!await ReconcileNotificationSubscriptionAsync(
                                subscription, version, endpoints, authoritative, failingEndpoint,
                                _lifetimeCancellation.Token).ConfigureAwait(false))
                            return;
                        lock (_gate)
                        {
                            if (_notificationSubscriptions.TryGetValue(subscription, out var reconciled))
                            {
                                reconciled.FailedAttempts = 0;
                                reconciled.FailingEndpoint = null;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        TryLogWarning(error, "Cluster notification topology reconciliation failed");
                        // Each failing endpoint gets the policy's full attempt budget. A failure
                        // against a different endpoint, such as a newer topology replacing an
                        // unreachable primary, starts a new count, so a topology that flaps
                        // between endpoints never exhausts a subscription it can still serve.
                        int subscriptionAttempt;
                        lock (_gate)
                        {
                            // Every path that ends a subscription holds _controlGate, as this
                            // pass does, so the state is still present. Guard it anyway.
                            if (!_notificationSubscriptions.TryGetValue(subscription, out var state)) continue;
                            subscriptionAttempt = state.FailedAttempts != 0
                                && state.FailingEndpoint == failingEndpoint.Value ? state.FailedAttempts + 1 : 1;
                            state.FailedAttempts = subscriptionAttempt;
                            state.FailingEndpoint = failingEndpoint.Value;
                        }
                        (failures ??= []).Add((subscription, failingEndpoint.Value, error, subscriptionAttempt));
                    }
                }
                if (failures is not null)
                {
                    var policy = core.Options.ReconnectPolicy;
                    foreach (var (subscription, endpoint, error, subscriptionAttempt) in failures)
                    {
                        if (policy?.IsExhausted(subscriptionAttempt) == true)
                            await ExhaustNotificationSubscriptionAsync(subscription, endpoint, error, subscriptionAttempt)
                                .ConfigureAwait(false);
                        else
                        {
                            var delay = NotificationReconnectDelay(subscriptionAttempt);
                            (subscriptionRetries ??= []).Add((subscription, delay));
                            ReportNotificationReconciliationRetry(endpoint, error, subscriptionAttempt, delay);
                        }
                    }
                }
                HashSet<RespireEndpoint> stillRetrying;
                lock (_gate)
                    stillRetrying = _notificationSubscriptions.Values
                        .Where(static state => state.FailedAttempts != 0 && state.FailingEndpoint is not null)
                        .Select(static state => state.FailingEndpoint!.Value).ToHashSet();
                // Endpoint state stays retrying while any subscription still needs that route.
                ClearNotificationReconciliationRetries(stillRetrying);
            }
            finally { _controlGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            TryLogWarning(error, "Cluster notification topology reconciliation failed");
            retryFullPass = true;
        }
        if (!_disposed && version == Volatile.Read(ref _notificationTopologyVersion)
            && subscriptionRetries is { } retries)
        {
            foreach (var (subscription, delay) in retries)
                _ = RetryNotificationReconciliationAsync(version, endpoints, authoritative, delay, subscription);
        }
        if (retryFullPass && !_disposed && version == Volatile.Read(ref _notificationTopologyVersion))
            _ = RetryNotificationReconciliationAsync(version, endpoints, authoritative,
                NotificationReconnectDelay(attempt + 1), onlySubscription: null, attempt: attempt + 1);
    }

    // Shared by node recovery and topology reconciliation: the reconnect policy's delay, or a
    // doubling backoff from 250 ms capped at 5 s when no policy is configured.
    private TimeSpan NotificationReconnectDelay(int attempt)
        => core.Options.ReconnectPolicy?.GetDelay(attempt)
            ?? TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, Math.Min(attempt - 1, 5)), 5000));

    // Retries the current topology, for example after a node replay dropped a rejected route.
    private void ScheduleNotificationReconciliation()
    {
        NotificationTopology? latest;
        long version;
        lock (_gate)
        {
            latest = _latestNotificationTopology;
            version = _notificationTopologyVersion;
        }
        _ = ReconcileNotificationsAsync(version, latest?.Endpoints, latest?.Authoritative ?? false);
    }

    private async ValueTask<bool> ReconcileNotificationSubscriptionAsync(
        RespireSubscription subscription, long version, RespireEndpoint[]? endpoints, bool authoritative,
        StrongBox<RespireEndpoint?> failingEndpoint, CancellationToken cancellationToken)
    {
        if (version != Volatile.Read(ref _notificationTopologyVersion)) return false;
        var desired = await GetNotificationCoverageAsync(subscription, cancellationToken, endpoints).ConfigureAwait(false);
        if (version != Volatile.Read(ref _notificationTopologyVersion)) return false;
        HashSet<RespireEndpoint> current;
        lock (_gate)
        {
            var coverage = _notificationSubscriptions.TryGetValue(subscription, out var state) ? state.Coverage : null;
            current = coverage is not null ? new HashSet<RespireEndpoint>(coverage) : [];
            current.RemoveWhere(endpoint => !_notificationNodes.TryGetValue(endpoint, out var node) || node.Retired);
            // An empty coverage set keeps the subscription in reconciliation until a route succeeds.
            coverage?.RemoveWhere(endpoint => !current.Contains(endpoint));
        }
        // A partial topology cannot prove that a primary left, so it never removes
        // all-primary routes. Key-owner routes still follow their resolved slot owner.
        // Route tables cache names without metadata, so resolve scope from the descriptors.
        bool Removable(RespireChannel name)
            => authoritative || !subscription.Names.Any(descriptor => descriptor.Equals(name)
                && descriptor.RoutingScope == RespireChannelRoutingScope.AllPrimaries);

        // Subscribe every new route before removing any old route, so an owner swap never
        // leaves a key unobserved while its new owner is still being subscribed.
        List<(ClusterNotificationNode Node, RespireChannel[] Names)> removals = [];
        foreach (var endpoint in desired.Keys.Intersect(current))
        {
            if (version != Volatile.Read(ref _notificationTopologyVersion)) return false;
            failingEndpoint.Value = endpoint;
            if (TryGetNotificationNode(endpoint) is not { } node) continue;
            var desiredNames = desired[endpoint];
            RespireChannel[] currentNames;
            lock (_gate)
            {
                lock (node.Gate)
                {
                    var routes = node.Routes[(int)subscription.Kind];
                    currentNames = routes.Names.Where(name => routes.TryGetValue(name, out var consumers)
                        && consumers.Contains(subscription)).ToArray();
                }
            }
            foreach (var name in desiredNames.Where(name => !currentNames.Contains(name)))
            {
                try
                {
                    await AddNotificationRouteAsync(node, subscription, name, trackCoverage: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    RespireConnection? connection;
                    bool emptied;
                    lock (_gate)
                    {
                        // A rejected route is already gone; an uncertain one is dropped here
                        // because its socket closes below and must not replay it.
                        RemoveNotificationRouteLocked(node, subscription.Kind, subscription, name);
                        connection = node.Connection;
                        emptied = node.IsEmpty;
                        if (emptied)
                        {
                            TryRetireNotificationNodeLocked(node);
                            // Keep the subscription itself discoverable so a later pass retries it.
                            if (_notificationSubscriptions.TryGetValue(subscription, out var state))
                                state.Coverage.Remove(endpoint);
                        }
                    }
                    // The command may have reached Redis before its reply timed out. Retire this
                    // socket so local and server route state cannot diverge; a shared node then
                    // reconnects and replays its remaining routes. A definite server rejection
                    // leaves a shared socket consistent, so only an emptied node closes it then.
                    if (connection is not null && (emptied || !ContainsServerRejection(error)))
                    {
                        try { await connection.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception disposeError)
                        {
                            TryLogDebug(disposeError, "Closing a cluster notification connection after a failed subscribe failed");
                        }
                    }
                    throw;
                }
            }
            var removedNames = currentNames.Where(name => !desiredNames.Contains(name) && Removable(name)).ToArray();
            if (removedNames.Length != 0) removals.Add((node, removedNames));
        }
        foreach (var endpoint in desired.Keys.Except(current))
        {
            failingEndpoint.Value = endpoint;
            HashSet<RespireEndpoint> uncertain = [];
            try
            {
                var node = await EnsureNotificationNodeAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (version != Volatile.Read(ref _notificationTopologyVersion))
                {
                    await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false);
                    return false;
                }
                foreach (var name in desired[endpoint])
                {
                    try
                    {
                        await AddNotificationRouteAsync(node, subscription, name, trackCoverage: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (!ContainsServerRejection(error)) uncertain.Add(endpoint);
                        throw;
                    }
                }
            }
            catch
            {
                // Roll back this endpoint only; any failure, including cancellation, applies.
                await RollbackNotificationActivationAsync(subscription, [endpoint], uncertain).ConfigureAwait(false);
                throw;
            }
        }
        failingEndpoint.Value = null;
        foreach (var (node, names) in removals)
        {
            if (version != Volatile.Read(ref _notificationTopologyVersion)) return false;
            await ReleaseNotificationRoutesAsync(node, subscription, names, removeCoverage: false).ConfigureAwait(false);
        }
        foreach (var endpoint in current.Except(desired.Keys).ToArray())
        {
            if (version != Volatile.Read(ref _notificationTopologyVersion)) return false;
            if (TryGetNotificationNode(endpoint) is not { } node) continue;
            if (subscription.Names.All(Removable))
                await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false);
            else if (subscription.Names.Where(Removable).ToArray() is { Length: > 0 } names)
                await ReleaseNotificationRoutesAsync(node, subscription, names, removeCoverage: false).ConfigureAwait(false);
        }
        PublishRejectedReplayGap(subscription);
        return true;
    }

    private void PublishRejectedReplayGap(RespireSubscription subscription)
    {
        RespireSubscriptionGap? gap = null;
        lock (_gate)
        {
            if (!_notificationSubscriptions.TryGetValue(subscription, out var state)
                || state.ReplayRejectedAt is not { } started) return;
            state.ReplayRejectedAt = null;
            var ended = DateTimeOffset.UtcNow;
            if (ended < started) ended = started;
            var candidate = new RespireSubscriptionGap(RespireSubscriptionGapReason.Reconnect, started, ended);
            if (subscription.Buffer.WriteGap(candidate)) gap = candidate;
        }
        if (gap is { } published) subscription.NotifyGap(published);
    }

    // Topology reconciliation reached the reconnect policy's limit for a subscription. End
    // it as the per-node watcher does, releasing only the routes that subscription owned.
    private async ValueTask ExhaustNotificationSubscriptionAsync(
        RespireSubscription subscription, RespireEndpoint? endpoint, Exception error, int attempt)
    {
        RespireEndpoint[] covered;
        lock (_gate) covered = _notificationSubscriptions.TryGetValue(subscription, out var state) ? [.. state.Coverage] : [];
        foreach (var coveredEndpoint in covered)
        {
            if (TryGetNotificationNode(coveredEndpoint) is not { } node) continue;
            // Release can remove coverage before it fails. The subscription must still reach its
            // terminal state, because no later reconciliation pass can rediscover it.
            try { await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false); }
            catch (Exception releaseError)
            {
                TryLogDebug(releaseError, "Releasing an exhausted cluster notification subscription failed");
            }
        }
        var disconnected = false;
        var wasRetrying = false;
        lock (_gate)
        {
            EndNotificationSubscriptionLocked(subscription);
            if (endpoint is { } retrying) wasRetrying = _notificationRetryingEndpoints.Remove(retrying);
            // Report the endpoint as exhausted only when no notification node remains for it. A
            // rejected SUBSCRIBE is not a connection outage, and a node still serving other
            // subscriptions owns its endpoint's health: its watcher reconnects a socket this
            // failure closed and reports exhaustion only if its own attempts run out.
            if (endpoint is { } failed
                && !ContainsServerRejection(error)
                && !_notificationNodes.ContainsKey(failed))
            {
                _notificationDisconnectedEndpoints.Add(failed);
                _notificationExhaustedEndpoints.Add(failed);
                disconnected = true;
            }
        }
        if (endpoint is { } exhaustedEndpoint)
        {
            if (disconnected)
                core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                    exhaustedEndpoint, RespireConnectionState.Disconnected, error)
                {
                    ReconnectAttempt = attempt,
                    ReconnectExhausted = true,
                });
            else if (wasRetrying)
                core.ClearClusterSubscriptionState(exhaustedEndpoint);
            try
            {
                RespireTelemetry.RecordReconnectExhaustion(
                    exhaustedEndpoint.Host, exhaustedEndpoint.Port, RespireReconnectSource.PubSub);
            }
            catch (Exception telemetryError)
            {
                TryLogWarning(telemetryError, "Cluster notification reconnect exhaustion telemetry listener threw");
            }
        }
        subscription.CompleteFromReconnectExhaustion();
    }

    private async Task RetryNotificationReconciliationAsync(
        long version, RespireEndpoint[]? endpoints, bool authoritative, TimeSpan delay,
        RespireSubscription? onlySubscription, int attempt = 0)
    {
        try { await Task.Delay(delay, _recoveryClock, _lifetimeCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
        if (!_disposed && version == Volatile.Read(ref _notificationTopologyVersion))
            await ReconcileNotificationsAsync(version, endpoints, authoritative, attempt, onlySubscription)
                .ConfigureAwait(false);
    }

    // Marks an endpoint whose topology route failed with an outage (not a server rejection) as
    // retrying, until a later pass reaches or drops it, or the subscription exhausts. With no
    // node, rollback retired it and no watcher reports the outage, so report it here with the
    // same attempt telemetry a watcher uses. A node that still serves other routes had its
    // socket closed by the failure; its watcher reports the outage, and this marker keeps its
    // recovery from being reported before the failed route is acknowledged.
    private void ReportNotificationReconciliationRetry(
        RespireEndpoint? endpoint, Exception error, int attempt, TimeSpan nextDelay)
    {
        if (endpoint is not { } failed || ContainsServerRejection(error)) return;
        lock (_gate)
        {
            _notificationRetryingEndpoints.Add(failed);
            if (_notificationNodes.ContainsKey(failed)) return;
        }
        try
        {
            RespireTelemetry.RecordReconnectAttempt(failed.Host, failed.Port, attempt, nextDelay,
                RespireReconnectSource.PubSub);
        }
        catch (Exception telemetryError)
        {
            TryLogWarning(telemetryError, "Cluster notification reconnect telemetry listener threw");
        }
        core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
            failed, RespireConnectionState.Reconnecting, error)
        {
            ReconnectAttempt = attempt,
            NextReconnectDelay = nextDelay,
        });
    }

    private void ClearNotificationReconciliationRetries(HashSet<RespireEndpoint> stillRetrying)
    {
        List<RespireEndpoint> recovered = [];
        lock (_gate)
        {
            if (_notificationRetryingEndpoints.Count == 0) return;
            foreach (var endpoint in _notificationRetryingEndpoints.Where(endpoint => !stillRetrying.Contains(endpoint)).ToArray())
            {
                _notificationRetryingEndpoints.Remove(endpoint);
                // An exhausted endpoint stays Disconnected until a new route reaches it.
                if (_notificationExhaustedEndpoints.Contains(endpoint)
                    || _notificationDisconnectedEndpoints.Contains(endpoint)) continue;
                // A node that is still reconnecting owns its endpoint state; its watcher reports
                // the recovery once the socket is back.
                if (_notificationNodes.TryGetValue(endpoint, out var node) && node.InterruptedAt is not null) continue;
                recovered.Add(endpoint);
            }
        }
        foreach (var endpoint in recovered)
            core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                endpoint, RespireConnectionState.Connected, null));
    }

    private static bool ContainsServerRejection(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is RespireServerException) return true;
        return false;
    }
}
