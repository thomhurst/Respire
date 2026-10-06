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
internal sealed partial class SentinelRouter(ClientCore core) : IAsyncDisposable
{
    private static long _retiredGenerationCount;
    internal static long RetiredGenerationCount => Interlocked.Read(ref _retiredGenerationCount);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SentinelDiscoveryState _discovery = new(core.Options.Endpoints.Count == 0
        ? [new RespireEndpoint("localhost", 26379)] : core.Options.Endpoints);
    private readonly HashSet<Generation> _owned = [];
    private readonly HashSet<DedicatedConnectionPool> _correctionPools = [];
    private readonly SentinelNotificationCoalescer _coalescer = new(); // Guarded by _gate.
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private Task _notifications = Task.CompletedTask;
    private Task? _notificationRediscovery;
    private TaskCompletionSource _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SentinelBackgroundWork? _background;
    private SentinelBackgroundWork Background { get { lock (_gate) return _background ??= new(_gate); } }
    // Optional observer for tests; production does not count or allocate notification test state.
    internal volatile Action? NotificationQueuedObserver;
    // Only the single notification worker reads/writes this deadline. _gate serializes worker
    // publication and clearing _notificationRediscovery before a replacement worker can start.
    internal const int MinimumNotificationDiscoveryIntervalMilliseconds = SentinelNotificationState.MinimumDiscoveryIntervalMilliseconds;
    private readonly string _masterDownKey = "master-down:" + core.Options.SentinelPrimaryName;

    internal Generation? Current => Volatile.Read(ref _current);
    internal RespireEndpoint[] DiscoveredEndpoints => _discovery.Snapshot();
    private SentinelMonitoring? _monitoring;
    internal SentinelMonitoring Monitoring
    {
        get
        {
            lock (_gate) return _monitoring ??= new(core.Options, core.Logger, _gate, _discovery, _lifetime,
                ObserveSentinelEventAsync, QueueDeliveryGapRediscovery, Background);
        }
    }
    internal TimeProvider Clock { get => Monitoring.Clock; set => Monitoring.Clock = value; }
    internal TimeProvider ShutdownClock { get; set; } = TimeProvider.System;
    internal Task CurrentMonitorRearm() => Monitoring.CurrentMonitorRearm();

    // Queries the configured and learned Sentinels directly, so replica reads do not depend on a
    // reachable primary during an outage or failover window.
    internal ValueTask<RespireEndpoint[]> DiscoverReplicaEndpointsAsync(CancellationToken cancellationToken)
        => SentinelResolver.DiscoverReplicaEndpointsAsync(core.Options, _discovery.Snapshot(), cancellationToken);
    internal bool IsConnected => Current is { IsRetired: false } generation && generation.Multiplexer.IsConnected;
    /// <summary>Counts distinct Sentinels with an established monitor subscription. Tests use it as readiness.</summary>
    internal int SubscribedSentinelCount
    {
        get => Monitoring.SubscribedCount;
    }
    /// <summary>Resolves switch sources and discovered owner aliases within their discovery deadlines.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> HostResolver { get => Monitoring.HostResolver; set => Monitoring.HostResolver = value; }
    internal int PendingSwitchSourceResolutions
    {
        get => Background.Count(SentinelWorkKind.SourceResolution);
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
            if (previous is { IsRetired: false } && previous.Multiplexer.IsConnected
                && (!forceDiscovery || notificationHint is { StartupSubscriptionVersion: > 0 } startup
                    && startup.ReportingSentinel is { } reporter
                    && !Monitoring.NeedsStartupValidation(reporter, startup.StartupSubscriptionVersion))) return previous;
            var subscriptionVersion = Monitoring.SubscriptionVersion;
            RespireEndpoint? acceptedReporter = null;
            // Classify against the generation current after acquiring discovery ownership:
            // a down report queued during A-to-B publication may describe B's next outage.
            if (notificationHint is { } downHint && previous is not null)
                notificationHint = downHint.BindDownReportsToCurrentPrimary(new(previous.Endpoint, previous.ValidatedPeer));
            if (!forceDiscovery && previous is not null) Invalidate(previous);
            // A forced discovery that resolves to the healthy current primary confirms it with ROLE
            // on the existing connection instead of opening and discarding a candidate generation.
            Func<RespireOptions, string[]?, CancellationToken, ValueTask<Generation>> connect = forceDiscovery && previous is not null
                ? (options, addresses, token) => ReuseOrConnectGenerationAsync(previous, options, addresses, notificationHint, token)
                : (options, _, token) => ConnectGenerationAsync(options, notificationHint, token);
            var replacement = await SentinelResolver.ResolveAndConnectPrimaryAsync(
                core.Options, connect, linked.Token, _discovery,
                notificationHint?.ReportingSentinel,
                forceDiscovery ? previous?.Endpoint : null,
                notificationHint?.Target, notificationHint, HostResolver,
                getValidatedPeer: static generation => generation.ValidatedPeer,
                rejectPrimaryAsync: RejectGenerationAsync,
                acceptedReporter: endpoint => acceptedReporter = endpoint).ConfigureAwait(false);
            if (ReferenceEquals(replacement, previous))
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (replacement.IsRetired || !ReferenceEquals(Current, replacement))
                        throw new RespireConnectionException("Sentinel primary changed while it was being revalidated.");
                    if (acceptedReporter is { } validatedReporter) Monitoring.Validated(validatedReporter, subscriptionVersion);
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
                    && SameEndpoint(old.Endpoint, replacement.Endpoint)
                    && old.ValidatedPeer is { } oldPeer && replacement.ValidatedPeer is { } replacementPeer
                    && SameEndpoint(oldPeer, replacementPeer)
                    && old.Multiplexer.AllCurrentPeersMatch(replacementPeer.Host, replacementPeer.Port))
                {
                    if (acceptedReporter is { } validatedReporter) Monitoring.Validated(validatedReporter, subscriptionVersion);
                    return old;
                }
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
                Monitoring.Published();
                if (acceptedReporter is { } publishedReporter) Monitoring.Validated(publishedReporter, subscriptionVersion);
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
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && generation.IsRetired
            && SentinelExceptionPolicy.IsRecoverable(error))
        {
            // Every failure on a generation that was retired while ROLE was in flight means "not the
            // current primary". A ROLE mismatch seen by application traffic retires the generation
            // before this resumes, and a socket or timeout fault on a retired generation needs the same
            // rediscovery. A failed rediscovery below becomes the probe error, so keep this cause in the log.
            SafeLog(error, static (logger, error)
                => logger.SentinelRetiredPrimaryProbeFailed(error));
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
        string[]? addresses, SentinelHint? hint, CancellationToken cancellationToken)
    {
        var endpoint = options.PrimaryEndpoint;
        var samePeer = new SentinelAddressEvidence(endpoint, addresses).SingleAddress is { } address
            && current.Multiplexer.AllCurrentPeersMatch(address, endpoint.Port);
        // A stable DNS name is not proof that its established socket is still the owner.
        // Only an unavailable DNS answer permits falling back to textual endpoint identity.
        var sameEndpointWithoutAddresses = addresses is null && SameEndpoint(current.Endpoint, endpoint)
            && current.ValidatedPeer is { } peer && current.Multiplexer.AllCurrentPeersMatch(peer.Host, peer.Port);
        if (current.IsRetired || !current.Multiplexer.IsConnected
            || !sameEndpointWithoutAddresses && !samePeer)
            return await ConnectGenerationAsync(options, hint, cancellationToken).ConfigureAwait(false);
        await current.ValidateAsync(current.Multiplexer.GetConnection(), cancellationToken).ConfigureAwait(false);
        ValidateSwitchTargetPeer(current, hint);
        return current;
    }

    private void ValidateSwitchTargetPeer(Generation candidate, SentinelHint? hint)
    {
        if (hint is not { Target: { } target } evidence || !SameEndpoint(candidate.Endpoint, target)
            || System.Net.IPAddress.TryParse(target.Host, out _)) return;
        // DNS can change between discovery's lookup and connection establishment. The peer
        // of every validated socket must be distinct from a differently named switch source.
        if (candidate.FindSwitchSourcePeer(target, in evidence) is not { } peer) return;
        Invalidate(candidate);
        throw new RespireConnectionException($"Sentinel target {target} connected to a demoted switch source at {peer}.");
    }

    // The supervisor wakes when discovery learns a Sentinel. Monitors complete only when the
    // client is disposed, so this fallback interval only restarts one that faulted unexpectedly.
    private async ValueTask<Generation> ConnectGenerationAsync(RespireOptions options, SentinelHint? hint, CancellationToken cancellationToken)
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
            ValidateSwitchTargetPeer(generation, hint);
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
            if (_disposed || !generation.TryRetire() || !ReferenceEquals(Current, generation)) return;
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

    private async ValueTask RejectGenerationAsync(Generation generation)
    {
        if (ReferenceEquals(generation, Current))
        {
            // Revalidation can reject a published generation. Preserve its in-flight work
            // through normal retirement; unpublished candidates can be disposed immediately.
            Invalidate(generation);
            return;
        }
        await generation.DisposeAsync().ConfigureAwait(false);
        lock (_gate) RemoveOwnedLocked(generation);
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
            catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
            {
                SafeLog(error, static (logger, error) => logger.SentinelStateObserverFailed(error));
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
            catch (Exception error) when (generation.Multiplexer.RetirementDrained && !_lifetime.IsCancellationRequested
                && SentinelExceptionPolicy.IsRecoverable(error))
            {
                SafeLog((error, generation.Endpoint), static (logger, state)
                    => logger.SentinelRetirementDrainFailed(state.Endpoint, state.error));
            }
            var delay = 1;
            long? lastWarning = null;
            while (generation.Multiplexer.HasPendingCorrectionFences && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await generation.Multiplexer.FenceRetiredConnectionsAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested && SentinelExceptionPolicy.IsRecoverable(error))
                {
                    var now = Clock.GetTimestamp();
                    if (lastWarning is null || Clock.GetElapsedTime(lastWarning.Value, now) >= TimeSpan.FromMinutes(5))
                    {
                        lastWarning = now;
                        SafeLog((error, generation.Endpoint), static (logger, state)
                            => logger.SentinelCorrectionFenceUnacknowledged(state.Endpoint, state.error));
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), Clock, _lifetime.Token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 30);
                }
            }
            await poolDrain.ConfigureAwait(false);
            await connectionsDrained.ConfigureAwait(false);
            lock (_gate) RemoveOwnedLocked(generation);
        }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            try { await Task.WhenAll(poolDrain, connectionsDrained).ConfigureAwait(false); }
            catch (Exception poolError) when (SentinelExceptionPolicy.IsRecoverable(poolError)) { error = new AggregateException(error, poolError); }
            if (!_lifetime.IsCancellationRequested)
            {
                SafeLog((error, generation.Endpoint), static (logger, state)
                    => logger.SentinelGenerationCleanupFailed(state.Endpoint, state.error));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _disposed = true;
            _coalescer.Transition(new(SentinelNotificationEventKind.Dispose));
            var background = Monitoring.Stop();
            _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = DisposeCoreAsync(_disposeCompletion, background);
            return new(_disposeCompletion.Task);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion, Task[] monitorTasks)
    {
        try
        {
            Exception? disposeError = null;
            // Cancellation callbacks share the shutdown bound: an uncooperative callback
            // must not prevent cleanup of generations. Registration already stopped under gate.
            monitorTasks = [.. monitorTasks, _lifetime.CancelAsync()];
            var stopping = CleanupTasks.WhenAllAsync(monitorTasks);
            // Bounded: a monitor client whose cleanup ignores cancellation must not hang disposal.
            // Stragglers cannot publish or retire afterwards, because both recheck _disposed under the gate.
            try { await stopping.WaitAsync(NotificationShutdownTimeout, ShutdownClock).ConfigureAwait(false); }
            catch (TimeoutException) when (!stopping.IsFaulted)
            {
                SafeLog(monitorTasks, static (logger, tasks) => logger.SentinelMonitorShutdownTimedOut(NotificationShutdownTimeout, tasks));
            }
            catch (Exception error) { disposeError = stopping.Exception?.InnerException ?? error; }
            finally
            {
                // A timed-out join still owns its late failure; never leave it unobserved.
                _ = stopping.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            Generation[] owned;
            DedicatedConnectionPool[] corrections;
            // No discovery-gate wait is needed: _disposed is set before this snapshot, and
            // ConnectGenerationAsync checks it under _gate before adding an owned generation.
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
        internal SentinelGenerationIdentity Identity { get; } = new();
        private readonly SentinelRouter _owner;
        private readonly ClientCore _core;
        private readonly Lock _connectionsGate = new();
        private readonly Lock _poolsGate = new();
        private readonly DedicatedPoolLedger _pools;
        private DedicatedConnectionPool _pool;
        private readonly HashSet<RespireConnection> _connections = [];
        private int _retired;
        internal readonly RespireEndpoint Endpoint;
        // Keep the actual socket peer from ROLE validation. A multi-address DNS result cannot
        // prove which server answered and must never consume another source's demotion fence.
        private RespireConnection? _validatedConnection;
        internal RespireEndpoint? ValidatedPeer => Volatile.Read(ref _validatedConnection) is { } connection
            ? new(connection.PeerKey.Host, connection.PeerKey.Port) : null;
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
                await _pools.RetireAsync(pool, moving: true).ConfigureAwait(false);
            }
            catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
            {
                _owner.SafeLog(error, static (logger, error) => logger.SentinelMovingUploadCleanupFailed(error));
            }
        }

        internal Task RetirePoolsAsync() => _pools.RetireAllAsync();

        internal RespireEndpoint? FindSwitchSourcePeer(RespireEndpoint target, in SentinelHint hint)
        {
            lock (_connectionsGate)
            {
                foreach (var connection in _connections)
                {
                    var peer = new RespireEndpoint(connection.PeerKey.Host, connection.PeerKey.Port);
                    if (SentinelResolver.TargetPeerMatchesSwitchSource(target, peer, in hint)) return peer;
                }
            }
            return null;
        }

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
                Volatile.Write(ref _validatedConnection, connection);
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
