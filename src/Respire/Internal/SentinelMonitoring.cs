using System.Net;
using Microsoft.Extensions.Logging;
using Respire.Protocol;

namespace Respire.Internal;

// Owns Sentinel subscriptions and their transport lifecycle. Generation publication,
// retirement, and evidence reconciliation remain callbacks guarded by the router.
// received runs synchronously under gate and must not block or perform network I/O;
// its asynchronous continuation is awaited after releasing gate. deliveryGap runs
// outside gate with a captured startup version (zero means an independent gap).
// The router rechecks disposal when consuming either callback.
internal sealed class SentinelMonitoring(
    RespireOptions options, ILogger? logger, object gate, SentinelDiscoveryState discovery,
    CancellationTokenSource lifetime,
    Func<RespireEndpoint, SentinelEvent, string?, CancellationToken, ValueTask> received,
    Action<RespireEndpoint, long, CancellationToken> deliveryGap,
    SentinelBackgroundWork? background = null)
{
    private readonly object _gate = gate;
    private readonly SentinelDiscoveryState _discovery = discovery;
    private readonly CancellationTokenSource _lifetime = lifetime;
    private bool _disposed;
    private readonly SentinelBackgroundWork _background = background ?? new(gate);
    private readonly Dictionary<RespireEndpoint, EndpointMonitor> _notificationMonitors = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private Task _notificationMonitorSupervisor = Task.CompletedTask;
    private readonly HashSet<RespireEndpoint> _subscribedSentinels = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private readonly HashSet<RespireEndpoint> _readySentinels = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private TaskCompletionSource _monitorRearm = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _subscriptionVersion;
    private readonly Dictionary<RespireEndpoint, long> _validatedSubscriptions = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private readonly byte[] _serviceNameUtf8 = System.Text.Encoding.UTF8.GetBytes(options.SentinelPrimaryName ?? "");
    private TaskCompletionSource _readinessChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class EndpointMonitor(CancellationToken lifetime, long membershipVersion)
    {
        internal readonly long MembershipVersion = membershipVersion;
        internal readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Task Task = Task.CompletedTask;
    }

    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    internal Func<RespireOptions, ISentinelMonitorClient> ClientFactory { get; set; }
        = static options => new SentinelMonitorClient(options);
    internal Func<string, CancellationToken, Task<IPAddress[]>> HostResolver { get; set; } = Dns.GetHostAddressesAsync;
    internal int SubscribedCount { get { lock (_gate) return _readySentinels.Count; } }
    internal long SubscriptionVersion { get { lock (_gate) return _subscriptionVersion; } }
    internal async Task WaitForSubscriptionsAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_readySentinels.Count >= count) return;
                changed = _readinessChanged.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    internal bool NeedsStartupValidation(RespireEndpoint reporter, long version)
    {
        lock (_gate) return !_validatedSubscriptions.TryGetValue(reporter, out var validated) || version > validated;
    }

    // Capture the version before discovery starts. A lookup already in flight when a
    // subscription attaches cannot prove that its preceding delivery gap was covered.
    internal void Validated(RespireEndpoint reporter, long version)
    {
        lock (_gate)
        {
            _validatedSubscriptions.TryGetValue(reporter, out var validated);
            _validatedSubscriptions[reporter] = Math.Max(validated, version);
        }
    }

    internal void SubscriptionEstablished(RespireEndpoint endpoint, bool first, CancellationToken cancellationToken = default)
    {
        long startupVersion = 0;
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            // A replacement monitor task has fresh local state, but this endpoint's
            // earlier subscription means the restart is a real delivery gap.
            if (first && _subscribedSentinels.Add(endpoint)) startupVersion = ++_subscriptionVersion;
        }
        // If the callback throws, deliberately leave a new endpoint unready. The monitor
        // recovery loop retries the subscription; a finally block must not publish readiness.
        deliveryGap(endpoint, startupVersion, cancellationToken);
        // Readiness must not become visible before the router queues this subscription's
        // validation. Moving the callback outside gate must preserve that startup fence.
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            if (_readySentinels.Add(endpoint)) SignalReadinessChanged();
        }
    }

    private void SignalReadinessChanged()
    {
        var changed = _readinessChanged;
        _readinessChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    // The same gate serializes router disposal, publication, and monitor registration.
    internal void Published()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Start();
            var rearm = Volatile.Read(ref _monitorRearm);
            Volatile.Write(ref _monitorRearm, new(TaskCreationOptions.RunContinuationsAsynchronously));
            rearm.TrySetResult();
        }
    }

    internal Task[] Stop()
    {
        lock (_gate)
        {
            _disposed = true;
            SignalReadinessChanged();
            foreach (var monitor in _notificationMonitors.Values)
                _ = monitor.Task.ContinueWith(static (_, state) => ((EndpointMonitor)state!).Lifetime.Dispose(),
                    monitor, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _background.Stop();
        }
    }

    private ValueTask ObserveMessageAsync(RespireEndpoint sentinel, RespireMessage message, CancellationToken token)
    {
        if (message.Kind == RespireMessageKind.Gap)
        {
            lock (_gate) if (_disposed || token.IsCancellationRequested) return ValueTask.CompletedTask;
            deliveryGap(sentinel, 0, token);
            return ValueTask.CompletedTask;
        }
        if (message.Kind != RespireMessageKind.Message || _serviceNameUtf8.Length == 0)
            return ValueTask.CompletedTask;
        var parsed = SentinelEvent.Parse(message.Channel.Span, message.Payload.Span, _serviceNameUtf8);
        if (parsed.Kind == SentinelEventKind.None) return ValueTask.CompletedTask;
        LogSentinelEvent(parsed.Kind == SentinelEventKind.ReplicaDown ? LogLevel.Debug : LogLevel.Information, message, sentinel);
        var malformed = parsed.Kind == SentinelEventKind.SwitchMaster
            && (parsed.OldPrimary is null || parsed.NewPrimary is null) ? message.Text : null;
        lock (_gate)
            return _disposed || token.IsCancellationRequested ? ValueTask.CompletedTask : received(sentinel, parsed, malformed, token);
    }

    private static readonly TimeSpan SentinelMonitorSupervisorFallbackInterval = TimeSpan.FromSeconds(30);
    private const string SentinelMonitorReconnectScope = "sentinel-monitor";

    // Monitoring starts after the first validated publication. Until then every command runs
    // discovery itself, and a client whose initial discovery keeps failing has no primary to move.
    private void Start()
    {
        if (_notificationMonitorSupervisor != Task.CompletedTask) return;
        _notificationMonitorSupervisor = _background.TryStart(SentinelWorkKind.Supervisor, MonitorSentinelsAsync)
            ?? Task.CompletedTask;
    }

    private async Task MonitorSentinelsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var memberships = _discovery.MembershipSnapshot(out var changed);
                List<(RespireEndpoint Endpoint, Exception? Error)>? restarted = null;
                lock (_gate)
                {
                    if (_disposed) return;
                    var current = memberships.ToDictionary(item => item.Endpoint, item => item.Version,
                        SentinelEndpointIdentity.EndpointComparer.Instance);
                    foreach (var removed in _notificationMonitors.Where(pair =>
                        !current.TryGetValue(pair.Key, out var version) || version != pair.Value.MembershipVersion)
                        .Select(pair => pair.Key).ToArray())
                    {
                        var monitor = _notificationMonitors[removed];
                        _notificationMonitors.Remove(removed);
                        _readySentinels.Remove(removed);
                        // CancelAsync marks the token before running callbacks asynchronously.
                        // Late messages/readiness now fail the token check under this gate.
                        var cancellation = monitor.Lifetime.CancelAsync();
                        _background.TryStart(SentinelWorkKind.MonitorRemoval, async () =>
                        {
                            try { await CleanupTasks.WhenAllAsync([cancellation, monitor.Task]).ConfigureAwait(false); }
                            finally { monitor.Lifetime.Dispose(); }
                        });
                        SignalReadinessChanged();
                    }
                    foreach (var membership in memberships)
                    {
                        var endpoint = membership.Endpoint;
                        if (_notificationMonitors.TryGetValue(endpoint, out var monitor) && !monitor.Task.IsCompleted) continue;
                        if (monitor is not null)
                        {
                            (restarted ??= []).Add((endpoint, monitor.Task.Exception));
                            monitor.Lifetime.Dispose();
                        }
                        monitor = new(_lifetime.Token, membership.Version);
                        var started = monitor;
                        monitor.Task = _background.TryStart(SentinelWorkKind.Monitor,
                            () => MonitorSentinelAsync(endpoint, started.Lifetime.Token)) ?? Task.CompletedTask;
                        _notificationMonitors[endpoint] = monitor;
                    }
                }
                if (restarted is not null)
                    foreach (var restart in restarted)
                        SafeLog(restart, static (logger, state) => logger.LogWarning(state.Error,
                            "Restarting an unexpectedly completed Sentinel event monitor at {Endpoint}", state.Endpoint));
                // Wake on additions and removals, without periodically polling membership.
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
        var budget = new SentinelRetryBudget(options.ReconnectPolicy);
        var subscribedBefore = false;
        // Captured when a reconnect episode starts, not when the monitor parks: a publication
        // that lands while the last attempts are failing must still grant the fresh budget.
        Task rearm = CurrentMonitorRearm();
        while (!cancellationToken.IsCancellationRequested)
        {
            var subscriptionReconnectExhausted = false;
            ISentinelMonitorClient? client = null;
            ISentinelMonitorSubscription? subscription = null;
            try
            {
                client = ClientFactory(CreateOptions(options, endpoint,
                    () =>
                    {
                        // Temporary clients that close before SUBSCRIBE succeeds belong to
                        // the same outer retry episode. Preserve any publication it captured.
                        if (budget.Attempts == 0)
                            Volatile.Write(ref rearm, CurrentMonitorRearm());
                    }));
                subscription = await client.SubscribeAsync(cancellationToken).ConfigureAwait(false);
                budget.Reset();
                // The close callback captures the current epoch for each reconnect episode.
                // Do not overwrite it here: the socket may already have closed and a publication
                // may already have completed that captured epoch before this continuation runs.
                // The first subscription follows initial discovery; reconnects can miss events
                // while disconnected. Revalidate after either subscription is established.
                SubscriptionEstablished(endpoint, !subscribedBefore, cancellationToken);
                subscribedBefore = true;
                await foreach (var message in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
                    await ObserveMessageAsync(endpoint, message, cancellationToken).ConfigureAwait(false);
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
                try
                {
                    await DisposeMonitorResourcesAsync(shutdown ? client : subscription,
                        shutdown ? subscription : client, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && SentinelExceptionPolicy.IsRecoverable(error)
                    && (error is not AggregateException aggregate
                        || aggregate.Flatten().InnerExceptions.All(SentinelExceptionPolicy.IsRecoverable)))
                {
                    SafeLog((error, endpoint), static (logger, state)
                        => logger.LogDebug(state.error, "Sentinel event monitor cleanup failed at {Endpoint}", state.endpoint));
                }
            }
            if (cancellationToken.IsCancellationRequested) return;

            // MaxAttempts counts replacement attempts, as on the other reconnect paths, so the
            // initial subscription failure still receives a retry.
            if (subscriptionReconnectExhausted) budget.MarkSubscriptionExhausted();
            if (!budget.TryStartRetry())
            {
                // Surface the lost fast path: respire.connection.reconnect.exhausted with
                // respire.reconnect.scope=sentinel-monitor, plus a warning.
                if (!subscriptionReconnectExhausted)
                    RespireTelemetry.RecordDiscoveryReconnect(endpoint, SentinelMonitorReconnectScope, budget.Attempts, null, logger);
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
                budget.Reset();
                rearm = CurrentMonitorRearm();
                continue;
            }
            var delay = budget.GetDelay();
            RespireTelemetry.RecordDiscoveryReconnect(endpoint, SentinelMonitorReconnectScope, budget.Attempts, delay, logger);
            try { await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    internal Task CurrentMonitorRearm()
        => Volatile.Read(ref _monitorRearm).Task;

    private static async ValueTask DisposeMonitorResourcesAsync(IAsyncDisposable? first, IAsyncDisposable? second,
        CancellationToken cancellationToken)
    {
        var firstCleanup = Dispose(first);
        if (!cancellationToken.IsCancellationRequested)
        {
            // Normally unsubscribe before closing the client. If shutdown interrupts a
            // stalled unsubscribe, start client cleanup too, without losing the first task.
            try { await firstCleanup.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* The join below retains every cleanup failure. */ }
        }
        await CleanupTasks.WhenAllAsync([firstCleanup, Dispose(second)]).ConfigureAwait(false);

        static Task Dispose(IAsyncDisposable? resource)
        {
            try { return resource?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(error); }
        }
    }

    internal static RespireOptions CreateOptions(RespireOptions options, RespireEndpoint endpoint,
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

    // Run address resolution after queueing the one-shot notification, so DNS cannot hold up
    // failover discovery or prevent this monitor from reading later events.
    internal async ValueTask<string[]?> ResolveAddressesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_disposed) return null;
        if (IPAddress.TryParse(host, out var literal)) return [SentinelEndpointIdentity.NormalizeAddress(literal)];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectTimeout);
            Task<IPAddress[]> resolution;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed) return null;
                // Starting DNS shares Stop's gate; its asynchronous completion must not.
                // The internal resolver seam must return its task without blocking.
                resolution = HostResolver(host, timeout.Token);
            }
            var addresses = await resolution.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Stop closes ownership before linked cancellation callbacks necessarily run.
            lock (_gate) if (_disposed) return null;
            return Array.ConvertAll(addresses, SentinelEndpointIdentity.NormalizeAddress);
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
        if (logger is null) return;
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
            if (logger?.IsEnabled(level) == true)
                logger.Log(level, "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}",
                    message.Channel.ToString(), options.SentinelPrimaryName, sentinel, message.Text);
        }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            RespireTelemetry.RecordSentinelGuardedLoggingFailure();
        }
    }

}
