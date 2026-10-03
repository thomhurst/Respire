using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    internal async ValueTask<RespireConnection?> GetHedgeConnectionAsync(int slot, RespireReadFrom readFrom,
        RespireConnection original, CancellationToken cancellationToken)
    {
        if (readFrom == RespireReadFrom.Primary) return null;
        if (RoutingSnapshot[slot].Replicas is { } routes)
        {
            var candidates = new ClusterReplicaSelector(routes);
            while (candidates.TryNext(out var node))
            {
                if (ReferenceEquals(node, original.Multiplexer)) continue;
                try
                {
                    await EnsureRouteNodeConnectedAsync(node, cancellationToken, discovery: null).ConfigureAwait(false);
                    if (node.IsRetired || RoutingSnapshot[slot].Replicas?.Nodes.Contains(node) != true) continue;
                    var connection = node.GetConnection(slot);
                    if (HedgedReadPolicy.IsDifferentPeer(original, connection)) return connection;
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken)) { }
            }
        }
        if (readFrom == RespireReadFrom.Replica) return null;
        var primary = await GetConnectionAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        return ReferenceEquals(RoutingSnapshot[slot].Primary, primary.Multiplexer)
            && HedgedReadPolicy.IsDifferentPeer(original, primary) ? primary : null;
    }
}
