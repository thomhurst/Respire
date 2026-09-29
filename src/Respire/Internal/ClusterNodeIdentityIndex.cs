using Respire.Infrastructure;

namespace Respire.Internal;

internal sealed record ClusterTopologyRange(
    int Start, int End, RespireEndpoint Preferred, string? NodeId, List<RespireEndpoint> Aliases);

/// <summary>Owns endpoint aliases, Redis node identities, and every constructed transport.</summary>
/// <remarks>The caller holds ClusterRouter's node gate for all access, including snapshot publication.</remarks>
internal sealed class ClusterNodeIdentityIndex
{
    private readonly Dictionary<RespireEndpoint, RespireConnectionMultiplexer> _nodes = new(EndpointComparer.Instance);
    private readonly Dictionary<string, RespireConnectionMultiplexer> _nodesById = new(StringComparer.Ordinal);
    private readonly Dictionary<RespireConnectionMultiplexer, string> _nodeIds = [];
    // TODO #390: drain superseded transports and remove obsolete identities before releasing ownership.
    private readonly HashSet<RespireConnectionMultiplexer> _allNodes = [];
    private readonly Func<RespireEndpoint, RespireConnectionMultiplexer> _create;

    internal ClusterNodeIdentityIndex(RespireEndpoint endpoint, RespireConnectionMultiplexer primary,
        Func<RespireEndpoint, RespireConnectionMultiplexer> create)
    {
        _create = create;
        _nodes.Add(endpoint, primary);
        _allNodes.Add(primary);
    }

    internal IEnumerable<RespireConnectionMultiplexer> All => _allNodes;
    internal IEnumerable<RespireEndpoint> Endpoints => _nodes.Keys;

    internal RespireConnectionMultiplexer GetOrCreate(RespireEndpoint endpoint)
    {
        if (_nodes.TryGetValue(endpoint, out var existing))
        {
            return existing;
        }
        var node = CreateNode(endpoint);
        _nodes.Add(endpoint, node);
        return node;
    }

    internal RespireConnectionMultiplexer GetCurrent(RespireConnectionMultiplexer node)
    {
        if (_nodeIds.TryGetValue(node, out var id)
            && _nodesById.TryGetValue(id, out var identified) && IsCurrentTransport(identified))
        {
            return identified;
        }
        return _nodes.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var current) ? current : node;
    }

    internal List<(ClusterTopologyRange Range, RespireConnectionMultiplexer Node)> ApplySnapshot(
        List<ClusterTopologyRange> ranges)
    {
        var advertisedById = new Dictionary<string, HashSet<RespireEndpoint>>(StringComparer.Ordinal);
        foreach (var range in ranges)
        {
            if (range.NodeId is not { } id)
            {
                continue;
            }

            if (!advertisedById.TryGetValue(id, out var endpoints))
            {
                advertisedById.Add(id, endpoints = new HashSet<RespireEndpoint>(EndpointComparer.Instance));
            }

            endpoints.Add(range.Preferred);
            endpoints.UnionWith(range.Aliases);
        }

        var selectedById = new Dictionary<string, RespireConnectionMultiplexer>(StringComparer.Ordinal);
        var selectedNodeIds = new Dictionary<RespireConnectionMultiplexer, string>();
        var selectedEndpoints = new Dictionary<RespireEndpoint, RespireConnectionMultiplexer>(EndpointComparer.Instance);
        var resolved = new List<(ClusterTopologyRange Range, RespireConnectionMultiplexer Node)>();
        foreach (var range in ranges)
        {
            RespireConnectionMultiplexer node;
            if (range.NodeId is { } id && selectedById.TryGetValue(id, out var selected))
            {
                node = selected;
            }
            else
            {
                var advertised = range.NodeId is { } knownId ? advertisedById[knownId]
                    : new HashSet<RespireEndpoint>(range.Aliases, EndpointComparer.Instance) { range.Preferred };
                node = ResolveTopologyNode(range, advertised, selectedEndpoints, selectedNodeIds);
                if (range.NodeId is { } nodeId)
                {
                    selectedById.Add(nodeId, node);
                    selectedNodeIds[node] = nodeId;
                }
            }

            resolved.Add((range, node));
            selectedEndpoints[range.Preferred] = node;
            foreach (var alias in range.Aliases)
            {
                selectedEndpoints.TryAdd(alias, node);
            }
        }

        // Publish all preferred endpoints before metadata. An alias reassigned in this
        // snapshot replaces its old owner, but cannot override another current preferred endpoint.
        var published = new HashSet<RespireEndpoint>(EndpointComparer.Instance);
        foreach (var (range, node) in resolved)
        {
            _nodes[range.Preferred] = node;
            published.Add(range.Preferred);
        }

        foreach (var (range, node) in resolved)
        {
            foreach (var alias in range.Aliases)
            {
                if (published.Add(alias))
                {
                    _nodes[alias] = node;
                }
            }
        }

        foreach (var (id, node) in selectedById)
        {
            _nodesById[id] = node;
            _nodeIds[node] = id;
        }

        return resolved;
    }

    // Reuse a transport only while its immutable address remains
    // advertised for this node; a stable node ID alone does not make an old host reachable.
    private RespireConnectionMultiplexer ResolveTopologyNode(
        ClusterTopologyRange range, HashSet<RespireEndpoint> advertised,
        Dictionary<RespireEndpoint, RespireConnectionMultiplexer> selectedEndpoints,
        Dictionary<RespireConnectionMultiplexer, string> selectedNodeIds)
    {
        if (range.NodeId is { } id && _nodesById.TryGetValue(id, out var identified)
            && IsCurrentTransport(identified) && IsAdvertised(identified))
        {
            return identified;
        }

        // Metadata is a fallback; an existing compatible preferred transport wins.
        if (ResolveEndpoint(range.Preferred) is { } preferred)
        {
            return preferred;
        }
        foreach (var endpoint in advertised)
        {
            if (ResolveEndpoint(endpoint) is { } alias)
            {
                return alias;
            }
        }

        return CreateNode(range.Preferred);

        RespireConnectionMultiplexer? ResolveEndpoint(RespireEndpoint endpoint)
        {
            if (selectedEndpoints.TryGetValue(endpoint, out var selected)
                && HasCompatibleId(selected) && IsAdvertised(selected))
            {
                return selected;
            }

            if (_nodes.TryGetValue(endpoint, out var existing) && IsCurrentTransport(existing)
                && HasCompatibleId(existing) && IsAdvertised(existing))
            {
                return existing;
            }
            return null;
        }

        bool IsAdvertised(RespireConnectionMultiplexer node)
            => advertised.Contains(new RespireEndpoint(node.Host, node.Port));

        bool HasCompatibleId(RespireConnectionMultiplexer node)
            => range.NodeId is null
                || !(selectedNodeIds.TryGetValue(node, out var knownId) || _nodeIds.TryGetValue(node, out knownId))
                || knownId == range.NodeId;
    }

    private bool IsCurrentTransport(RespireConnectionMultiplexer node)
        => _nodes.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var owner)
            && ReferenceEquals(owner, node);

    private RespireConnectionMultiplexer CreateNode(RespireEndpoint endpoint)
    {
        var node = _create(endpoint);
        // Retain ownership even if a later range fails before the snapshot is published.
        _allNodes.Add(node);
        return node;
    }

    private sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        public static readonly EndpointComparer Instance = new();

        public bool Equals(RespireEndpoint left, RespireEndpoint right)
            => left.Port == right.Port && StringComparer.OrdinalIgnoreCase.Equals(left.Host, right.Host);

        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
    }
}
