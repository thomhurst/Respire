using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>A single pass over a range's replicas, starting at its next round-robin position.</summary>
/// <remarks>
/// The selector owns no transports and allocates nothing. The router connects each candidate
/// and checks retirement again after connection readiness, which can race topology changes.
/// </remarks>
internal struct ClusterReplicaSelector
{
    private readonly RespireConnectionMultiplexer[] _nodes;
    private readonly int _start;
    private int _offset;

    internal ClusterReplicaSelector(ClusterReplicaSet routes)
    {
        _nodes = routes.Nodes;
        _start = _nodes.Length == 0 ? 0 : routes.NextStart();
    }

    internal bool TryNext(out RespireConnectionMultiplexer node)
    {
        while (_offset < _nodes.Length)
        {
            node = _nodes[(_start + _offset++) % _nodes.Length];
            if (!node.IsRetired) return true;
        }

        node = null!;
        return false;
    }
}
