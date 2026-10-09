using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    internal bool HasPotentialHedgePeer(int slot, RespireReadFrom readFrom, RespireConnection original)
    {
        if (readFrom != RespireReadFrom.Replica) return true;
        if (RoutingSnapshot[slot].Replicas is not { } replicas) return false;
        foreach (var node in replicas.Nodes)
            if (!node.IsRetired && !ReferenceEquals(node, original.Multiplexer)) return true;
        return false;
    }

    internal async ValueTask<RespireConnection?> GetHedgeConnectionAsync(int slot, RespireReadFrom readFrom,
        RespireConnection original, CancellationToken cancellationToken)
    {
        // Selection can outlive the original caller. Keep its rejected candidates on an
        // independent owner until the selector finishes, including late metric activation.
        var owner = DispatchResponseSource<bool>.Start();
        var observation = owner.Observation;
        try
        {
            if (readFrom == RespireReadFrom.Primary) return null;
            if (RoutingSnapshot[slot].Replicas is { } routes)
            {
                var selected = await TrySelectReplicaAsync(routes, slot, cancellationToken, discovery: null,
                    readFrom, excluded: null, excludedPeer: original, observation: observation).ConfigureAwait(false);
                if (selected.Connection is { } connection && connection.Multiplexer is { IsRetired: false } node
                    && (ReferenceEquals(RoutingSnapshot[slot].Primary, node)
                        || RoutingSnapshot[slot].Replicas?.Nodes.Contains(node) == true)) return connection;
            }
            if (readFrom == RespireReadFrom.Replica) return null;
            var primary = await GetPrimaryReadConnectionAsync(slot, readFrom, cancellationToken, discovery: null).ConfigureAwait(false);
            return ReferenceEquals(RoutingSnapshot[slot].Primary, primary.Multiplexer)
                && HedgedReadPolicy.IsDifferentPeer(original, primary) ? primary : null;
        }
        catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
        {
            ReadEndpointRouter.RecordCandidateFailure(observation, error);
            throw;
        }
        finally { owner.CompleteInternal(); }
    }
}
