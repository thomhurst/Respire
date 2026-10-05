using Respire.Networking;

namespace Respire;

public sealed partial class RespireClient
{
    // Capture one routing publication. Never discover topology or open a probe connection.
    internal (RespireEndpoint Endpoint, RespireConnection? Connection)[] CaptureHealthConnections(bool allNodes)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (_core.Cluster is { } cluster)
        {
            var snapshot = cluster.RoutingSnapshot;
            if (!snapshot.IsComplete)
                throw new RespireConnectionException("Redis Cluster topology is not available for a health check.");
            var nodes = allNodes ? snapshot.Masters.Concat(snapshot.ReplicaNodes) : snapshot.Masters.Take(1);
            return nodes.Distinct().Select(node =>
                (new RespireEndpoint(node.Host, node.Port), node.GetExistingHealthConnection())).ToArray();
        }

        var primary = _core.Sentinel is { } sentinel ? sentinel.Current?.Multiplexer : _core.Multiplexer;
        if (primary is null)
            throw new RespireConnectionException("Sentinel has no validated primary for a health check.");
        (RespireEndpoint Endpoint, RespireConnection? Connection) target =
            (new(primary.Host, primary.Port), primary.GetExistingHealthConnection());
        return allNodes ? new[] { target }.Concat(_core.ReadRouter.CaptureHealthConnections()).ToArray() : [target];
    }
}
