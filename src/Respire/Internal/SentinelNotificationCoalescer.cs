namespace Respire.Internal;

// Gate-owned adapter. Evidence transitions return immutable state; the router owns
// synchronization and I/O. Compatibility methods also expose those transitions to tests.
internal sealed class SentinelNotificationCoalescer
{
    internal SentinelNotificationState State { get; private set; } = new();
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
