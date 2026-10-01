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
    private readonly Dictionary<string, Task> _notificationMonitors = new(StringComparer.OrdinalIgnoreCase);
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private Task _notifications = Task.CompletedTask;
    private Task _notificationMonitorSupervisor = Task.CompletedTask;
    private Task? _notificationRediscovery;
    private bool _notificationPending;
    private string? _activeNotificationKey;
    private string? _pendingNotificationKey;
    private RespireEndpoint? _pendingNotificationTarget;
    private bool _pendingNotificationRetiresCurrent;

    internal Generation? Current => Volatile.Read(ref _current);
    internal RespireEndpoint[] DiscoveredEndpoints => _discovery.Snapshot();
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Queries the configured and learned Sentinels directly, so replica reads do not depend on a
    // reachable primary during an outage or failover window.
    internal ValueTask<RespireEndpoint[]> DiscoverReplicaEndpointsAsync(CancellationToken cancellationToken)
        => SentinelResolver.DiscoverReplicaEndpointsAsync(core.Options, _discovery.Snapshot(), cancellationToken);
    internal bool IsConnected => Current is { IsRetired: false } generation && generation.Multiplexer.IsConnected;

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

    internal async ValueTask<Generation> GetGenerationAsync(CancellationToken cancellationToken, bool forceDiscovery = false)
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
            var replacement = await SentinelResolver.ResolveAndConnectPrimaryAsync(
                core.Options, ConnectGenerationAsync, linked.Token, _discovery).ConfigureAwait(false);
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
        if (role.Type != RespDataType.Array) return false;
        var fields = role.AsArray();
        return fields.Length >= 3
            && fields[0].Type is RespDataType.BulkString or RespDataType.SimpleString
            && fields[0].AsString() == "master"
            && fields[1].Type == RespDataType.Integer
            && fields[2].Type == RespDataType.Array;
    }

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
                foreach (var endpoint in _discovery.Snapshot())
                {
                    var key = endpoint.Host + ":" + endpoint.Port;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        if (_notificationMonitors.TryGetValue(key, out var monitor) && !monitor.IsCompleted) continue;
                        _notificationMonitors[key] = Task.Run(() => MonitorSentinelAsync(endpoint, _lifetime.Token));
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), Clock, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task MonitorSentinelAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var client = await RespireClient.ConnectAsync(CreateSentinelMonitorOptions(core.Options, endpoint), cancellationToken).ConfigureAwait(false);
                var subscription = await client.SubscribeAsync(
                    ["+switch-master", "+sdown", "+odown"], cancellationToken).ConfigureAwait(false);
                try
                {
                    attempt = 0;
                    await foreach (var message in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
                        ObserveSentinelNotification(endpoint, message);
                    if (!cancellationToken.IsCancellationRequested)
                        await subscription.Completion.ConfigureAwait(false);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { await client.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception) { }
                    }
                    try { await subscription.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception) when (cancellationToken.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                try { core.Logger?.LogWarning(error, "Sentinel event monitor failed at {Endpoint}", endpoint); }
                catch (Exception) { }
            }

            attempt++;
            var policy = core.Options.ReconnectPolicy;
            if (policy?.IsExhausted(attempt) == true)
            {
                try { core.Logger?.LogWarning("Sentinel event monitor exhausted reconnect attempts at {Endpoint}", endpoint); }
                catch (Exception) { }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                return;
            }
            var delay = policy?.GetDelay(attempt) ?? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));
            try { await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    internal static RespireOptions CreateSentinelMonitorOptions(RespireOptions options, RespireEndpoint endpoint)
    {
        var authDisabled = options.SentinelPassword is { Length: 0 };
        var useSeparateCredentials = options.SentinelUsername is not null || options.SentinelPassword is not null;
        return options with
        {
            Endpoints = [endpoint],
            UseCluster = false,
            SentinelPrimaryName = null,
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
            Protocol = options.SentinelCredentialProvider is not null
                || (!useSeparateCredentials && options.CredentialProvider is not null)
                ? RespProtocol.Resp3 : RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            ClientSideCache = null,
            ThreadPoolMonitoring = false,
            Connections = 1,
        };
    }

    private void ObserveSentinelNotification(RespireEndpoint sentinel, in RespireMessage message)
    {
        if (message.Kind != RespireMessageKind.Message) return;
        var channel = message.Channel.ToString();
        var fields = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var serviceName = core.Options.SentinelPrimaryName;
        if (serviceName is null || fields.Length == 0) return;
        var switchHint = channel == "+switch-master" && fields.Length >= 5
            && fields[0].Equals(serviceName, StringComparison.Ordinal);
        var masterDownHint = (channel is "+sdown" or "+odown") && fields.Length >= 4
            && fields[0] == "master" && fields[1].Equals(serviceName, StringComparison.Ordinal);
        var relatedDownEvent = (channel is "+sdown" or "+odown") && fields.Length >= 4
            && (masterDownHint || fields.Length >= 6 && fields[0] == "slave"
                && fields[3] == "@" && fields[4].Equals(serviceName, StringComparison.Ordinal));
        if (!switchHint && !relatedDownEvent) return;
        RespireEndpoint? promotedPrimary = null;
        if (channel == "+switch-master")
        {
            if (!int.TryParse(fields[2], out var oldPort) || oldPort is < 1 or > 65535
                || !int.TryParse(fields[4], out var newPort) || newPort is < 1 or > 65535) return;
            promotedPrimary = new(fields[3], newPort);
        }
        if (relatedDownEvent)
        {
            try { core.Logger?.LogInformation("Sentinel {Channel} hint for service {Service} from {Sentinel}", channel, serviceName, sentinel); }
            catch (Exception) { }
        }
        if (switchHint || masterDownHint)
            QueueNotificationRediscovery(channel + ":" + message.Text, promotedPrimary, retireCurrent: switchHint);
    }

    private void QueueNotificationRediscovery(string notificationKey, RespireEndpoint? target, bool retireCurrent)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_activeNotificationKey == notificationKey || _pendingNotificationKey == notificationKey) return;
            var current = Current;
            if (target is { } targetEndpoint && current is { IsRetired: false }
                && SameEndpoint(current.Endpoint, targetEndpoint)) return;
            _notificationPending = true;
            _pendingNotificationKey = notificationKey;
            _pendingNotificationTarget = target;
            _pendingNotificationRetiresCurrent = retireCurrent;
            if (_notificationRediscovery is { IsCompleted: false }) return;
            _notificationPending = false;
            _pendingNotificationKey = null;
            _pendingNotificationTarget = null;
            _pendingNotificationRetiresCurrent = false;
            _activeNotificationKey = notificationKey;
            if (retireCurrent && current is { IsRetired: false }) Invalidate(current);
            _notificationRediscovery = Task.Run(() => RediscoverFromNotificationAsync(notificationKey));
        }
    }

    private async Task RediscoverFromNotificationAsync(string notificationKey)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await GetGenerationAsync(_lifetime.Token, forceDiscovery: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                try { core.Logger?.LogWarning(error, "Sentinel notification-triggered primary discovery failed"); }
                catch (Exception) { }
            }

            lock (_gate)
            {
                if (_disposed) return;
                if (!_notificationPending)
                {
                    _notificationRediscovery = null;
                    _activeNotificationKey = null;
                    return;
                }
                var pendingKey = _pendingNotificationKey;
                var pendingTarget = _pendingNotificationTarget;
                var retireCurrent = _pendingNotificationRetiresCurrent;
                _notificationPending = false;
                _pendingNotificationKey = null;
                _pendingNotificationTarget = null;
                _pendingNotificationRetiresCurrent = false;
                var current = Current;
                if (pendingTarget is { } target && current is { IsRetired: false }
                    && SameEndpoint(current.Endpoint, target))
                {
                    _notificationRediscovery = null;
                    _activeNotificationKey = null;
                    return;
                }
                if (retireCurrent && current is { IsRetired: false }) Invalidate(current);
                notificationKey = pendingKey ?? notificationKey;
                _activeNotificationKey = notificationKey;
            }
        }
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
            lock (_gate) monitorTasks = [_notificationMonitorSupervisor, .. _notificationMonitors.Values];
            Exception? disposeError = null;
            try { await Task.WhenAll(monitorTasks).ConfigureAwait(false); }
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

        private static bool IsPrimary(in RespValue reply)
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
