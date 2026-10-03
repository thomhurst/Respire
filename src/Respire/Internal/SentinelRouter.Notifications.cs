using System.Collections.Immutable;
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
                _coalescer.RetainResolvedOldPrimaryAddresses(oldPrimary, addresses, resolution);
                var retained = resolution.Hint;
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
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                SentinelNotificationTransition transition;
                lock (_gate)
                {
                    transition = _coalescer.Transition(new(SentinelNotificationEventKind.PrepareAttempt),
                        new(NowMilliseconds: Environment.TickCount64));
                    ApplyNotificationTransitionLocked(in transition);
                }
                if (transition.Action == SentinelNotificationAction.Stop) return;
                if (transition.Action == SentinelNotificationAction.RetryAfter)
                {
                    // Minimum spacing survives worker completion and cannot be interrupted
                    // by another advisory notification. Policy backoff below can be interrupted.
                    await Task.Delay(transition.Delay, _lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                Generation? validated = null;
                Exception? failure = null;
                try
                {
                    validated = await GetGenerationAsync(_lifetime.Token, forceDiscovery: true,
                        notificationHint: transition.State.Active).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error)) { failure = error; }

                // Logger callbacks run before reconciliation and outside the gate. A callback
                // may allow another publication; capture Current only after it has returned.
                int loggedFailures;
                lock (_gate) loggedFailures = _coalescer.State.GetOutcomeLogCount(failure is null);
                if (failure is not null)
                    SafeLog((error: failure, attempt: loggedFailures), static (logger, state) => logger.Log(
                        state.attempt == 1 ? LogLevel.Warning : LogLevel.Debug, state.error,
                        "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})", state.attempt));
                else if (loggedFailures > 0)
                    SafeLog(loggedFailures, static (logger, count) => logger.LogInformation(
                        "Sentinel notification-triggered primary discovery succeeded after {Failures} failed attempt(s)", count));

                Task notification;
                lock (_gate)
                {
                    if (_disposed) return;
                    var current = Current;
                    transition = _coalescer.Transition(new(failure is null
                        ? SentinelNotificationEventKind.AttemptSucceeded : SentinelNotificationEventKind.AttemptFailed),
                        new(Policy: core.Options.ReconnectPolicy, RandomUnit: Random.Shared.NextDouble(),
                            CurrentGeneration: current, ValidatedGeneration: validated,
                            ValidatedPrimary: validated is null ? null : new(validated.Endpoint, validated.ValidatedPeer),
                            ConfirmedCurrentPeer: current is { IsRetired: false }
                                ? current.Multiplexer.GetConfirmedCurrentPeer() : null));
                    ApplyNotificationTransitionLocked(in transition);
                    notification = _pendingNotification.Task;
                }

                if (transition.Action == SentinelNotificationAction.Stop) return;
                if (transition.Action != SentinelNotificationAction.RetryAfter || transition.Delay <= TimeSpan.Zero) continue;

                using var retry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var delay = Task.Delay(transition.Delay, Clock, retry.Token);
                if (await Task.WhenAny(delay, notification).ConfigureAwait(false) == notification)
                {
                    retry.Cancel();
                    continue;
                }
                await delay.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    // State replacement and pending-signal replacement share the gate. The reducer
    // selects work; only the router can retire transports or execute discovery.
    private void ApplyNotificationTransitionLocked(in SentinelNotificationTransition transition)
    {
        if (transition.ReplacePendingSignal)
            _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (transition.RetireActiveSource && transition.State.Active is { } active)
            RetireIfSwitchSourceLocked(Current, in active);
        if (transition.Action == SentinelNotificationAction.Stop) _notificationRediscovery = null;
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
            if (IsCurrentPeer(current, endpoint, default, allowHostnameIdentity: false)) return true;
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

    private static bool IsCurrentPeer(Generation current, RespireEndpoint endpoint, ImmutableArray<string> addresses,
        bool allowHostnameIdentity = true)
    {
        var numeric = IPAddress.TryParse(endpoint.Host, out var literal);
        // A hostname can move behind an established socket. Source matching and explicit
        // cycle protection allow textual identity; target shortcuts require peer evidence.
        if ((allowHostnameIdentity || numeric) && SameEndpoint(current.Endpoint, endpoint)) return true;
        if (numeric && current.Multiplexer.HasCurrentPeer(SentinelEndpointIdentity.NormalizeAddress(literal!), endpoint.Port)) return true;
        if (!addresses.IsDefaultOrEmpty)
            foreach (var address in addresses)
                if (current.Multiplexer.HasCurrentPeer(address, endpoint.Port)) return true;
        return false;
    }

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left, right);

}
