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

    /// <summary>
    /// Pure reducer. Time, retry policy, jitter, the exact returned generation and validated owner/peer
    /// facts are explicit inputs. Worker decisions return <see cref="SentinelNotificationAction.Stop"/>,
    /// <see cref="SentinelNotificationAction.RunNext"/> or <see cref="SentinelNotificationAction.RetryAfter"/>;
    /// evidence-only changes return <see cref="SentinelNotificationAction.None"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The router replaces pending signals atomically with the state change under its gate, then executes
    /// discovery, waits and retirement. The reducer returns the exact <see cref="SentinelGenerationIdentity"/>
    /// to retire; the router retires it only if it is still current and disposal has not begun.
    /// </para>
    /// <para>
    /// Retry attempts and consecutive failures are separate. Success resets both; pending hints spend the
    /// current retry budget; exhaustion completes active and pending work. Without a policy, retries are
    /// unlimited and backoff caps at 30 s. The <see cref="MinimumDiscoveryIntervalMilliseconds"/> deadline
    /// survives worker completion and restart; notifications can interrupt policy backoff but never that
    /// deadline. Completing a worker clears hints, not counters or the deadline; starting a new idle worker
    /// resets both counters; taking pending work during backoff preserves them.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Attempt outcome</term><description>Retry attempts / consecutive failures → next action</description></listheader>
    /// <item><term>No active hint</term><description>Unchanged / unchanged → Stop.</description></item>
    /// <item><term>Success, superseded by another generation</term><description>Reset / reset (report prior count) →
    /// run genuine pending work, else Stop.</description></item>
    /// <item><term>Success for current generation</term><description>Reset / reset (report prior count) → reconcile
    /// pending work, then run or Stop.</description></item>
    /// <item><term>Failure, exhausted policy</term><description>Unchanged (checked before increment) / saturating
    /// increment → clear active/pending, Stop.</description></item>
    /// <item><term>Failure, budget left, no pending</term><description>Saturating increment / saturating increment →
    /// interruptible policy backoff.</description></item>
    /// <item><term>Failure, budget left, pending</term><description>Saturating increment / saturating increment →
    /// run pending, or Stop if its target is already confirmed and it need not rediscover.</description></item>
    /// </list>
    /// <list type="table">
    /// <listheader><term>State / event</term><description>Transition</description></listheader>
    /// <item><term>Idle, relevant notification</term><description>Make the hint active; start one worker.</description></item>
    /// <item><term>Active, notification</term><description>Retain evidence in the pending hint. Arrival order is not
    /// failover order.</description></item>
    /// <item><term>Active+pending, duplicate of active switch</term><description>Retain the duplicate's reporter without
    /// adding the active switch's source to the independent pending switch.</description></item>
    /// <item><term>Discovery succeeds</term><description>Reconcile unqueried reporters, keeping demotion evidence for other
    /// primaries. Consume source evidence for the validated endpoint or its ROLE-validated socket peer only when
    /// reconciling that completed evidence; independent pending switches keep their fences. Source DNS aliases
    /// must be unambiguous.</description></item>
    /// <item><term>ROLE accepts one peer of a multi-address hostname</term><description>Narrow the epoch owner's DNS
    /// candidates to that physical peer. A numeric fallback may confirm it at the same or missing epoch after the
    /// hostname socket closes; another peer under the same hostname needs a newer epoch. Rejected connected
    /// candidates are released before fallback. Deployments that never exposed an epoch keep metadata-free
    /// recovery.</description></item>
    /// <item><term>Success, no remaining source evidence</term><description>Bind reporter-only reconciliation to the
    /// validated owner and socket peer; a different owner needs a strictly newer epoch.</description></item>
    /// <item><term>Success, same master-down outage pending</term><description>Bind reconciliation to the validated owner.
    /// The affected endpoint (numeric-normalized, hostname case-folded, port kept) identifies the outage regardless of
    /// reporter, channel and quorum count.</description></item>
    /// <item><term>Down report after its outage worker completed</term><description>Keep each affected endpoint with its
    /// reporter; capture the current validated owner after taking discovery ownership. Each Sentinel uses only its own
    /// down evidence: reports about superseded owners and unreported fallback Sentinels reconcile the current owner unless
    /// a newer epoch authorizes movement; current-owner reports stay independent, including after failback. Check numeric
    /// evidence first, then resolve hostname reports concurrently within one separate alias deadline, requiring
    /// unambiguous validated-peer identity; cancel and join losing lookups. Unknown aliases keep the fence without
    /// spending the candidate's DNS/connection/ROLE deadline; caller cancellation still wins.</description></item>
    /// <item><term>DNS moves around a hostname down report</term><description>Keep the endpoint and peer validated at receipt
    /// in that reporter's evidence. A hostname report authorizes recovery only while the observed owner is current and fresh
    /// DNS unambiguously matches its validated peer; text equality alone never identifies the outage, and old evidence is
    /// never rebound to a later owner through fresh DNS. Hostname outage identity includes the observed peer so the next
    /// owner's outage is not mistaken for the completed one. Numeric reports identify their endpoint directly.</description></item>
    /// <item><term>Success, independent down/gap pending</term><description>Discover again without binding that hint to the
    /// completed primary; it may describe a later promotion, including without epoch metadata.</description></item>
    /// <item><term>Discovery fails</term><description>Keep source evidence; prioritize unqueried reporters; retry with bounded
    /// backoff.</description></item>
    /// <item><term>Another discovery publishes a different generation</term><description>Discard superseded active evidence,
    /// keep pending notifications, continue from the published generation.</description></item>
    /// <item><term>No pending evidence or reporter</term><description>Complete the worker; a later event starts another.</description></item>
    /// <item><term>Disposal</term><description>Cancel work; prevent later publication under the router gate.</description></item>
    /// </list>
    /// </remarks>
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
                ?? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(state.RetryAttempts - 1, 5)));
            return new(state, SentinelNotificationAction.RetryAfter, delay,
                Interruptible: true, ReplacePendingSignal: true);
        }
        if (!next.Value.MustRediscover && next.Value.Target is { } target
            && context.CurrentEvidence?.ConfirmsTarget(target) == true)
            return new(state.Complete(), SentinelNotificationAction.Stop, RecoveredFailures: recovered);
        return new(state, SentinelNotificationAction.RunNext, ReplacePendingSignal: true,
            RecoveredFailures: recovered, RetireGeneration: RetirementFor(next.Value, in context));
    }

    private static int IncrementSaturated(int count) => count < int.MaxValue ? count + 1 : count;

    internal int GetOutcomeLogCount(bool succeeded)
        => succeeded ? ConsecutiveFailures : IncrementSaturated(ConsecutiveFailures);

    private static SentinelGenerationIdentity? RetirementFor(in SentinelHint hint, in SentinelNotificationContext context)
        => context.CurrentEvidence is { } current && current.ShouldRetire(in hint) ? current.Identity : null;
}
