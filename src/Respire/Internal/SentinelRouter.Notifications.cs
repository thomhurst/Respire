using System.Net;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Monitor supervision, event evidence, and the single notification discovery worker.
internal sealed partial class SentinelRouter
{
    private static readonly TimeSpan SentinelMonitorSupervisorFallbackInterval = TimeSpan.FromSeconds(30);
    // Disposal stops waiting for monitor, supervisor and rediscovery tasks after this bound and
    // logs the stragglers. Publication and retirement recheck disposal under the gate.
    private static readonly TimeSpan NotificationShutdownTimeout = TimeSpan.FromSeconds(10);
    private const string SentinelMonitorReconnectScope = "sentinel-monitor";

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
                List<(RespireEndpoint Endpoint, Exception? Error)>? restarted = null;
                lock (_gate)
                {
                    if (_disposed) return;
                    foreach (var endpoint in endpoints)
                    {
                        if (_notificationMonitors.TryGetValue(endpoint, out var monitor) && !monitor.IsCompleted) continue;
                        if (monitor is not null)
                            (restarted ??= []).Add((endpoint, monitor.Exception));
                        _notificationMonitors[endpoint] = Task.Run(() => MonitorSentinelAsync(endpoint, _lifetime.Token));
                    }
                }
                if (restarted is not null)
                    foreach (var restart in restarted)
                        SafeLog(restart, static (logger, state) => logger.LogWarning(state.Error,
                            "Restarting an unexpectedly completed Sentinel event monitor at {Endpoint}", state.Endpoint));
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
                    () =>
                    {
                        // Temporary clients that close before SUBSCRIBE succeeds belong to
                        // the same outer retry episode. Preserve any publication it captured.
                        if (Volatile.Read(ref attempt) == 0)
                            Volatile.Write(ref rearm, CurrentMonitorRearm());
                    }));
                subscription = await client.SubscribeAsync(
                    ["+switch-master", "+sdown", "+odown"], cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref attempt, 0);
                // The close callback captures the current epoch for each reconnect episode.
                // Do not overwrite it here: the socket may already have closed and a publication
                // may already have completed that captured epoch before this continuation runs.
                // The first subscription follows initial discovery; reconnects can miss events
                // while disconnected. Revalidate after either subscription is established.
                QueueDeliveryGapRediscovery(endpoint, initialSubscription: !subscribedBefore);
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
            catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
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

    internal Task CurrentMonitorRearm()
        => Volatile.Read(ref _monitorRearm).Task;

    private async ValueTask DisposeMonitorResourceAsync(IAsyncDisposable? resource, RespireEndpoint endpoint)
    {
        if (resource is null) return;
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
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
        // Copy only transport, credentials and subscription policy. New data-client options
        // must not silently become monitor settings.
        return new RespireOptions
        {
            Endpoints = [endpoint],
            TestingStreamFactory = options.TestingStreamFactory,
            LoggerFactory = options.LoggerFactory,
            ConnectTimeout = options.ConnectTimeout,
            CommandTimeout = options.CommandTimeout,
            ConnectionIdleReadTimeout = options.ConnectionIdleReadTimeout,
            ReconnectPolicy = options.ReconnectPolicy,
            CredentialRefreshBeforeExpiry = options.CredentialRefreshBeforeExpiry,
            CredentialRefreshRetryDelay = options.CredentialRefreshRetryDelay,
            CredentialTimeProvider = options.CredentialTimeProvider,
            TcpKeepAliveTime = options.TcpKeepAliveTime,
            TcpKeepAliveInterval = options.TcpKeepAliveInterval,
            TcpKeepAliveRetryCount = options.TcpKeepAliveRetryCount,
            SubscriptionBufferSize = options.SubscriptionBufferSize,
            SubscriptionOverflow = options.SubscriptionOverflow,
            ReceiveBufferSize = options.ReceiveBufferSize,
            WriteBufferSize = options.WriteBufferSize,
            MaxInflightCommands = options.MaxInflightCommands,
            Username = authDisabled ? null : options.SentinelUsername ?? options.Username,
            Password = authDisabled ? null : options.SentinelPassword ?? options.Password,
            CredentialProvider = authDisabled ? null : options.SentinelCredentialProvider
                ?? (useSeparateCredentials ? null : options.CredentialProvider),
            UseTls = options.SentinelUseTls ?? options.UseTls,
            TlsOptions = options.SentinelTlsOptions ?? options.TlsOptions,
            Protocol = !authDisabled && (options.SentinelCredentialProvider is not null
                || (!useSeparateCredentials && options.CredentialProvider is not null)
                ) ? RespProtocol.Resp3 : RespProtocol.Resp2,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            ReconnectTelemetryScope = SentinelMonitorReconnectScope,
            ReconnectEpisodeStarted = reconnectEpisodeStarted,
            ThreadPoolMonitoring = false,
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
                // Ignore changing quorum counts, but distinguish a later outage of the promoted
                // primary from another reporter describing the outage already being recovered.
                lock (_gate)
                {
                    var observed = Current;
                    SentinelValidatedPrimary? owner = observed is null ? null : new(observed.Endpoint, observed.ValidatedPeer);
                    QueueNotificationRediscovery(SentinelHint.FromDown(_masterDownKey, sentinel, sentinelEvent.OldPrimary, owner));
                }
                return ValueTask.CompletedTask;
            case SentinelEventKind.SwitchMaster:
                LogSentinelEvent(LogLevel.Information, message, sentinel);
                // Queue immediately so slow DNS cannot hold up later one-shot notifications.
                // Every Sentinel in the quorum announces the same parsed switch, so key on it.
                var hint = SentinelHint.FromSwitchMaster(sentinelEvent is { OldPrimary: { } from, NewPrimary: { } to }
                        ? $"+switch-master:{from.Host}:{from.Port}>{to.Host}:{to.Port}"
                        : "+switch-master:" + message.Text,
                    sentinelEvent.OldPrimary, sentinelEvent.NewPrimary, sentinel);
                // Only the generation current when the event arrived can be its source. A later
                // failover back to the same endpoint publishes a new generation that must survive.
                lock (_gate)
                {
                    var arrivedDuring = Current;
                    if (hint.OldPrimary is { } announcedSource && arrivedDuring?.ValidatedPeer is { } peer
                        && SameEndpoint(arrivedDuring.Endpoint, announcedSource))
                        hint = hint.WithSourceAddresses(announcedSource, [SentinelResolver.NormalizeHost(peer.Host)]);
                    QueueNotificationRediscovery(in hint);
                    if (sentinelEvent.OldPrimary is { } source && arrivedDuring is not null
                        && !SameEndpoint(arrivedDuring.Endpoint, source))
                        StartSwitchSourceResolution(hint, arrivedDuring, cancellationToken);
                }
                return ValueTask.CompletedTask;
        }
        return ValueTask.CompletedTask;
    }

    private async Task ResolveAndRetireSwitchSourceAsync(SentinelHint hint, Generation arrivedDuring,
        CancellationToken cancellationToken, SentinelNotificationCoalescer.SourceResolution? resolution = null)
    {
        lock (_gate) resolution ??= _coalescer.BeginSourceResolution(in hint);
        try
        {
            var oldPrimary = hint.OldPrimary!.Value;
            var addresses = await ResolveAddressesAsync(oldPrimary.Host, cancellationToken).ConfigureAwait(false);
            if (addresses is null) return;

            // Resolve hostname targets before treating a source's fresh DNS as demotion
            // evidence. Both names may now point to the promoted peer, before or after
            // its publication. Their overlap cannot identify that peer as the old owner.
            if (!IPAddress.TryParse(oldPrimary.Host, out _))
            {
                HashSet<string> resolvedTargets = new(StringComparer.OrdinalIgnoreCase);
                foreach (var target in hint.Targets)
                {
                    if (target.Port != oldPrimary.Port || IPAddress.TryParse(target.Host, out _)) continue;
                    var targetAddresses = await ResolveAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
                    if (SentinelDiscoveryState.SingleAddress(target, targetAddresses) is { } targetAddress)
                        resolvedTargets.Add(targetAddress);
                }
                if (resolvedTargets.Count > 0)
                {
                    // Fresh DNS overlap cannot erase the peer known when the event arrived.
                    // Both names may still alias that demoted socket while it answers ROLE master.
                    var knownPeer = arrivedDuring.ValidatedPeer;
                    addresses = Array.FindAll(addresses, address => !resolvedTargets.Contains(address)
                        || knownPeer is { } peer && peer.Port == oldPrimary.Port
                            && StringComparer.OrdinalIgnoreCase.Equals(address, SentinelResolver.NormalizeHost(peer.Host)));
                    if (addresses.Length == 0) return;
                }
            }

            lock (_gate)
            {
                if (_disposed) return;
                var current = Current;
                _coalescer.RetainResolvedOldPrimaryAddresses(oldPrimary, addresses);
                var retained = resolution.Hint.WithSourceAddresses(oldPrimary, addresses);
                // Do not apply an old resolution to a later generation for the same endpoint:
                // a failback can legitimately publish that address again. A changed endpoint
                // is checked so a hostname alias is not lost across an in-flight handoff.
                if (current is null || !ReferenceEquals(current, arrivedDuring)
                    && (SameEndpoint(current.Endpoint, arrivedDuring.Endpoint)
                        || arrivedDuring.ValidatedPeer is { } arrivedPeer
                            && current.Multiplexer.HasCurrentPeer(arrivedPeer.Host, arrivedPeer.Port))) return;
                if (RetireIfSwitchSourceLocked(current, in retained))
                {
                    QueueNotificationRediscoveryCore(retained with { MustRediscover = true });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            SafeLog(error, static (logger, error)
                => logger.LogDebug(error, "Could not retire the primary named by a Sentinel switch event"));
        }
        finally
        {
            lock (_gate) _coalescer.EndSourceResolution(resolution);
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
            var evidence = _coalescer.BeginSourceResolution(in hint);
            resolution = Task.Run(() => ResolveAndRetireSwitchSourceAsync(hint, arrivedDuring, cancellationToken, evidence), CancellationToken.None);
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
        if (IPAddress.TryParse(host, out var literal)) return [SentinelResolver.NormalizeAddress(literal)];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(core.Options.ConnectTimeout);
            var addresses = await HostResolver(host, timeout.Token).ConfigureAwait(false);
            return Array.ConvertAll(addresses, SentinelResolver.NormalizeAddress);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && SentinelExceptionPolicy.IsRecoverable(error))
        {
            SafeLog((error, host), static (logger, state)
                => logger.LogDebug(state.error, "Could not resolve Sentinel switch source {Host}", state.host));
            return null;
        }
    }

    // Ordinary logger failures must not stop monitoring, rediscovery or disposal. Fatal failures propagate.
    private void SafeLog<TState>(TState state, Action<ILogger, TState> log)
    {
        if (core.Logger is not { } logger) return;
        try { log(logger, state); }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            RespireTelemetry.RecordSentinelGuardedLoggingFailure();
        }
    }

    private void LogSentinelEvent(LogLevel level, in RespireMessage message, RespireEndpoint sentinel)
    {
        try
        {
            if (core.Logger?.IsEnabled(level) == true)
                core.Logger.Log(level, "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}",
                    message.Channel.ToString(), core.Options.SentinelPrimaryName, sentinel, message.Text);
        }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            RespireTelemetry.RecordSentinelGuardedLoggingFailure();
        }
    }

    private void QueueDeliveryGapRediscovery(RespireEndpoint sentinel, bool initialSubscription = false)
    {
        SafeLog((sentinel, initialSubscription), static (logger, state) => logger.LogInformation(state.initialSubscription
            ? "Sentinel monitor established at {Sentinel}; revalidating the primary after subscription"
            : "Sentinel event delivery from {Sentinel} had a gap; rediscovering the primary", state.sentinel));
        // Untargeted and never satisfied by an earlier attempt: a missed switch could leave the
        // former primary serving reads as a replica without a disconnect or READONLY reply.
        QueueNotificationRediscovery(SentinelHint.FromGap(sentinel));
    }

    internal void QueueNotificationRediscovery(in SentinelHint hint)
    {
        try
        {
            QueueNotificationRediscoveryCore(in hint);
        }
        finally
        {
            NotificationQueuedObserver?.Invoke();
        }
    }

    private void QueueNotificationRediscoveryCore(in SentinelHint hint)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var current = Current;
            var targetIsCurrent = hint.Target is { } target && current is { IsRetired: false }
                && IsConfirmedTarget(current, target);
            var startWorker = _coalescer.Offer(in hint, targetIsCurrent);
            if (_coalescer.Pending is not null) _pendingNotification.TrySetResult();
            // Compare the switch source with Current under the gate, immediately before retirement.
            // This also covers hints that wait behind an active discovery, so a direct endpoint
            // match never waits for that attempt or for DNS.
            RetireIfSwitchSourceLocked(current, in hint);
            if (startWorker) _notificationRediscovery = Task.Run(RediscoverFromNotificationAsync);
        }
    }

    private async Task RediscoverFromNotificationAsync()
    {
        var failures = 0;
        // Consecutive failed attempts. Only the first of a run logs a warning, so a long Sentinel
        // outage without a ReconnectPolicy does not repeat it every 30 seconds.
        var consecutiveFailures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            var succeeded = false;
            Generation? validated = null;
            var retryDelay = TimeSpan.Zero;
            try
            {
                // One worker owns this deadline across successes and restarts. New hints can
                // interrupt failure backoff, but cannot turn a pub/sub storm into an unbounded
                // sequence of successful Sentinel queries and ROLE checks.
                var delay = _notificationDiscoveryNotBefore - Environment.TickCount64;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _lifetime.Token).ConfigureAwait(false);
                _notificationDiscoveryNotBefore = Environment.TickCount64 + MinimumNotificationDiscoveryIntervalMilliseconds;
                SentinelHint? hint;
                lock (_gate)
                {
                    // Evidence can arrive after TakePending, during backoff or its wake-up.
                    // Spend the remaining retry on that evidence, not the failed reporter again.
                    if (failures > 0 && _coalescer.Pending is not null)
                    {
                        var next = _coalescer.TakePending(activeFailed: true)!.Value;
                        _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        var current = Current;
                        RetireIfSwitchSourceLocked(current, in next);
                    }
                    hint = _coalescer.Active;
                }
                validated = await GetGenerationAsync(_lifetime.Token, forceDiscovery: true, notificationHint: hint).ConfigureAwait(false);
                succeeded = true;
                if (consecutiveFailures > 0)
                    SafeLog(consecutiveFailures, static (logger, count) => logger.LogInformation(
                        "Sentinel notification-triggered primary discovery succeeded after {Failures} failed attempt(s)", count));
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
            {
                SafeLog((error, attempt: ++consecutiveFailures), static (logger, state) => logger.Log(
                    state.attempt == 1 ? LogLevel.Warning : LogLevel.Debug, state.error,
                    "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})", state.attempt));
            }

            lock (_gate)
            {
                if (_disposed) return;
                // Discovery releases its semaphore before this lock. A command may have
                // published another generation meanwhile; old reporters cannot undo it.
                if (succeeded && !ReferenceEquals(validated, Current))
                {
                    if (_coalescer.SupersedeActive() is null)
                    {
                        _notificationRediscovery = null;
                        return;
                    }
                    failures = 0;
                    _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    continue;
                }
                var policy = core.Options.ReconnectPolicy;
                if (!succeeded && policy?.IsExhausted(failures) == true)
                {
                    // Pending hints cannot bypass the shared retry budget.
                    _coalescer.Complete();
                    _notificationRediscovery = null;
                    return;
                }
                if (_coalescer.TakePending(activeFailed: !succeeded, validatedPrimary: validated?.Endpoint,
                    validatedPeer: validated?.ValidatedPeer) is not { } next)
                {
                    // Sentinel publishes each event at most once. Retry a failed hint with backoff,
                    // because a switch may already have retired the current generation. Without a
                    // ReconnectPolicy this retries until discovery succeeds or the client is disposed,
                    // at the default backoff capped at 30 seconds: dropping the hint could leave a
                    // retired generation with no event-driven replacement. A policy's MaxAttempts
                    // bounds it; commands still run discovery on demand after that.
                    if (succeeded)
                    {
                        _coalescer.Complete();
                        _notificationRediscovery = null;
                        return;
                    }
                    retryDelay = GetNotificationRetryDelay(policy, ++failures);
                }
                else
                {
                    _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A newer hint restarts discovery at once. Count each failed attempt against
                    // the same policy budget; only success starts a fresh run.
                    if (succeeded) failures = 0;
                    else failures++;
                    var current = Current;
                    if (!next.MustRediscover && next.Target is { } target && current is { IsRetired: false }
                        && IsConfirmedTarget(current, target))
                    {
                        _coalescer.Complete();
                        _notificationRediscovery = null;
                        return;
                    }
                    RetireIfSwitchSourceLocked(current, in next);
                }
            }

            if (retryDelay > TimeSpan.Zero)
            {
                Task notification;
                lock (_gate)
                {
                    if (_coalescer.Pending is not null) continue;
                    _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    notification = _pendingNotification.Task;
                }
                try
                {
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    var delay = Task.Delay(retryDelay, Clock, retry.Token);
                    if (await Task.WhenAny(delay, notification).ConfigureAwait(false) == notification)
                    {
                        retry.Cancel();
                        continue;
                    }
                    await delay.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            }
        }
    }

    // Caller holds _gate so source matching and admission retirement see one current generation.
    private bool RetireIfSwitchSourceLocked(Generation? current, in SentinelHint hint)
    {
        if (IsAnnouncedTarget(current, in hint) || !IsSwitchSource(current, in hint)) return false;
        Invalidate(current!);
        return true;
    }

    // Whether a switch hint's old primary is the healthy current generation, by announced
    // endpoint or by the resolved addresses of its connected peers.
    private static bool IsSwitchSource(Generation? current, in SentinelHint hint)
    {
        if (current is not { IsRetired: false }) return false;
        foreach (var source in hint.Sources)
            if (IsCurrentPeer(current, source.Endpoint, source.Addresses)) return true;
        return false;
    }

    // A coalesced A→B→A sequence must rediscover, but B can already be a valid current
    // primary. Keep any announced target alive while that fresh discovery runs.
    private static bool IsAnnouncedTarget(Generation? current, in SentinelHint hint)
    {
        if (current is not { IsRetired: false }) return false;
        foreach (var endpoint in hint.Targets)
        {
            if (IsCurrentPeer(current, endpoint, null, allowHostnameIdentity: false)) return true;
            // In a cycle the same hostname can be both source and target. Its resolved
            // addresses must protect a target just as they identify a demoted source.
            foreach (var source in hint.Sources)
                if (SameEndpoint(endpoint, source.Endpoint)
                    && IsCurrentPeer(current, endpoint, source.Addresses)) return true;
        }
        return false;
    }

    // Skipping rediscovery requires every command socket to confirm the target. Source
    // fencing and cycle protection below intentionally continue to match any known peer.
    private static bool IsConfirmedTarget(Generation current, RespireEndpoint endpoint)
        => IPAddress.TryParse(endpoint.Host, out var address)
            && current.Multiplexer.AllCurrentPeersMatch(SentinelResolver.NormalizeAddress(address), endpoint.Port);

    private static bool IsCurrentPeer(Generation current, RespireEndpoint endpoint, string[]? addresses,
        bool allowHostnameIdentity = true)
    {
        var numeric = IPAddress.TryParse(endpoint.Host, out var literal);
        // A hostname can move behind an established socket. Source matching and explicit
        // cycle protection allow textual identity; target shortcuts require peer evidence.
        if ((allowHostnameIdentity || numeric) && SameEndpoint(current.Endpoint, endpoint)) return true;
        if (numeric && current.Multiplexer.HasCurrentPeer(SentinelResolver.NormalizeAddress(literal!), endpoint.Port)) return true;
        if (addresses is not null)
            foreach (var address in addresses)
                if (current.Multiplexer.HasCurrentPeer(address, endpoint.Port)) return true;
        return false;
    }

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => SentinelDiscoveryState.EndpointComparer.Instance.Equals(left, right);

}
