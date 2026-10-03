using System.Collections.Immutable;

namespace Respire.Internal;

internal enum SentinelNotificationEventKind
{
    Offer,
    PrepareAttempt,
    AttemptSucceeded,
    AttemptFailed,
    BeginSourceResolution,
    EndSourceResolution,
    SourceResolved,
    Dispose,
}

internal readonly record struct SentinelNotificationEvent(SentinelNotificationEventKind Kind,
    SentinelHint Hint = default, bool TargetIsCurrent = false,
    long ResolutionId = 0, SentinelAddressEvidence AddressEvidence = default,
    ImmutableArray<SentinelAddressEvidence> TargetAddresses = default);

// Generation identities are opaque tokens. The reducer reads only captured values;
// it never asks a live generation, transport, clock, or random source for information.
internal readonly record struct SentinelNotificationContext(
    long NowMilliseconds = 0,
    RespireReconnectPolicy? Policy = null,
    double RandomUnit = 0.5,
    SentinelGenerationIdentity? ValidatedGeneration = null,
    SentinelValidatedPrimary? ValidatedPrimary = null,
    SentinelGenerationEvidence? CurrentEvidence = null,
    SentinelGenerationEvidence? LookupGeneration = null);

internal enum SentinelNotificationAction
{
    None,
    Stop,
    RunNext,
    RetryAfter,
}

internal readonly record struct SentinelNotificationTransition(
    SentinelNotificationState State,
    SentinelNotificationAction Action,
    TimeSpan Delay = default,
    bool Interruptible = false,
    bool ReplacePendingSignal = false,
    int RecoveredFailures = 0,
    long ResolutionId = 0,
    SentinelGenerationIdentity? RetireGeneration = null);

internal readonly partial record struct SentinelNotificationState
{
    internal const int MinimumDiscoveryIntervalMilliseconds = 100;

    internal SentinelNotificationTransition Transition(in SentinelNotificationEvent notification,
        in SentinelNotificationContext context = default)
    {
        if (notification.Kind == SentinelNotificationEventKind.Dispose)
            return new(Complete() with { IsDisposed = true }, SentinelNotificationAction.Stop);
        // A cancelled lookup still releases its retained record after disposal.
        if (notification.Kind == SentinelNotificationEventKind.EndSourceResolution)
            return new(EndSourceResolution(notification.ResolutionId), SentinelNotificationAction.None);
        if (IsDisposed) return new(this, SentinelNotificationAction.Stop);

        switch (notification.Kind)
        {
            case SentinelNotificationEventKind.Offer:
                var targetIsCurrent = notification.TargetIsCurrent || notification.Hint.Target is { } target
                    && context.CurrentEvidence?.ConfirmsTarget(target) == true;
                var offered = Offer(notification.Hint, targetIsCurrent, out var start);
                if (start) offered = offered with { RetryAttempts = 0, ConsecutiveFailures = 0 };
                return new(offered, start ? SentinelNotificationAction.RunNext : SentinelNotificationAction.None,
                    RetireGeneration: RetirementFor(notification.Hint, in context));
            case SentinelNotificationEventKind.PrepareAttempt:
                if (Active is null) return new(this, SentinelNotificationAction.Stop);
                if (context.NowMilliseconds < DiscoveryNotBefore)
                    return new(this, SentinelNotificationAction.RetryAfter,
                        TimeSpan.FromMilliseconds(DiscoveryNotBefore - context.NowMilliseconds));
                var prepared = this with
                {
                    DiscoveryNotBefore = context.NowMilliseconds + MinimumDiscoveryIntervalMilliseconds,
                };
                // Evidence arriving during backoff spends the existing retry budget.
                var takePending = RetryAttempts > 0 && Pending is not null;
                if (takePending) prepared = prepared.TakePending(true, null, null, out _);
                return new(prepared, SentinelNotificationAction.RunNext,
                    ReplacePendingSignal: takePending,
                    RetireGeneration: takePending ? RetirementFor(prepared.Active!.Value, in context) : null);
            case SentinelNotificationEventKind.AttemptSucceeded:
                return FinishAttempt(true, in context);
            case SentinelNotificationEventKind.AttemptFailed:
                return FinishAttempt(false, in context);
            case SentinelNotificationEventKind.BeginSourceResolution:
                var resolving = BeginSourceResolution(notification.Hint, out var id);
                return new(resolving, SentinelNotificationAction.None, ResolutionId: id);
            case SentinelNotificationEventKind.SourceResolved:
                return ResolveSourceEvidence(in notification, in context);
            default:
                throw new ArgumentOutOfRangeException(nameof(notification));
        }
    }

    private SentinelNotificationTransition FinishAttempt(bool succeeded, in SentinelNotificationContext context)
    {
        if (Active is null) return new(this, SentinelNotificationAction.Stop);
        var recovered = succeeded ? ConsecutiveFailures : 0;
        var state = this with
        {
            ConsecutiveFailures = succeeded ? 0 : GetOutcomeLogCount(false),
        };
        // The generation returned by discovery, rather than whichever owner is current
        // afterward, determines whether its old active reporters may be reconciled.
        if (succeeded && !ReferenceEquals(context.ValidatedGeneration, context.CurrentEvidence?.Identity))
        {
            state = state.SupersedeActive() with { RetryAttempts = 0 };
            return new(state, state.Active is null ? SentinelNotificationAction.Stop : SentinelNotificationAction.RunNext,
                ReplacePendingSignal: state.Active is not null, RecoveredFailures: recovered);
        }
        if (!succeeded)
        {
            if (context.Policy?.IsExhausted(RetryAttempts) == true)
                return new(state.Complete(), SentinelNotificationAction.Stop);
            state = state with { RetryAttempts = IncrementSaturated(RetryAttempts) };
        }
        else state = state with { RetryAttempts = 0 };

        state = state.TakePending(!succeeded, context.ValidatedPrimary?.Endpoint,
            context.ValidatedPrimary?.Peer, out var next);
        if (next is null)
        {
            if (succeeded)
                return new(state.Complete(), SentinelNotificationAction.Stop, RecoveredFailures: recovered);
            var delay = context.Policy?.GetDelay(state.RetryAttempts, context.RandomUnit)
                ?? DefaultRetryDelay(state.RetryAttempts);
            return new(state, SentinelNotificationAction.RetryAfter, delay,
                Interruptible: true, ReplacePendingSignal: true);
        }
        if (!next.Value.MustRediscover && next.Value.Target is { } target
            && context.CurrentEvidence?.ConfirmsTarget(target) == true)
            return new(state.Complete(), SentinelNotificationAction.Stop, RecoveredFailures: recovered);
        return new(state, SentinelNotificationAction.RunNext, ReplacePendingSignal: true,
            RecoveredFailures: recovered, RetireGeneration: RetirementFor(next.Value, in context));
    }

    private const int DefaultRetryDelayCapSeconds = 30;

    // RespireOptions.ReconnectPolicy is optional. Without one, notification retries back off
    // exponentially from 1s to 30s, uncapped in count and without jitter.
    internal static TimeSpan DefaultRetryDelay(int retryAttempts)
        => TimeSpan.FromSeconds(Math.Min(DefaultRetryDelayCapSeconds, 1 << Math.Clamp(retryAttempts - 1, 0, 5)));

    private static int IncrementSaturated(int count) => count < int.MaxValue ? count + 1 : count;

    internal int GetOutcomeLogCount(bool succeeded)
        => succeeded ? ConsecutiveFailures : IncrementSaturated(ConsecutiveFailures);

    private static SentinelGenerationIdentity? RetirementFor(in SentinelHint hint, in SentinelNotificationContext context)
        => context.CurrentEvidence is { } current && current.ShouldRetire(in hint) ? current.Identity : null;
}
