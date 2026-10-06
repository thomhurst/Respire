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
                    hint = hint.CaptureObservationContext(arrivedDuring is null ? null
                        : new(arrivedDuring.Endpoint, arrivedDuring.ValidatedPeer), _discovery.EpochEvidence);
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

            // I/O collects facts only. The reducer decides whether target aliases
            // disqualify these addresses as evidence of a demoted source.
            var targets = ImmutableArray.CreateBuilder<SentinelAddressEvidence>();
            if (!IPAddress.TryParse(oldPrimary.Host, out _))
            {
                foreach (var target in hint.Targets)
                {
                    if (target.Port != oldPrimary.Port || IPAddress.TryParse(target.Host, out _)) continue;
                    var targetAddresses = await Monitoring.ResolveAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
                    targets.Add(new(target, targetAddresses));
                }
            }
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                var transition = _coalescer.Transition(new(SentinelNotificationEventKind.SourceResolved,
                    ResolutionId: resolution.Id, AddressEvidence: new(oldPrimary, addresses), TargetAddresses: targets.ToImmutable()),
                    new(CurrentEvidence: CaptureGenerationEvidence(Current),
                        LookupGeneration: CaptureGenerationEvidence(arrivedDuring)));
                ApplyQueuedNotificationTransitionLocked(in transition);
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
            var observed = hint.CaptureObservationContext(current is null ? null
                : new(current.Endpoint, current.ValidatedPeer), _discovery.EpochEvidence);
            var transition = _coalescer.Transition(new(SentinelNotificationEventKind.Offer, observed),
                new(CurrentEvidence: CaptureGenerationEvidence(current)));
            ApplyQueuedNotificationTransitionLocked(in transition);
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
                        new(NowMilliseconds: Clock.GetElapsedTime(0, Clock.GetTimestamp()).Ticks / TimeSpan.TicksPerMillisecond,
                            CurrentEvidence: CaptureGenerationEvidence(Current)));
                    ApplyNotificationTransitionLocked(in transition);
                }
                if (transition.Action == SentinelNotificationAction.Stop) return;
                if (transition.Action == SentinelNotificationAction.RetryAfter)
                {
                    // Minimum spacing survives worker completion and cannot be interrupted
                    // by another advisory notification. Policy backoff below can be interrupted.
                    await WaitForNotificationRetryAsync(transition, Task.CompletedTask).ConfigureAwait(false);
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
                            ValidatedGeneration: validated?.Identity,
                            ValidatedPrimary: validated is null ? null : new(validated.Endpoint, validated.ValidatedPeer),
                            CurrentEvidence: CaptureGenerationEvidence(current)));
                    ApplyNotificationTransitionLocked(in transition);
                    notification = _pendingNotification.Task;
                }

                if (transition.Action == SentinelNotificationAction.Stop) return;
                if (transition.Action != SentinelNotificationAction.RetryAfter || transition.Delay <= TimeSpan.Zero) continue;

                await WaitForNotificationRetryAsync(transition, notification).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task WaitForNotificationRetryAsync(SentinelNotificationTransition transition, Task notification)
    {
        if (!transition.Interruptible)
        {
            await Task.Delay(transition.Delay, Clock, _lifetime.Token).ConfigureAwait(false);
            return;
        }
        using var retry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var delay = Task.Delay(transition.Delay, Clock, retry.Token);
        if (await Task.WhenAny(delay, notification).ConfigureAwait(false) == notification)
        {
            await retry.CancelAsync().ConfigureAwait(false);
            return;
        }
        await delay.ConfigureAwait(false);
    }

    // State replacement and pending-signal replacement share the gate. The reducer
    // selects work; only the router can retire transports or execute discovery.
    private void ApplyNotificationTransitionLocked(in SentinelNotificationTransition transition)
    {
        if (transition.ReplacePendingSignal)
            _pendingNotification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (transition.State.Pending is not null) _pendingNotification.TrySetResult();
        if (!_disposed && Current is { } generation && ReferenceEquals(transition.RetireGeneration, generation.Identity))
            Invalidate(generation);
        if (transition.Action == SentinelNotificationAction.Stop) _notificationRediscovery = null;
    }

    private void ApplyQueuedNotificationTransitionLocked(in SentinelNotificationTransition transition)
    {
        ApplyNotificationTransitionLocked(in transition);
        // Queued Offer/SourceResolved events return RunNext only when activating idle
        // state. Attempt outcomes continue inside the existing worker instead.
        // TryStart refuses only after Stop. Disposal marks the reducer disposed and
        // stops background registration under this same gate, so RunNext cannot be refused.
        if (transition.Action == SentinelNotificationAction.RunNext)
            _notificationRediscovery = Background.TryStart(SentinelWorkKind.Rediscovery, RediscoverFromNotificationAsync);
    }

    private static SentinelGenerationEvidence? CaptureGenerationEvidence(Generation? generation)
    {
        if (generation is null) return null;
        var peers = generation.Multiplexer.CaptureSentinelPeers();
        return new(generation.Identity, generation.Endpoint, generation.ValidatedPeer, generation.IsRetired,
            peers.Peers, peers.ConfirmedPeer);
    }

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left, right);

}
