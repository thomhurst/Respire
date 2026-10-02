using System.Net;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Discovery publishes an entire validated generation. Old generations remain owned until
// their accepted commands, borrowed leases, and correction fences finish or disposal aborts them.
// Lifecycle: discovery owns an unpublished candidate; publication makes it Current;
// Invalidate retires admission and starts draining; RemoveOwnedLocked ends ownership and
// balances the retired-generation gauge. Failed candidates skip publication and drain counting.
// Router disposal can end any phase. A replacement may be current while older generations drain,
// so these transitions belong to each generation rather than one router-wide state enum.
internal sealed class SentinelRouter(ClientCore core) : IAsyncDisposable
{
    private static long _retiredGenerationCount;
    internal static long RetiredGenerationCount => Interlocked.Read(ref _retiredGenerationCount);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SentinelDiscoveryState _discovery = new(core.Options.Endpoints.Count == 0
        ? [new RespireEndpoint("localhost", 26379)] : core.Options.Endpoints);
    private readonly HashSet<Generation> _owned = [];
    private readonly HashSet<DedicatedConnectionPool> _correctionPools = [];
    // Keyed like discovery itself. Discovery never forgets an endpoint, so entries are never removed.
    private readonly Dictionary<RespireEndpoint, Task> _notificationMonitors = new(SentinelDiscoveryState.EndpointComparer.Instance);
    private readonly SentinelNotificationCoalescer _coalescer = new(); // Guarded by _gate.
    private readonly byte[] _serviceNameUtf8 = System.Text.Encoding.UTF8.GetBytes(core.Options.SentinelPrimaryName ?? "");
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private Task _notifications = Task.CompletedTask;
    private Task _notificationMonitorSupervisor = Task.CompletedTask;
    private Task? _notificationRediscovery;
    // Background DNS checks for +switch-master sources. Disposal joins them with the monitors.
    private readonly HashSet<Task> _switchSourceResolutions = []; // Guarded by _gate.
    private int _queuedNotifications;
    private int _successfulMonitorSubscriptions;
    // Distinct Sentinels whose monitor has subscribed at least once. Reconnects do not add to it.
    private readonly HashSet<RespireEndpoint> _subscribedSentinels = new(SentinelDiscoveryState.EndpointComparer.Instance); // Guarded by _gate.
    // Completed and replaced on each publication. Monitors parked after exhausting their reconnect
    // budget wait on it, because a published generation proves Sentinel discovery works again.
    private TaskCompletionSource _monitorRearm = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _masterDownKey = "master-down:" + core.Options.SentinelPrimaryName;

    internal Generation? Current => Volatile.Read(ref _current);
    internal RespireEndpoint[] DiscoveredEndpoints => _discovery.Snapshot();
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Queries the configured and learned Sentinels directly, so replica reads do not depend on a
    // reachable primary during an outage or failover window.
    internal ValueTask<RespireEndpoint[]> DiscoverReplicaEndpointsAsync(CancellationToken cancellationToken)
        => SentinelResolver.DiscoverReplicaEndpointsAsync(core.Options, _discovery.Snapshot(), cancellationToken);
    internal bool IsConnected => Current is { IsRetired: false } generation && generation.Multiplexer.IsConnected;
    /// <summary>Counts failover hints passed to rediscovery coalescing. Tests use it to order events.</summary>
    internal int QueuedNotificationCount => Volatile.Read(ref _queuedNotifications);
    internal int SuccessfulMonitorSubscriptions => Volatile.Read(ref _successfulMonitorSubscriptions);
    /// <summary>Counts distinct Sentinels with an established monitor subscription. Tests use it as readiness.</summary>
    internal int SubscribedSentinelCount
    {
        get { lock (_gate) return _subscribedSentinels.Count; }
    }
    /// <summary>Resolves a switch source host name. Tests replace it to hold resolution open.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> HostResolver { get; set; } = Dns.GetHostAddressesAsync;
    internal int PendingSwitchSourceResolutions
    {
        get { lock (_gate) return _switchSourceResolutions.Count; }
    }
    internal Task? NotificationRediscovery
    {
        get { lock (_gate) return _notificationRediscovery; }
    }

    internal sealed class CorrectionLease(SentinelRouter owner, DedicatedConnectionPool pool) : IAsyncDisposable
    {
        private int _disposed;
        internal DedicatedConnectionPool Pool => pool;
        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseCorrectionAsync(pool) : default;
    }

    internal CorrectionLease GetCorrectionLease(RespireConnection original)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = (original.Multiplexer?.Options ?? core.Options.ToConnectionOptions()) with
            {
                Generation = null, EnableClientTracking = false, PushHandler = null, SubscriptionConfirmationHandler = null,
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            };
            if (options.UseTls)
                options = options with { TlsOptions = RespireConnection.CreateTlsOptions(options.TlsOptions, original.Host) };
            // Corrections remain on the original physical peer, even after DNS or Sentinel moves.
            var pool = new DedicatedConnectionPool(original.NetworkPeerAddress ?? original.Host,
                original.NetworkPeerPort ?? original.Port, options, core.Logger);
            _correctionPools.Add(pool);
            return new(this, pool);
        }
    }

    private async ValueTask ReleaseCorrectionAsync(DedicatedConnectionPool pool)
    {
        await pool.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _correctionPools.Remove(pool);
    }

    internal async ValueTask<Generation> GetGenerationAsync(CancellationToken cancellationToken, bool forceDiscovery = false,
        SentinelHint? notificationHint = null)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        // An unexpected close retires a Sentinel generation, even when that multiplexer
        // could reconnect. Reconnecting the former primary alone cannot establish that it
        // is still the elected primary; discovery and ROLE validation select a new generation.
        if (!forceDiscovery && Current is { IsRetired: false } current && current.Multiplexer.IsConnected) return current;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        Generation? unpublished = null;
        try
        {
            await _discoveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            // Another discovery owner may have published while this caller awaited the gate.
            var previous = Current;
            if (!forceDiscovery && previous is { IsRetired: false } && previous.Multiplexer.IsConnected) return previous;
            if (!forceDiscovery && previous is not null) Invalidate(previous);
            // A forced discovery that resolves to the healthy current primary confirms it with ROLE
            // on the existing connection instead of opening and discarding a candidate generation.
            Func<RespireOptions, CancellationToken, ValueTask<Generation>> connect = forceDiscovery && previous is not null
                ? (options, token) => ReuseOrConnectGenerationAsync(previous, options, token)
                : ConnectGenerationAsync;
            var replacement = await SentinelResolver.ResolveAndConnectPrimaryAsync(
                core.Options, connect, linked.Token, _discovery,
                notificationHint?.ReportingSentinel).ConfigureAwait(false);
            if (ReferenceEquals(replacement, previous))
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (replacement.IsRetired || !ReferenceEquals(Current, replacement))
                        throw new RespireConnectionException("Sentinel primary changed while it was being revalidated.");
                }
                return replacement;
            }
            unpublished = replacement;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                if (replacement.IsRetired)
                    throw new RespireConnectionException("Sentinel primary changed before its generation was published.");
                var old = Current;
                if (forceDiscovery && old is { IsRetired: false } && old.Multiplexer.IsConnected
                    && SameEndpoint(old.Endpoint, replacement.Endpoint))
                    return old;
                if (old is not null) Invalidate(old);
                Volatile.Write(ref _current, replacement);
                unpublished = null;
                // Publication owns this measurement even if disposal suppresses later health
                // callbacks. Keep meter listeners outside discovery to permit observer disposal.
                if (old is not null && old.Endpoint != replacement.Endpoint)
                    QueueNotificationLocked(() => RespireTelemetry.SentinelFailovers.Add(1,
                        new KeyValuePair<string, object?>("server.address", replacement.Endpoint.Host),
                        new KeyValuePair<string, object?>("server.port", replacement.Endpoint.Port)), suppressAfterDisposal: false);
                QueueNotificationLocked(() => core.NotifySentinelPrimaryChanged(old?.Multiplexer, replacement.Multiplexer));
                StartNotificationMonitoringLocked();
                var rearm = _monitorRearm;
                _monitorRearm = new(TaskCreationOptions.RunContinuationsAsynchronously);
                rearm.TrySetResult();
            }
            return replacement;
        }
        catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
            error, cancellationToken, linked.Token))
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        finally
        {
            try
            {
                if (unpublished is not null)
                {
                    await unpublished.DisposeAsync().ConfigureAwait(false);
                    lock (_gate) RemoveOwnedLocked(unpublished);
                }
            }
            finally { if (acquired) _discoveryGate.Release(); }
        }
    }

    /// <summary>
    /// Ensures a <c>ROLE</c>-validated primary is current. Checks the published primary with <c>ROLE</c>.
    /// On a mismatch, or when the probed generation was retired while answering, retires only that
    /// generation and rediscovers; discovery validates the replacement with <c>ROLE</c> before publishing it.
    /// Completes when a validated primary is current; otherwise throws, and discovery failures propagate.
    /// </summary>
    internal async ValueTask EnsureValidatedPrimaryAsync(CancellationToken cancellationToken)
    {
        var generation = await GetGenerationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasPrimaryRoleAsync(generation, cancellationToken).ConfigureAwait(false)) return;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && generation.IsRetired)
        {
            // Every failure on a generation that was retired while ROLE was in flight means "not the
            // current primary". A ROLE mismatch seen by application traffic retires the generation
            // before this resumes, and a socket or timeout fault on a retired generation needs the same
            // rediscovery. A failed rediscovery below becomes the probe error, so keep this cause in the log.
            try { core.Logger?.LogDebug(error, "Sentinel primary probe failed on a retired generation; rediscovering"); }
            catch { /* Logging must not stop health probes. */ }
        }

        // Application traffic may already have published a replacement; never retire that one.
        Invalidate(generation);
        await GetGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasPrimaryRoleAsync(Generation generation, CancellationToken cancellationToken)
    {
        using var role = await generation.Multiplexer.GetConnection()
            .SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
        return Generation.IsPrimary(in role);
    }

    private async ValueTask<Generation> ReuseOrConnectGenerationAsync(Generation current, RespireOptions options,
        CancellationToken cancellationToken)
    {
        if (current.IsRetired || !current.Multiplexer.IsConnected || !SameEndpoint(current.Endpoint, options.PrimaryEndpoint))
            return await ConnectGenerationAsync(options, cancellationToken).ConfigureAwait(false);
        // ObserveResponse retires the generation when ROLE no longer reports a primary.
        using var reply = await current.Multiplexer.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
        if (!Generation.IsPrimary(in reply))
        {
            Invalidate(current);
            throw new RespireConnectionException($"Sentinel primary at {current.Endpoint} no longer confirms a primary ROLE.");
        }
        return current;
    }

    // The supervisor wakes when discovery learns a Sentinel. Monitors complete only when the
    // client is disposed, so this fallback interval only restarts one that faulted unexpectedly.
    private static readonly TimeSpan SentinelMonitorSupervisorFallbackInterval = TimeSpan.FromSeconds(30);
    // Disposal stops waiting for monitor, supervisor and rediscovery tasks after this bound and
    // logs the stragglers. Publication and retirement recheck disposal under the gate.
    private static readonly TimeSpan NotificationShutdownTimeout = TimeSpan.FromSeconds(10);
    private const string SentinelMonitorReconnectScope = "sentinel-monitor";
    private const string DeliveryGapKey = "gap";

    private static TimeSpan GetNotificationRetryDelay(RespireReconnectPolicy? policy, int attempt)
        => policy?.GetDelay(attempt) ?? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));

    // Monitoring starts after the first validated publication. Until then every command runs
    // discovery itself, and a client whose initial discovery keeps failing has no primary to move.
    private void StartNotificationMonitoringLocked()
    {
        if (_notificationMonitorSupervisor != Task.CompletedTask) return;
        _notificationMonitorSupervisor = Task.Run(MonitorSentinelsAsync);
    }

    private async Task MonitorSentinelsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var endpoints = _discovery.Snapshot(out var changed);
                lock (_gate)
                {
                    if (_disposed) return;
                    foreach (var endpoint in endpoints)
                    {
                        if (_notificationMonitors.TryGetValue(endpoint, out var monitor) && !monitor.IsCompleted) continue;
                        _notificationMonitors[endpoint] = Task.Run(() => MonitorSentinelAsync(endpoint, _lifetime.Token));
                    }
                }
                // Wake as soon as discovery learns a Sentinel; no periodic polling of the endpoint set.
                try
                {
                    await changed.WaitAsync(SentinelMonitorSupervisorFallbackInterval, Clock, _lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException) { }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task MonitorSentinelAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        var attempt = 0;
        var subscribedBefore = false;
        // Captured when a reconnect episode starts, not when the monitor parks: a publication
        // that lands while the last attempts are failing must still grant the fresh budget.
        Task rearm = CurrentMonitorRearm();
        while (!cancellationToken.IsCancellationRequested)
        {
            var subscriptionReconnectExhausted = false;
            RespireClient? client = null;
            RespireSubscription? subscription = null;
            try
            {
                client = RespireClient.Create(CreateSentinelMonitorOptions(core.Options, endpoint,
                    () => Volatile.Write(ref rearm, CurrentMonitorRearm())));
                subscription = await client.SubscribeAsync(
                    ["+switch-master", "+sdown", "+odown"], cancellationToken).ConfigureAwait(false);
                attempt = 0;
                Volatile.Write(ref rearm, RefreshMonitorRearm(Volatile.Read(ref rearm)));
                // The first subscription follows initial discovery; reconnects can miss events
                // while disconnected. Revalidate after either subscription is established.
                QueueDeliveryGapRediscovery(endpoint, initialSubscription: !subscribedBefore);
                Interlocked.Increment(ref _successfulMonitorSubscriptions);
                if (!subscribedBefore)
                    lock (_gate) _subscribedSentinels.Add(endpoint);
                subscribedBefore = true;
                await foreach (var message in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
                    await ObserveSentinelNotificationAsync(endpoint, message, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    subscriptionReconnectExhausted = await subscription.Completion.ConfigureAwait(false)
                        == RespireSubscriptionEndReason.ReconnectExhausted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                SafeLog((error, endpoint), static (logger, state)
                    => logger.LogWarning(state.error, "Sentinel event monitor failed at {Endpoint}", state.endpoint));
            }
            finally
            {
                // The single disposal path. On shutdown, close the client first so the subscription's
                // UNSUBSCRIBE cannot wait on a live socket; otherwise unsubscribe before closing.
                var shutdown = cancellationToken.IsCancellationRequested;
                await DisposeMonitorResourceAsync(shutdown ? client : subscription, endpoint).ConfigureAwait(false);
                await DisposeMonitorResourceAsync(shutdown ? subscription : client, endpoint).ConfigureAwait(false);
            }
            if (cancellationToken.IsCancellationRequested) return;

            // MaxAttempts counts replacement attempts, as on the other reconnect paths, so the
            // initial subscription failure still receives a retry.
            var policy = core.Options.ReconnectPolicy;
            if (subscriptionReconnectExhausted && policy?.MaxAttempts is { } exhaustedAt)
                attempt = exhaustedAt;
            if (policy?.IsExhausted(attempt) == true)
            {
                // Surface the lost fast path: respire.connection.reconnect.exhausted with
                // respire.reconnect.scope=sentinel-monitor, plus a warning.
                if (!subscriptionReconnectExhausted)
                    RespireTelemetry.RecordDiscoveryReconnect(endpoint, SentinelMonitorReconnectScope, attempt, null, core.Logger);
                SafeLog(endpoint, static (logger, endpoint) => logger.LogWarning(
                    "Sentinel event monitor exhausted reconnect attempts at {Endpoint}; failover events from this "
                    + "Sentinel are not observed until a new primary is published, and discovery runs on demand", endpoint));
                // Stay parked rather than completing: the supervisor restarts completed monitors,
                // which would bypass the configured budget. Discovery still runs on demand, and the
                // next validated publication grants a fresh budget, so one long outage does not
                // remove this Sentinel's fast path for the life of the client.
                // The episode's rearm task is already complete when a publication happened while
                // the final attempts were failing, so the monitor resumes without a second one.
                try { await Volatile.Read(ref rearm).WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                SafeLog(endpoint, static (logger, endpoint) => logger.LogInformation(
                    "Sentinel event monitor at {Endpoint} resumes after a new primary was published", endpoint));
                attempt = 0;
                rearm = CurrentMonitorRearm();
                continue;
            }
            attempt++;
            var delay = GetNotificationRetryDelay(policy, attempt);
            RespireTelemetry.RecordDiscoveryReconnect(endpoint, SentinelMonitorReconnectScope, attempt, delay, core.Logger);
            try { await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private Task RefreshMonitorRearm(Task reconnectEpoch)
    {
        lock (_gate)
        {
            // A publication may have completed the reconnect epoch while SubscribeAsync was
            // returning. Keep that signal so exhaustion cannot park until another publication.
            return reconnectEpoch.IsCompleted ? reconnectEpoch : _monitorRearm.Task;
        }
    }

    private Task CurrentMonitorRearm()
    {
        lock (_gate) return _monitorRearm.Task;
    }

    private async ValueTask DisposeMonitorResourceAsync(IAsyncDisposable? resource, RespireEndpoint endpoint)
    {
        if (resource is null) return;
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            SafeLog((error, endpoint), static (logger, state)
                => logger.LogDebug(state.error, "Sentinel event monitor cleanup failed at {Endpoint}", state.endpoint));
        }
    }

    internal static RespireOptions CreateSentinelMonitorOptions(RespireOptions options, RespireEndpoint endpoint,
        Action? reconnectEpisodeStarted = null)
    {
        var authDisabled = options.SentinelPassword is { Length: 0 };
        var useSeparateCredentials = options.SentinelUsername is not null || options.SentinelPassword is not null;
        return options with
        {
            Endpoints = [endpoint],
            UseCluster = false,
            SentinelPrimaryName = null,
            ReplicaEndpoints = [],
            ReadFrom = RespireReadFrom.Primary,
            Username = authDisabled ? null : options.SentinelUsername ?? options.Username,
            Password = authDisabled ? null : options.SentinelPassword ?? options.Password,
            CredentialProvider = authDisabled ? null : options.SentinelCredentialProvider
                ?? (useSeparateCredentials ? null : options.CredentialProvider),
            SentinelUsername = null,
            SentinelPassword = null,
            SentinelCredentialProvider = null,
            SentinelUseTls = null,
            SentinelTlsOptions = null,
            UseTls = options.SentinelUseTls ?? options.UseTls,
            TlsOptions = options.SentinelTlsOptions ?? options.TlsOptions,
            ClientName = null,
            Database = 0,
            Protocol = !authDisabled && (options.SentinelCredentialProvider is not null
                || (!useSeparateCredentials && options.CredentialProvider is not null)
                ) ? RespProtocol.Resp3 : RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            ReconnectTelemetryScope = SentinelMonitorReconnectScope,
            ReconnectEpisodeStarted = reconnectEpisodeStarted,
            ClientSideCache = null,
            ThreadPoolMonitoring = false,
            Connections = 1,
        };
    }

    private ValueTask ObserveSentinelNotificationAsync(RespireEndpoint sentinel, RespireMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Kind == RespireMessageKind.Gap)
        {
            QueueDeliveryGapRediscovery(sentinel);
            return ValueTask.CompletedTask;
        }
        if (message.Kind != RespireMessageKind.Message || _serviceNameUtf8.Length == 0)
            return ValueTask.CompletedTask;
        // Filter by channel and service on the raw bytes, so events for other masters do not allocate.
        var sentinelEvent = SentinelEvent.Parse(message.Channel.Span, message.Payload.Span, _serviceNameUtf8);
        switch (sentinelEvent.Kind)
        {
            case SentinelEventKind.ReplicaDown:
                // Replica events never move the primary; keep them out of Information logs.
                LogSentinelEvent(LogLevel.Debug, message, sentinel);
                return ValueTask.CompletedTask;
            case SentinelEventKind.MasterDown:
                LogSentinelEvent(LogLevel.Information, message, sentinel);
                // +odown text carries changing quorum counts; key master-down hints by service so
                // repeated reports of one outage coalesce while discovery is active.
                QueueNotificationRediscovery(new SentinelHint(_masterDownKey, MustRediscover: true));
                return ValueTask.CompletedTask;
            case SentinelEventKind.SwitchMaster:
                LogSentinelEvent(LogLevel.Information, message, sentinel);
                // Queue immediately so slow DNS cannot hold up later one-shot notifications.
                // Every Sentinel in the quorum announces the same parsed switch, so key on it.
                var hint = new SentinelHint(sentinelEvent is { OldPrimary: { } from, NewPrimary: { } to }
                        ? $"+switch-master:{from.Host}:{from.Port}>{to.Host}:{to.Port}"
                        : "+switch-master:" + message.Text,
                    sentinelEvent.NewPrimary, sentinelEvent.OldPrimary,
                    MustRediscover: sentinelEvent.NewPrimary is null,
                    ReportingSentinel: sentinel);
                // Only the generation current when the event arrived can be its source. A later
                // failover back to the same endpoint publishes a new generation that must survive.
                var arrivedDuring = Current;
                QueueNotificationRediscovery(in hint);
                if (sentinelEvent.OldPrimary is { } source && arrivedDuring is not null
                    && !SameEndpoint(arrivedDuring.Endpoint, source))
                    StartSwitchSourceResolution(hint, arrivedDuring, cancellationToken);
                return ValueTask.CompletedTask;
        }
        return ValueTask.CompletedTask;
    }

    private async Task ResolveAndRetireSwitchSourceAsync(SentinelHint hint, Generation arrivedDuring,
        CancellationToken cancellationToken)
    {
        try
        {
            var oldPrimary = hint.OldPrimary!.Value;
            var addresses = await ResolveAddressesAsync(oldPrimary.Host, cancellationToken).ConfigureAwait(false);
            if (addresses is null) return;

            lock (_gate)
            {
                if (_disposed) return;
                _coalescer.RetainResolvedOldPrimaryAddresses(oldPrimary, addresses);
                var current = Current;
                // Do not apply an old resolution to a later generation for the same endpoint:
                // a failback can legitimately publish that address again. A changed endpoint
                // is checked so a hostname alias is not lost across an in-flight handoff.
                if (current is null || !ReferenceEquals(current, arrivedDuring)
                    && SameEndpoint(current.Endpoint, arrivedDuring.Endpoint)) return;
                if (!IsAnnouncedTarget(current, in hint)
                    && IsSwitchSource(current, hint with { OldPrimaryAddresses = addresses }))
                    Invalidate(current!);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            SafeLog(error, static (logger, error)
                => logger.LogDebug(error, "Could not retire the primary named by a Sentinel switch event"));
        }
    }

    // Starts and registers the resolution in one step under the gate. Disposal sets _disposed before
    // it snapshots this set under the same gate, so every started resolution is either joined by
    // disposal or never started. Task.Run keeps the resolver from running while the gate is held.
    private void StartSwitchSourceResolution(SentinelHint hint, Generation arrivedDuring, CancellationToken cancellationToken)
    {
        Task resolution;
        lock (_gate)
        {
            if (_disposed) return;
            resolution = Task.Run(() => ResolveAndRetireSwitchSourceAsync(hint, arrivedDuring, cancellationToken), CancellationToken.None);
            _switchSourceResolutions.Add(resolution);
        }
        _ = resolution.ContinueWith(static (completed, state) =>
        {
            var router = (SentinelRouter)state!;
            lock (router._gate) router._switchSourceResolutions.Remove(completed);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Run address resolution after queueing the one-shot notification, so DNS cannot hold up
    // failover discovery or prevent this monitor from reading later events.
    private async ValueTask<string[]?> ResolveAddressesAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return [NormalizeAddress(literal)];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(core.Options.ConnectTimeout);
            var addresses = await HostResolver(host, timeout.Token).ConfigureAwait(false);
            return Array.ConvertAll(addresses, NormalizeAddress);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            SafeLog((error, host), static (logger, state)
                => logger.LogDebug(state.error, "Could not resolve Sentinel switch source {Host}", state.host));
            return null;
        }
    }

    private static string NormalizeAddress(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    // Logging is diagnostic only: a failing user logger must never stop monitoring, rediscovery or disposal.
    private void SafeLog<TState>(TState state, Action<ILogger, TState> log)
    {
        if (core.Logger is not { } logger) return;
        try { log(logger, state); }
        catch (Exception) { }
    }

    private void LogSentinelEvent(LogLevel level, in RespireMessage message, RespireEndpoint sentinel)
    {
        try
        {
            if (core.Logger?.IsEnabled(level) == true)
                core.Logger.Log(level, "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}",
                    message.Channel.ToString(), core.Options.SentinelPrimaryName, sentinel, message.Text);
        }
        catch (Exception) { }
    }

    private void QueueDeliveryGapRediscovery(RespireEndpoint sentinel, bool initialSubscription = false)
    {
        SafeLog((sentinel, initialSubscription), static (logger, state) => logger.LogInformation(state.initialSubscription
            ? "Sentinel monitor established at {Sentinel}; revalidating the primary after subscription"
            : "Sentinel event delivery from {Sentinel} had a gap; rediscovering the primary", state.sentinel));
        // Untargeted and never satisfied by an earlier attempt: a missed switch could leave the
        // former primary serving reads as a replica without a disconnect or READONLY reply.
        QueueNotificationRediscovery(new SentinelHint(DeliveryGapKey, MustRediscover: true,
            ReportingSentinel: sentinel));
    }

    internal void QueueNotificationRediscovery(in SentinelHint hint)
    {
        try
        {
            QueueNotificationRediscoveryCore(in hint);
        }
        finally
        {
            Interlocked.Increment(ref _queuedNotifications);
        }
    }

    private void QueueNotificationRediscoveryCore(in SentinelHint hint)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var current = Current;
            var targetIsCurrent = hint.Target is { } target && current is { IsRetired: false }
                && SameEndpoint(current.Endpoint, target);
            var startWorker = _coalescer.Offer(in hint, targetIsCurrent);
            // Compare the switch source with Current under the gate, immediately before retirement.
            // This also covers hints that wait behind an active discovery, so a direct endpoint
            // match never waits for that attempt or for DNS.
            if (!IsAnnouncedTarget(current, in hint) && IsSwitchSource(current, in hint)) Invalidate(current!);
            if (startWorker) _notificationRediscovery = Task.Run(RediscoverFromNotificationAsync);
        }
    }

    private async Task RediscoverFromNotificationAsync()
    {
        var failures = 0;
        var failedSwitchTargets = new HashSet<string>(StringComparer.Ordinal);
        // Consecutive failed attempts. Only the first of a run logs a warning, so a long Sentinel
        // outage without a ReconnectPolicy does not repeat it every 30 seconds.
        var consecutiveFailures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            var succeeded = false;
            var retryDelay = TimeSpan.Zero;
            try
            {
                SentinelHint? hint;
                lock (_gate) hint = _coalescer.Active;
                if (hint is { Target: not null } targetedHint && failedSwitchTargets.Contains(targetedHint.Key))
                    hint = targetedHint with { Target = null };
                await GetGenerationAsync(_lifetime.Token, forceDiscovery: true, notificationHint: hint).ConfigureAwait(false);
                succeeded = true;
                if (consecutiveFailures > 0)
                    SafeLog(consecutiveFailures, static (logger, count) => logger.LogInformation(
                        "Sentinel notification-triggered primary discovery succeeded after {Failures} failed attempt(s)", count));
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (_coalescer.Active is { Target: not null } failedHint)
                        failedSwitchTargets.Add(failedHint.Key);
                }
                SafeLog((error, attempt: ++consecutiveFailures), static (logger, state) => logger.Log(
                    state.attempt == 1 ? LogLevel.Warning : LogLevel.Debug, state.error,
                    "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})", state.attempt));
            }

            lock (_gate)
            {
                if (_disposed) return;
                if (_coalescer.TakePending(activeFailed: !succeeded) is not { } next)
                {
                    // Sentinel publishes each event at most once. Retry a failed hint with backoff,
                    // because a switch may already have retired the current generation. Without a
                    // ReconnectPolicy this retries until discovery succeeds or the client is disposed,
                    // at the default backoff capped at 30 seconds: dropping the hint could leave a
                    // retired generation with no event-driven replacement. A policy's MaxAttempts
                    // bounds it; commands still run discovery on demand after that.
                    var policy = core.Options.ReconnectPolicy;
                    if (succeeded || policy?.IsExhausted(failures) == true)
                    {
                        _coalescer.Complete();
                        _notificationRediscovery = null;
                        return;
                    }
                    retryDelay = GetNotificationRetryDelay(policy, ++failures);
                }
                else
                {
                    // A newer hint restarts discovery at once, but only success resets the backoff.
                    if (succeeded) failures = 0;
                    var current = Current;
                    if (!next.MustRediscover && next.Target is { } target && current is { IsRetired: false }
                        && SameEndpoint(current.Endpoint, target))
                    {
                        _coalescer.Complete();
                        _notificationRediscovery = null;
                        return;
                    }
                    if (!IsAnnouncedTarget(current, in next) && IsSwitchSource(current, in next)) Invalidate(current!);
                }
            }

            if (retryDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(retryDelay, Clock, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            }
        }
    }

    // Whether a switch hint's old primary is the healthy current generation, by announced
    // endpoint or by the resolved addresses of its connected peers.
    private static bool IsSwitchSource(Generation? current, in SentinelHint hint)
    {
        if (current is not { IsRetired: false }) return false;
        if (hint.OldPrimary is { } oldPrimary && IsCurrentPeer(current, oldPrimary, hint.OldPrimaryAddresses)) return true;
        if (hint.AdditionalOldPrimaries is { } additional)
        {
            for (var i = 0; i < additional.Length; i++)
            {
                var addresses = hint.AdditionalOldPrimaryAddresses is { } allAddresses && i < allAddresses.Length
                    ? allAddresses[i]
                    : null;
                if (IsCurrentPeer(current, additional[i], addresses)) return true;
            }
        }
        return false;
    }

    // A coalesced A→B→A sequence must rediscover, but B can already be a valid current
    // primary. Keep any announced target alive while that fresh discovery runs.
    private static bool IsAnnouncedTarget(Generation? current, in SentinelHint hint)
    {
        if (current is not { IsRetired: false }) return false;
        if (hint.Target is { } target && SameEndpoint(current.Endpoint, target)) return true;
        if (hint.AdditionalTargets is { } additional)
            foreach (var endpoint in additional)
                if (SameEndpoint(current.Endpoint, endpoint)) return true;
        return false;
    }

    private static bool IsCurrentPeer(Generation current, RespireEndpoint endpoint, string[]? addresses)
    {
        if (SameEndpoint(current.Endpoint, endpoint)) return true;
        if (addresses is not null)
            foreach (var address in addresses)
                if (current.Multiplexer.HasCurrentPeer(address, endpoint.Port)) return true;
        return false;
    }

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => left.Port == right.Port && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase);

    private async ValueTask<Generation> ConnectGenerationAsync(RespireOptions options, CancellationToken cancellationToken)
    {
        var generation = new Generation(this, core, options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _owned.Add(generation);
        }
        try
        {
            await generation.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return generation;
        }
        catch
        {
            await generation.DisposeAsync().ConfigureAwait(false);
            lock (_gate) RemoveOwnedLocked(generation);
            throw;
        }
    }

    private void Invalidate(Generation generation, Exception? error = null)
    {
        lock (_gate)
        {
            // Retirement and its cleanup task become visible together to disposal. Once
            // disposal owns the router, it aborts every generation itself.
            if (!generation.TryRetire() || _disposed || !ReferenceEquals(Current, generation)) return;
            // Unpublished candidates are disposed by their discovery owner. Only the current
            // published generation can lose client continuity or need background draining.
            // The transport admission check sees retirement before any waiting caller resumes.
            // Cache invalidation is synchronous; metrics and health callbacks run elsewhere.
            generation.CountedAsRetired = true;
            Interlocked.Increment(ref _retiredGenerationCount);
            var evictions = core.ClientCache?.FlushForContinuityLossWithoutMetrics();
            if (evictions is { } count)
                QueueNotificationLocked(() => ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count));
            QueueNotificationLocked(() => core.NotifySentinelDisconnected(generation.Multiplexer, error));
            generation.Retirement = Task.Run(() => DrainAsync(generation));
        }
    }

    private void RemoveOwnedLocked(Generation generation)
    {
        if (!_owned.Remove(generation) || !generation.CountedAsRetired) return;
        generation.CountedAsRetired = false;
        Interlocked.Decrement(ref _retiredGenerationCount);
    }

    // Do not join this chain during disposal: an observer may synchronously dispose the
    // client itself. Queued callbacks are suppressed; an active callback may finish later.
    // Task.Run must keep user callbacks asynchronous: callers hold the publication gate.
    private void QueueNotificationLocked(Action notification, bool suppressAfterDisposal = true)
    {
        var previous = _notifications;
        _notifications = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (!suppressAfterDisposal || !core.Disposed) notification();
            }
            catch (Exception error)
            {
                try { core.Logger?.LogWarning(error, "Sentinel state observer failed"); }
                catch (Exception) { /* Keep later notifications independent of a user logger failure. */ }
            }
        });
    }

    private async Task DrainAsync(Generation generation)
    {
        var connectionsDrained = generation.StopConnections();
        var poolDrain = generation.RetirePoolsAsync();
        try
        {
            try { await generation.Multiplexer.RetireAsync().ConfigureAwait(false); }
            catch (Exception error) when (generation.Multiplexer.RetirementDrained && !_lifetime.IsCancellationRequested)
            {
                try { core.Logger?.LogDebug(error, "Sentinel transport retirement reported an error after draining at {Endpoint}", generation.Endpoint); }
                catch (Exception) { /* Diagnostics must not abandon correction-fence cleanup. */ }
            }
            var delay = 1;
            long? lastWarning = null;
            while (generation.Multiplexer.HasPendingCorrectionFences && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await generation.Multiplexer.FenceRetiredConnectionsAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    var now = Clock.GetTimestamp();
                    if (lastWarning is null || Clock.GetElapsedTime(lastWarning.Value, now) >= TimeSpan.FromMinutes(5))
                    {
                        lastWarning = now;
                        try { core.Logger?.LogWarning(error, "Sentinel generation at {Endpoint} retains an unacknowledged correction fence", generation.Endpoint); }
                        catch (Exception) { /* Logging must not abandon an owed fence. */ }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), Clock, _lifetime.Token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 30);
                }
            }
            await poolDrain.ConfigureAwait(false);
            await connectionsDrained.ConfigureAwait(false);
            lock (_gate) RemoveOwnedLocked(generation);
        }
        catch (Exception error)
        {
            try { await Task.WhenAll(poolDrain, connectionsDrained).ConfigureAwait(false); }
            catch (Exception poolError) { error = new AggregateException(error, poolError); }
            if (!_lifetime.IsCancellationRequested)
            {
                try { core.Logger?.LogWarning(error, "Sentinel generation cleanup failed at {Endpoint}; retained until client disposal", generation.Endpoint); }
                catch (Exception) { /* Ownership remains available to disposal even when logging fails. */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _disposed = true;
            _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = DisposeCoreAsync(_disposeCompletion);
            return new(_disposeCompletion.Task);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Task[] monitorTasks;
            // Join notification rediscovery and switch-source DNS checks too, so a late attempt
            // cannot publish or invalidate after disposal.
            lock (_gate) monitorTasks = [_notificationMonitorSupervisor, .. _notificationMonitors.Values,
                _notificationRediscovery ?? Task.CompletedTask, .. _switchSourceResolutions];
            Exception? disposeError = null;
            // Bounded: a monitor client whose cleanup ignores cancellation must not hang disposal.
            // Stragglers cannot publish or retire afterwards, because both recheck _disposed under the gate.
            try { await Task.WhenAll(monitorTasks).WaitAsync(NotificationShutdownTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                SafeLog(monitorTasks, static (logger, tasks) => logger.LogWarning(
                    "Sentinel event monitoring did not stop within {Timeout}; {Count} task(s) still running",
                    NotificationShutdownTimeout, tasks.Count(task => !task.IsCompleted)));
            }
            catch (Exception error) { disposeError = error; }
            await _discoveryGate.WaitAsync().ConfigureAwait(false);
            _discoveryGate.Release();
            Generation[] owned;
            DedicatedConnectionPool[] corrections;
            // Safe after releasing the discovery gate: ConnectGenerationAsync rechecks _disposed
            // under _gate before adding, so no generation can join _owned after this snapshot.
            lock (_gate)
            {
                owned = _owned.ToArray();
                corrections = _correctionPools.ToArray();
            }
            // Start every owned cleanup before observing failures, then join retirement too.
            // A failing correction or connection must not strand another generation.
            try
            {
                await CleanupTasks.WhenAllAsync(corrections.Select(pool => pool.DisposeAsync().AsTask())
                    .Concat(owned.Select(generation => generation.DisposeAsync().AsTask()))).ConfigureAwait(false);
            }
            catch (Exception error) { disposeError = disposeError is null ? error : new AggregateException(disposeError, error).Flatten(); }
            try { await CleanupTasks.WhenAllAsync(owned.Select(generation => generation.Retirement)).ConfigureAwait(false); }
            catch (Exception error)
            {
                disposeError = disposeError is null ? error : new AggregateException(disposeError, error).Flatten();
            }
            finally
            {
                // Disposal ends ownership even when transport cleanup faults. Clear the
                // retained-generation gauge before propagating any cleanup error.
                lock (_gate)
                {
                    foreach (var generation in owned) RemoveOwnedLocked(generation);
                    _correctionPools.Clear();
                }
            }
            if (disposeError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeError).Throw();
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal sealed class Generation : IConnectionGeneration, IAsyncDisposable
    {
        private readonly SentinelRouter _owner;
        private readonly ClientCore _core;
        private readonly object _connectionsGate = new();
        private readonly object _poolsGate = new();
        private readonly DedicatedPoolLedger _pools;
        private DedicatedConnectionPool _pool;
        private readonly HashSet<RespireConnection> _connections = [];
        private int _retired;
        internal readonly RespireEndpoint Endpoint;
        internal readonly RespireConnectionMultiplexer Multiplexer;
        internal DedicatedConnectionPool Pool
        {
            get { RefreshPool(); return Volatile.Read(ref _pool); }
        }
        internal readonly RespireConnectionOptions ConnectionOptions;
        internal Task Retirement = Task.CompletedTask;
        internal bool CountedAsRetired; // Accessed only under the router gate.

        internal Generation(SentinelRouter owner, ClientCore core, RespireOptions options)
        {
            _pools = new(_poolsGate);
            _owner = owner;
            _core = core;
            Endpoint = options.PrimaryEndpoint;
            ConnectionOptions = options.ToConnectionOptions(enableMaintenanceNotifications: true) with { Generation = this };
            var clientCache = core.ClientCache;
            RespirePushHandler? pushHandler = clientCache is null ? null : clientCache.HandlePush;
            var commandOptions = options.ToConnectionOptions(pushHandler,
                enableClientTracking: core.ClientCache is not null, enableMaintenanceNotifications: true) with
            {
                Generation = this,
                CredentialCacheInvalidation = clientCache is null ? null : clientCache.FlushForContinuityLossWithoutMetrics,
                CredentialCacheRetirementFence = clientCache is null ? null : clientCache.FlushForMovingRetirementFence,
            };
            Multiplexer = RespireConnectionMultiplexer.Create(Endpoint.Host, Endpoint.Port, options.Connections, commandOptions, core.Logger);
            _pool = CreatePool(Multiplexer.CaptureMovingPublication());
            _pools.Add(_pool);
            Multiplexer.MovingHandoffPublished += RefreshPool;
        }

        private DedicatedConnectionPool CreatePool((RespireEndpoint Endpoint, object Publication) publication)
        {
            var endpoint = publication.Endpoint;
            DedicatedConnectionPool? pool = null;
            pool = new(endpoint.Host, endpoint.Port, ConnectionOptions, _core.Logger, _core.NotifyRecoveryStateChanged,
                connection =>
                {
                    void OnMoving(MovingAnnouncement announcement)
                        => Multiplexer.QueueDedicatedMovingHandoff(connection, announcement,
                            () => !IsRetired && ReferenceEquals(pool, Volatile.Read(ref _pool)) && !pool!.IsStopping);
                    connection.MovingNotification += OnMoving;
                    if (connection.LastMovingAnnouncement is { } announcement) OnMoving(announcement);
                }) { MovingOwner = Multiplexer, MovingPublication = publication.Publication };
            return pool;
        }

        private void RefreshPool()
        {
            DedicatedConnectionPool previous;
            lock (_poolsGate)
            {
                if (IsRetired) return;
                var publication = Multiplexer.CaptureMovingPublication();
                if (ReferenceEquals(_pool.MovingPublication, publication.Publication)) return;
                previous = _pool;
                var next = CreatePool(publication);
                _pools.Add(next);
                Volatile.Write(ref _pool, next);
            }
            _ = DrainMovedPoolAsync(previous);
        }

        private async Task DrainMovedPoolAsync(DedicatedConnectionPool pool)
        {
            try
            {
                await _pools.RetireAsync(pool).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try { _core.Logger?.LogWarning(error, "Sentinel upload pool cleanup after MOVING failed"); }
                catch { /* Keep failed cleanup owned even if logging fails. */ }
            }
        }

        internal Task RetirePoolsAsync() => _pools.RetireAllAsync();

        public bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal bool TryRetire() => Interlocked.Exchange(ref _retired, 1) == 0;

        public async ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
        {
            using var reply = await connection.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
            if (!IsPrimary(in reply))
            {
                _owner.Invalidate(this);
                throw new RespireConnectionException($"Sentinel candidate at {Endpoint} did not confirm a valid primary ROLE.");
            }
            lock (_connectionsGate)
            {
                if (IsRetired || !connection.IsConnected)
                    throw new RespireConnectionException($"Sentinel candidate at {Endpoint} closed before validation completed.");
                _connections.Add(connection);
            }
        }

        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response)
        {
            // Ordinary collection reads cannot carry per-command errors. Avoid walking
            // their arrays again on the receive loop; only heterogeneous aggregates need it.
            var readOnly = response.IsError && ContainsReadOnly(in response);
            if (!readOnly && operation is "MULTI/EXEC" or "EXEC" or "EVAL" or "EVALSHA" or "EVAL_RO" or "EVALSHA_RO" or "FCALL" or "FCALL_RO")
                readOnly = ContainsReadOnly(in response);
            if (readOnly || operation == "ROLE" && !response.IsError && !IsPrimary(in response))
                _owner.Invalidate(this);
        }

        public void ConnectionClosed(RespireConnection connection, bool unexpected)
        {
            lock (_connectionsGate) _connections.Remove(connection);
            if (unexpected) _owner.Invalidate(this, connection.CloseError);
        }

        internal Task StopConnections()
        {
            RespireConnection[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            return Task.WhenAll(connections.Select(connection => connection.RetireAsync()));
        }

        internal static bool IsPrimary(in RespValue reply)
        {
            if (reply.Type != RespDataType.Array) return false;
            var role = reply.AsArray();
            return role.Length >= 3 && role[0].Type is RespDataType.BulkString or RespDataType.SimpleString
                && role[0].AsString() == "master" && role[1].Type == RespDataType.Integer && role[2].Type == RespDataType.Array;
        }

        private static bool ContainsReadOnly(in RespValue reply)
        {
            if (reply.IsError) return IsReadOnlyError(in reply);
            if (reply.AsArray().IsEmpty) return false;
            // The parser accepts arbitrary nesting. Flat replies need no allocation;
            // Every aggregate exposes elements through AsArray; maps include keys and values.
            // Nested aggregates retain only the parents with unvisited siblings.
            Stack<(RespValue Parent, int NextIndex)>? parents = null;
            var current = reply;
            var index = 0;
            while (true)
            {
                var elements = current.AsArray();
                if (index == elements.Length)
                {
                    if (parents is null || !parents.TryPop(out var parent)) return false;
                    (current, index) = parent;
                    continue;
                }
                var element = elements[index++];
                if (element.IsError && IsReadOnlyError(in element)) return true;
                if (element.AsArray().IsEmpty) continue;
                if (index < elements.Length)
                    (parents ??= new()).Push((current, index));
                current = element;
                index = 0;
            }
        }

        private static bool IsReadOnlyError(in RespValue reply)
        {
            var error = reply.GetErrorMessage();
            return error == "READONLY" || error.StartsWith("READONLY ", StringComparison.Ordinal);
        }

        public async ValueTask DisposeAsync()
        {
            TryRetire();
            Multiplexer.MovingHandoffPublished -= RefreshPool;
            RespireConnection[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            await CleanupTasks.WhenAllAsync(connections.Select(connection => connection.DisposeAsync().AsTask())
                .Append(_pools.DisposeAllAsync())
                .Append(Multiplexer.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
    }
}
