namespace Respire.Internal;

/// <summary>
/// Gate-owned adapter. Evidence transitions return immutable state; the router owns
/// synchronization and I/O. Compatibility methods also expose those transitions to tests.
/// </summary>
/// <remarks>
/// <para>
/// Component split: <see cref="SentinelMonitoring"/> supervises subscriptions; the router's
/// notification partial (<see cref="SentinelRouter"/>) runs exactly one discovery worker;
/// <see cref="SentinelBackgroundWork"/> registers both components' tasks under the router gate;
/// this coalescer applies the pure <see cref="SentinelNotificationState.Transition"/> under that
/// same gate. Network queries and waits for DNS completion happen outside the gate. Switch-source
/// DNS startup captures the resolver task under the gate, so starting a lookup is atomic with
/// stopping monitor ownership; the internal resolver seam must return its task promptly.
/// </para>
/// <para>
/// Reducer, state and safety invariants live on <see cref="SentinelNotificationState"/>. Further
/// notification behaviour must go through the explicit Idle/Active/ActivePending reducer (#727)
/// and preserve the joining and reentrancy contracts documented on the router, monitor and
/// background owner; the randomized publication tests are its regression gate.
/// </para>
/// </remarks>
internal sealed class SentinelNotificationCoalescer
{
    internal SentinelNotificationState State { get; private set; }
    internal SentinelHint? Active => State.Active;
    internal SentinelHintKey? ActiveKey => Active?.Key;
    internal SentinelHint? Pending => State.Pending;

    internal SentinelNotificationTransition Transition(in SentinelNotificationEvent notification,
        in SentinelNotificationContext context = default)
    {
        var transition = State.Transition(in notification, in context);
        State = transition.State;
        return transition;
    }

    internal sealed class SourceResolution(SentinelNotificationCoalescer owner, long id)
    {
        internal long Id => id;
        internal SentinelHint Hint => owner.State.SourceResolutions[id];
    }

    internal SourceResolution BeginSourceResolution(in SentinelHint hint)
    {
        var transition = Transition(new(SentinelNotificationEventKind.BeginSourceResolution, hint));
        return new(this, transition.ResolutionId);
    }

    internal void EndSourceResolution(SourceResolution resolution)
        => Transition(new(SentinelNotificationEventKind.EndSourceResolution, ResolutionId: resolution.Id));

    internal bool Offer(in SentinelHint hint, bool targetIsCurrent)
    {
        return Transition(new(SentinelNotificationEventKind.Offer, hint, targetIsCurrent)).Action
            == SentinelNotificationAction.RunNext;
    }

    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
        => SentinelNotificationState.Merge(pending, in hint);

    internal void RetainResolvedOldPrimaryAddresses(RespireEndpoint endpoint, string[] addresses,
        SourceResolution? resolution = null)
        => Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: resolution?.Id ?? 0, AddressEvidence: new(endpoint, addresses)));

    internal SentinelHint? TakePending(bool activeFailed = false, RespireEndpoint? validatedPrimary = null,
        RespireEndpoint? validatedPeer = null)
    {
        State = State.TakePending(activeFailed, validatedPrimary, validatedPeer, out var taken);
        return taken;
    }

    internal SentinelHint? SupersedeActive()
    {
        State = State.SupersedeActive();
        return State.Active;
    }

    internal void Complete() => State = State.Complete();
}
