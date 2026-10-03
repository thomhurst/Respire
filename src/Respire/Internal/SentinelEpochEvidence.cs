namespace Respire.Internal;

internal enum SentinelConfigurationRejection
{
    None,
    Superseded,
    DifferentPeer,
}

// Observation precedes network/ROLE validation; acceptance follows it. Keeping these
// facts in one immutable value lets notification transitions retain the exact evidence
// they observed without treating provisional DNS candidates as validated ownership.
internal readonly record struct SentinelEpochEvidence(
    long? AcceptedEpoch,
    long? ObservedEpoch,
    RespireEndpoint? ObservedPrimary,
    SentinelAddressEvidence ObservedAddresses,
    RespireEndpoint? ValidatedPeer)
{
    internal bool IsNewer(long? epoch)
        => epoch is { } candidate && AcceptedEpoch is { } accepted && candidate > accepted;

    internal bool IsCurrent(RespireEndpoint primary, long? epoch, string[]? addresses = null)
    {
        // Deployments that never expose epochs keep metadata-free recovery.
        if (ObservedEpoch is not { } observed) return true;
        if (epoch is { } candidate && candidate > observed) return true;
        return (epoch is null || epoch == observed) && ObservedPrimary is { } current
            && (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(primary, current)
                || new SentinelAddressEvidence(primary, addresses).ConfirmsSameAddress(
                    ValidatedPeer is { } peer ? new(peer, null) : ObservedAddresses));
    }

    internal bool TryObserve(RespireEndpoint primary, long? epoch, string[]? addresses,
        out SentinelEpochEvidence next)
    {
        next = this;
        if (!IsCurrent(primary, epoch, addresses)) return false;
        if (epoch is { } candidate && (ObservedEpoch is null || candidate > ObservedEpoch))
        {
            next = this with
            {
                ObservedEpoch = candidate,
                ObservedPrimary = primary,
                ObservedAddresses = new(primary, addresses),
                ValidatedPeer = null,
            };
        }
        return true;
    }

    internal SentinelConfigurationRejection Accept(RespireEndpoint primary, long? epoch,
        string[]? addresses, RespireEndpoint? validatedPeer, out SentinelEpochEvidence next)
    {
        if (!TryObserve(primary, epoch, addresses, out next))
            return SentinelConfigurationRejection.Superseded;
        if (next.ObservedEpoch is not null && validatedPeer is { } peer)
        {
            if (next.ValidatedPeer is { } acceptedPeer
                && !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(peer, acceptedPeer))
                return SentinelConfigurationRejection.DifferentPeer;
            next = next with { ValidatedPeer = next.ValidatedPeer ?? peer };
        }
        next = next with { AcceptedEpoch = epoch ?? next.ObservedEpoch };
        return SentinelConfigurationRejection.None;
    }
}
