using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    internal bool HasPotentialHedgePeer(RespireReadFrom readFrom, RespireConnection original)
    {
        if (readFrom != RespireReadFrom.Replica) return true;
        foreach (var endpoint in Volatile.Read(ref _replicas))
            if (!HedgedReadPolicy.IsOriginalEndpoint(endpoint, original)) return true;
        return false;
    }

    internal async ValueTask<RespireConnection?> GetHedgeConnectionAsync(RespireReadFrom readFrom,
        RespireConnection original, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (readFrom == RespireReadFrom.Primary) return null;
        var endpoints = await GetReplicaEndpointsAsync(cancellationToken, waitForUnknown: false).ConfigureAwait(false);
        try
        {
            var selected = await GetReplicaFromEndpointsAsync(endpoints, cancellationToken, readFrom, original).ConfigureAwait(false);
            if (selected.Replica is { } replica
                ? IsCurrent(replica) && replica.IsRoleEligible(selected.Connection)
                : ReferenceEquals(selected.Primary, Core.Multiplexer)) return selected.Connection;
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
        if (readFrom == RespireReadFrom.Replica) return null;
        var primary = await GetPrimaryAsync(cancellationToken,
            ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? Core.Options.ClientAvailabilityZone : null).ConfigureAwait(false);
        return ReferenceEquals(primary.Primary, Core.Multiplexer)
            && HedgedReadPolicy.IsDifferentPeer(original, primary.Connection) ? primary.Connection : null;
    }
}
