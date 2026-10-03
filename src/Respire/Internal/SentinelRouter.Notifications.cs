using System.Net;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Event evidence and the single notification discovery worker.
internal sealed partial class SentinelRouter
{
    private static readonly TimeSpan NotificationShutdownTimeout = TimeSpan.FromSeconds(10);
    private ValueTask ObserveSentinelEventAsync(RespireEndpoint sentinel, SentinelEvent sentinelEvent,
        string? malformedSwitch, CancellationToken cancellationToken)
    {
        switch (sentinelEvent.Kind)
        {
            case SentinelEventKind.ReplicaDown:
                // Replica events never move the primary; keep them out of Information logs.
                return ValueTask.CompletedTask;
            case SentinelEventKind.MasterDown:
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
                // Queue immediately so slow DNS cannot hold up later one-shot notifications.
                // Every Sentinel in the quorum announces the same parsed switch, so key on it.
                var hint = SentinelHint.FromSwitchMaster(sentinelEvent is { OldPrimary: { } from, NewPrimary: { } to }
                        ? $"+switch-master:{from.Host}:{from.Port}>{to.Host}:{to.Port}"
                        : "+switch-master:" + malformedSwitch,
                    sentinelEvent.OldPrimary, sentinelEvent.NewPrimary, sentinel);
                // Only the generation current when the event arrived can be its source. A later
                // failover back to the same endpoint publishes a new generation that must survive.
                lock (_gate)
                {
                    var arrivedDuring = Current;
                    if (hint.OldPrimary is { } announcedSource && arrivedDuring?.ValidatedPeer is { } peer
                        && SameEndpoint(arrivedDuring.Endpoint, announcedSource))
                        hint = hint.WithSourceAddresses(announcedSource, [SentinelEndpointIdentity.NormalizeHost(peer.Host)]);
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
            var addresses = await Monitoring.ResolveAddressesAsync(oldPrimary.Host, cancellationToken).ConfigureAwait(false);
            if (addresses is null) return;

            // Resolve hostname targets before treating a source's fresh DNS as demotion
            // evidence. Both names may now point to the promoted peer, before or after
            // its publication. Their overlap cannot identify that peer as the old owner.
            if (!IPAddress.TryParse(oldPrimary.Host, out _))
            {
                HashSet<string> resolvedTargets = new(SentinelEndpointIdentity.AddressComparer.Instance);
                foreach (var target in hint.Targets)
                {
                    if (target.Port != oldPrimary.Port || IPAddress.TryParse(target.Host, out _)) continue;
                    var targetAddresses = await Monitoring.ResolveAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
                    if (new SentinelAddressEvidence(target, targetAddresses).SingleAddress is { } targetAddress)
                        resolvedTargets.Add(targetAddress);
                }
                if (resolvedTargets.Count > 0)
                {
                    // Fresh DNS overlap cannot erase the peer known when the event arrived.
                    // Both names may still alias that demoted socket while it answers ROLE master.
                    var knownPeer = arrivedDuring.ValidatedPeer;
                    addresses = Array.FindAll(addresses, address => !resolvedTargets.Contains(address)
                        || knownPeer is { } peer && peer.Port == oldPrimary.Port
                            && SentinelEndpointIdentity.AddressComparer.Instance.Equals(address, peer.Host));
                    if (addresses.Length == 0) return;
                }
            }

            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
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

    // Evidence capture and task registration share disposal's gate. The background owner
    // starts asynchronous work without running the resolver inline under the gate.
    private void StartSwitchSourceResolution(SentinelHint hint, Generation arrivedDuring, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var evidence = _coalescer.BeginSourceResolution(in hint);
            Background.TryStart(SentinelWorkKind.SourceResolution,
                () => ResolveAndRetireSwitchSourceAsync(hint, arrivedDuring, cancellationToken, evidence));
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

    private void QueueDeliveryGapRediscovery(RespireEndpoint sentinel, long startupVersion, CancellationToken cancellationToken)
    {
        var initialSubscription = startupVersion > 0;
        SafeLog((sentinel, initialSubscription), static (logger, state) => logger.LogInformation(state.initialSubscription
            ? "Sentinel monitor established at {Sentinel}; revalidating the primary after subscription"
            : "Sentinel event delivery from {Sentinel} had a gap; rediscovering the primary", state.sentinel));
        // Only first-subscription gaps can be covered by a discovery begun after attachment.
        // Reconnect and overflow gaps remain independent, mandatory rediscovery hints.
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return;
            QueueNotificationRediscovery(SentinelHint.FromGap(sentinel) with
            {
                StartupSubscriptionVersion = startupVersion,
            });
        }
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
            if (startWorker) _notificationRediscovery = Background.TryStart(SentinelWorkKind.Rediscovery, RediscoverFromNotificationAsync);
        }
    }

    private async Task RediscoverFromNotificationAsync()
    {
        var budget = new SentinelRetryBudget(core.Options.ReconnectPolicy);
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
                    if (_disposed) return;
                    // Evidence can arrive after TakePending, during backoff or its wake-up.
                    // Spend the remaining retry on that evidence, not the failed reporter again.
                    if (budget.Attempts > 0 && _coalescer.Pending is not null)
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
                    budget.Reset();
                    _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    continue;
                }
                if (!succeeded && !budget.TryStartRetry())
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
                    retryDelay = budget.GetDelay();
                }
                else
                {
                    _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A newer hint restarts discovery at once. Count each failed attempt against
                    // the same policy budget; only success starts a fresh run.
                    if (succeeded) budget.Reset();
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
        if (_disposed || IsAnnouncedTarget(current, in hint) || !IsSwitchSource(current, in hint)) return false;
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
            && current.Multiplexer.AllCurrentPeersMatch(SentinelEndpointIdentity.NormalizeAddress(address), endpoint.Port);

    private static bool IsCurrentPeer(Generation current, RespireEndpoint endpoint, string[]? addresses,
        bool allowHostnameIdentity = true)
    {
        var numeric = IPAddress.TryParse(endpoint.Host, out var literal);
        // A hostname can move behind an established socket. Source matching and explicit
        // cycle protection allow textual identity; target shortcuts require peer evidence.
        if ((allowHostnameIdentity || numeric) && SameEndpoint(current.Endpoint, endpoint)) return true;
        if (numeric && current.Multiplexer.HasCurrentPeer(SentinelEndpointIdentity.NormalizeAddress(literal!), endpoint.Port)) return true;
        if (addresses is not null)
            foreach (var address in addresses)
                if (current.Multiplexer.HasCurrentPeer(address, endpoint.Port)) return true;
        return false;
    }

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left, right);

}
