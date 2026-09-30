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
        internal readonly ByteRouteDictionary<List<RespireSubscription>>[] Routes = [new(), new(), new()];
        internal RespireConnection? Connection;
        internal long Epoch;
        internal volatile bool Retired;
        internal DateTimeOffset? InterruptedAt;
    }

    private readonly Dictionary<RespireEndpoint, ClusterNotificationNode> _notificationNodes = [];
    private readonly Dictionary<RespireSubscription, HashSet<RespireEndpoint>> _notificationCoverage = [];
    private long _notificationTopologyVersion;

    private async ValueTask ActivateClusterNotificationsAsync(
        RespireSubscription subscription, CancellationToken cancellationToken)
    {
        var desired = await GetNotificationCoverageAsync(subscription, cancellationToken).ConfigureAwait(false);
        var touched = new HashSet<RespireEndpoint>();
        try
        {
            foreach (var (endpoint, names) in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var node = await EnsureNotificationNodeAsync(endpoint, cancellationToken).ConfigureAwait(false);
                touched.Add(endpoint);
                foreach (var name in names)
                {
                    bool subscribe;
                    lock (_gate)
                    {
                        var routes = node.Routes[(int)subscription.Kind];
                        if (!routes.TryGetValue(name, out var consumers))
                        {
                            consumers = [];
                            routes.Add(name.WithoutNotificationMetadata(), consumers);
                            subscribe = true;
                        }
                        else subscribe = false;
                        if (!consumers.Contains(subscription)) consumers.Add(subscription);
                        if (!_notificationCoverage.TryGetValue(subscription, out var coverage))
                            _notificationCoverage.Add(subscription, coverage = []);
                        coverage.Add(endpoint);
                    }

                    if (subscribe)
                    {
                        await SendControlAsync(node.Connection!, SubscribeVerb(subscription.Kind),
                            SubscribeOperation(subscription.Kind), name, cancellationToken, instrument: true)
                            .ConfigureAwait(false);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await RollbackNotificationActivationAsync(subscription, touched).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask RollbackNotificationActivationAsync(
        RespireSubscription subscription, HashSet<RespireEndpoint> touched)
    {
        foreach (var endpoint in touched)
        {
            if (!_notificationNodes.TryGetValue(endpoint, out var node)) continue;
            bool empty;
            lock (_gate)
            {
                foreach (var name in subscription.Names)
                {
                    var routes = node.Routes[(int)subscription.Kind];
                    if (!routes.TryGetValue(name, out var consumers)) continue;
                    consumers.Remove(subscription);
                    if (consumers.Count == 0) routes.Remove(name);
                }
                if (_notificationCoverage.TryGetValue(subscription, out var coverage))
                {
                    coverage.Remove(endpoint);
                    if (coverage.Count == 0) _notificationCoverage.Remove(subscription);
                }
                empty = node.Routes.All(static routes => !routes.Names.Any());
                if (empty)
                {
                    node.Retired = true;
                    Interlocked.Increment(ref node.Epoch);
                    _notificationNodes.Remove(endpoint);
                }
            }
            if (node.Connection is { } connection)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { core.Logger?.LogDebug(error, "Closing a rolled back cluster notification connection failed"); }
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
            primaries = primarySnapshot ?? await GetCurrentPrimaryEndpointsAsync(cluster, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<RespireEndpoint[]> GetCurrentPrimaryEndpointsAsync(
        ClusterRouter cluster, CancellationToken cancellationToken)
    {
        var masters = await cluster.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
        return masters.Select(static connection => new RespireEndpoint(connection.Host, connection.Port))
            .Distinct().ToArray();
    }

    private async ValueTask<ClusterNotificationNode> EnsureNotificationNodeAsync(
        RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (_notificationNodes.TryGetValue(endpoint, out var existing)
            && existing.Connection is { IsConnected: true }) return existing;

        var node = existing ?? new ClusterNotificationNode(endpoint);
        node.Retired = false;
        var epoch = Volatile.Read(ref node.Epoch) + 1;
        var options = core.Options.ToConnectionOptions(
            (in RespValue value) => OnNotificationPush(node, epoch, in value)) with
        {
            SubscriptionConfirmationHandler = (in RespValue _) => { },
        };
        using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token);
        var connection = await RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port, options,
            core.Logger, connectCancellation.Token).ConfigureAwait(false);
        Interlocked.Exchange(ref node.Epoch, epoch);
        node.Connection = connection;
        _notificationNodes[endpoint] = node;
        _ = WatchNotificationNodeAsync(node, connection, epoch);
        try
        {
            foreach (var (kind, name) in SnapshotNotificationRoutes(node))
                await SendControlAsync(connection, SubscribeVerb(kind), SubscribeOperation(kind), name,
                    cancellationToken, instrument: false).ConfigureAwait(false);
        }
        catch
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { core.Logger?.LogDebug(error, "Closing a failed cluster notification replacement failed"); }
            throw;
        }
        bool recovered;
        lock (_gate) recovered = node.InterruptedAt is not null;
        PublishNotificationReconnectGaps(node);
        if (recovered)
            core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                node.Endpoint, RespireConnectionState.Connected, null));
        return node;
    }

    private void OnNotificationPush(ClusterNotificationNode node, long epoch, in RespValue value)
    {
        if (Volatile.Read(ref node.Epoch) != epoch) return;
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
        lock (_gate)
        {
            if (_disposed || Volatile.Read(ref node.Epoch) != epoch || node.Retired
                || !node.Routes[(int)kind].TryGetValue(routeName, out var cachedName, out var targets)) return;
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

    private async Task WatchNotificationNodeAsync(
        ClusterNotificationNode node, RespireConnection connection, long epoch)
    {
        await connection.Closed.ConfigureAwait(false);
        if (_disposed || node.Retired || Volatile.Read(ref node.Epoch) != epoch) return;
        lock (_gate)
            if (node.InterruptedAt is null && node.Routes.Any(static routes => routes.Names.Any()))
                node.InterruptedAt = DateTimeOffset.UtcNow;
        core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
            node.Endpoint, RespireConnectionState.Reconnecting, connection.CloseError));
        var delay = TimeSpan.FromMilliseconds(250);
        var attempt = 0;
        while (!_disposed && !node.Retired)
        {
            try
            {
                if (attempt < int.MaxValue) attempt++;
                var nextDelay = core.Options.ReconnectPolicy?.GetDelay(attempt) ?? delay;
                RespireTelemetry.RecordReconnectAttempt(node.Endpoint.Host, node.Endpoint.Port,
                    attempt, nextDelay, RespireReconnectSource.PubSub);
                core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                    node.Endpoint, RespireConnectionState.Reconnecting, connection.CloseError)
                {
                    ReconnectAttempt = attempt,
                    NextReconnectDelay = nextDelay,
                });
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
                core.Logger?.LogWarning(error, "Cluster notification reconnect failed for {Host}:{Port}; retrying in {Delay}",
                    node.Endpoint.Host, node.Endpoint.Port, delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
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
            HashSet<RespireSubscription> subscriptions = [];
            foreach (var routes in node.Routes)
                foreach (var targets in routes.Values)
                    subscriptions.UnionWith(targets);
            foreach (var subscription in subscriptions)
            {
                var gap = new RespireSubscriptionGap(RespireSubscriptionGapReason.Reconnect, started, ended);
                if (subscription.Buffer.WriteGap(gap)) gaps.Add((subscription, gap));
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
        ClusterNotificationNode node, RespireSubscription subscription)
    {
        List<(SubscriptionKind Kind, RespireChannel Name)> released = [];
        lock (_gate)
        {
            foreach (var name in subscription.Names)
            {
                var routes = node.Routes[(int)subscription.Kind];
                if (!routes.TryGetValue(name, out var consumers)) continue;
                consumers.Remove(subscription);
                if (consumers.Count == 0)
                {
                    routes.Remove(name);
                    released.Add((subscription.Kind, name));
                }
            }
            if (_notificationCoverage.TryGetValue(subscription, out var coverage))
            {
                coverage.Remove(node.Endpoint);
                if (coverage.Count == 0) _notificationCoverage.Remove(subscription);
            }
        }
        if (node.Connection is { IsConnected: true } connection)
        {
            foreach (var (kind, name) in released)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(
                        core.Options.CommandTimeout ?? core.Options.ConnectTimeout);
                    await SendControlAsync(connection, UnsubscribeVerb(kind), UnsubscribeOperation(kind), name,
                        timeout.Token, instrument: true).ConfigureAwait(false);
                }
                catch (Exception error) when (error is RespireException or OperationCanceledException)
                {
                    core.Logger?.LogDebug(error, "Cluster notification unsubscribe failed for {Host}:{Port}",
                        node.Endpoint.Host, node.Endpoint.Port);
                    try { await connection.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception closeError) { core.Logger?.LogDebug(closeError, "Closing an uncertain cluster notification connection failed"); }
                    break;
                }
            }
        }
        bool empty;
        lock (_gate) empty = node.Routes.All(static routes => !routes.Names.Any());
        if (empty && _notificationNodes.Remove(node.Endpoint))
        {
            node.Retired = true;
            Interlocked.Increment(ref node.Epoch);
            core.NotifyClusterSubscriptionStateChanged(new RespireConnectionStateChange(
                node.Endpoint, RespireConnectionState.Connected, null));
            if (node.Connection is { } remainingConnection) await remainingConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void NotifyTopologyChanged(long version, RespireEndpoint[] endpoints)
    {
        if (_disposed) return;
        while (true)
        {
            var current = Volatile.Read(ref _notificationTopologyVersion);
            if (version <= current) return;
            if (Interlocked.CompareExchange(ref _notificationTopologyVersion, version, current) == current) break;
        }
        _ = ReconcileNotificationsAsync(version, endpoints);
    }

    private async Task ReconcileNotificationsAsync(long version, RespireEndpoint[] endpoints, int attempt = 0)
    {
        try
        {
            await _controlGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || version != Volatile.Read(ref _notificationTopologyVersion)) return;
                RespireSubscription[] subscriptions;
                lock (_gate) subscriptions = [.. _notificationCoverage.Keys];
                foreach (var subscription in subscriptions)
                {
                    if (version != Volatile.Read(ref _notificationTopologyVersion)) return;
                    var desired = await GetNotificationCoverageAsync(subscription, _lifetimeCancellation.Token, endpoints).ConfigureAwait(false);
                    if (version != Volatile.Read(ref _notificationTopologyVersion)) return;
                    var current = _notificationCoverage.TryGetValue(subscription, out var coverage)
                        ? new HashSet<RespireEndpoint>(coverage) : [];
                    foreach (var endpoint in desired.Keys.Except(current))
                    {
                        try
                        {
                            var node = await EnsureNotificationNodeAsync(endpoint, _lifetimeCancellation.Token).ConfigureAwait(false);
                            if (version != Volatile.Read(ref _notificationTopologyVersion))
                            {
                                await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false);
                                return;
                            }
                            foreach (var name in desired[endpoint])
                            {
                                bool subscribe;
                                lock (_gate)
                                {
                                    var routes = node.Routes[(int)subscription.Kind];
                                    subscribe = !routes.TryGetValue(name, out var consumers);
                                    if (subscribe) routes.Add(name.WithoutNotificationMetadata(), consumers = []);
                                    if (!consumers.Contains(subscription)) consumers.Add(subscription);
                                    if (!_notificationCoverage.TryGetValue(subscription, out var set))
                                        _notificationCoverage.Add(subscription, set = []);
                                    set.Add(endpoint);
                                }
                                if (subscribe)
                                    await SendControlAsync(node.Connection!, SubscribeVerb(subscription.Kind),
                                        SubscribeOperation(subscription.Kind), name, _lifetimeCancellation.Token, instrument: true)
                                        .ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            await RollbackNotificationActivationAsync(subscription, [endpoint]).ConfigureAwait(false);
                            throw;
                        }
                    }
                    foreach (var endpoint in current.Except(desired.Keys).ToArray())
                    {
                        if (version != Volatile.Read(ref _notificationTopologyVersion)) return;
                        if (_notificationNodes.TryGetValue(endpoint, out var node))
                            await ReleaseNotificationRoutesAsync(node, subscription).ConfigureAwait(false);
                    }
                }
            }
            finally { _controlGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            core.Logger?.LogWarning(error, "Cluster notification topology reconciliation failed");
            if (!_disposed && version == Volatile.Read(ref _notificationTopologyVersion))
                _ = RetryNotificationReconciliationAsync(version, endpoints, attempt: 1);
        }
    }

    private async Task RetryNotificationReconciliationAsync(
        long version, RespireEndpoint[] endpoints, int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(250 * Math.Pow(2, Math.Min(attempt - 1, 5)), 5000));
        try { await Task.Delay(delay, _recoveryClock, _lifetimeCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
        if (!_disposed && version == Volatile.Read(ref _notificationTopologyVersion))
            await ReconcileNotificationsAsync(version, endpoints, attempt + 1).ConfigureAwait(false);
    }
}
