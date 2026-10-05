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
            var nodes = allNodes ? snapshot.Masters.Concat(snapshot.ReplicaNodes) : snapshot.Masters;
            var targets = nodes.Distinct().Select(node =>
                (Endpoint: new RespireEndpoint(node.Host, node.Port), Connection: node.GetExistingHealthConnection())).ToArray();
            if (allNodes) return targets;
            foreach (var candidate in targets)
            {
                if (candidate.Connection is not null) return [candidate];
            }
            return targets.Take(1).ToArray();
        }

        var primary = _core.Sentinel is { } sentinel ? sentinel.Current?.Multiplexer : _core.Multiplexer;
        if (primary is null)
            throw new RespireConnectionException("Sentinel has no validated primary for a health check.");
        (RespireEndpoint Endpoint, RespireConnection? Connection) target =
            (new(primary.Host, primary.Port), primary.GetExistingHealthConnection());
        return allNodes ? new[] { target }.Concat(_core.ReadRouter.CaptureHealthConnections()).ToArray() : [target];
    }
}
