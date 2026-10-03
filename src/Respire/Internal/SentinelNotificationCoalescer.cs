namespace Respire.Internal;

// Gate-owned adapter. Evidence transitions return immutable state; the router owns
// synchronization and I/O. Compatibility methods also expose those transitions to tests.
internal sealed class SentinelNotificationCoalescer
{
    internal SentinelNotificationState State { get; private set; } = new();
    internal SentinelHint? Active => State.Active;
    internal SentinelHintKey? ActiveKey => Active?.Key;
    internal SentinelHint? Pending => State.Pending;

    internal sealed class SourceResolution(SentinelNotificationCoalescer owner, long id)
    {
        internal long Id => id;
        internal SentinelHint Hint => owner.State.SourceResolutions[id];
    }

    internal SourceResolution BeginSourceResolution(in SentinelHint hint)
    {
        State = State.BeginSourceResolution(in hint, out var id);
        return new(this, id);
    }

    internal void EndSourceResolution(SourceResolution resolution)
        => State = State.EndSourceResolution(resolution.Id);

    internal bool Offer(in SentinelHint hint, bool targetIsCurrent)
    {
        State = State.Offer(in hint, targetIsCurrent, out var startWorker);
        return startWorker;
    }

    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
        => SentinelNotificationState.Merge(pending, in hint);

    internal void RetainResolvedOldPrimaryAddresses(RespireEndpoint endpoint, string[] addresses)
        => State = State.RetainResolvedOldPrimaryAddresses(endpoint, addresses);

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