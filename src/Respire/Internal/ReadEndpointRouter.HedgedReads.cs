using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    internal async ValueTask<RespireConnection?> GetHedgeConnectionAsync(RespireReadFrom readFrom,
        RespireConnection original, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (readFrom == RespireReadFrom.Primary) return null;
        var endpoints = await GetReplicaEndpointsAsync(cancellationToken, waitForUnknown: false).ConfigureAwait(false);
        try
        {
            var replica = await GetReplicaFromEndpointsAsync(endpoints, cancellationToken, original).ConfigureAwait(false);
            if (IsCurrent(replica.Replica!) && replica.Replica!.IsRoleEligible(replica.Connection)) return replica.Connection;
        }
        catch (Exception error) when (IsUnavailable(error, cancellationToken)) { }
        if (readFrom == RespireReadFrom.Replica) return null;
        var primary = await GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        return ReferenceEquals(primary.Primary, Core.Multiplexer)
            && HedgedReadPolicy.IsDifferentPeer(original, primary.Connection) ? primary.Connection : null;
    }
}
