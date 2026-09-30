using System.Diagnostics;
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
    // Includes detached generations until their owner confirms drain and correction completion.
    private readonly HashSet<RespireConnectionMultiplexer> _allNodes = [];
    private readonly Func<RespireEndpoint, RespireConnectionMultiplexer> _create;
    private readonly object _gate;

    internal ClusterNodeIdentityIndex(RespireEndpoint endpoint, RespireConnectionMultiplexer primary,
        Func<RespireEndpoint, RespireConnectionMultiplexer> create, object gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _create = create;
        _gate = gate;
        _nodes.Add(endpoint, primary);
        _allNodes.Add(primary);
    }

    internal IEnumerable<RespireConnectionMultiplexer> All => _allNodes;
    internal IEnumerable<RespireEndpoint> Endpoints => _nodes.Keys;
    internal int NodeIdCount => _nodesById.Count;
    internal int ReverseNodeIdCount => _nodeIds.Count;

    internal bool IsActive(RespireConnectionMultiplexer node)
    {
        AssertAccess();
        return _nodes.Values.Contains(node);
    }

    /// <summary>Detaches departed generations, retaining configured seed addresses for discovery.</summary>
    /// <remarks>The caller includes newer protected redirects in activeNodes and retires returned
    /// transports outside its gate. Forget releases ownership only after their cleanup completes.</remarks>
    internal RespireConnectionMultiplexer[] DetachInactive(
        HashSet<RespireConnectionMultiplexer> activeNodes, IReadOnlyList<RespireEndpoint> configuredSeeds)
    {
        AssertAccess();
        var retained = new HashSet<RespireConnectionMultiplexer>(activeNodes);
        foreach (var seed in configuredSeeds)
            if (_nodes.TryGetValue(seed, out var node)) retained.Add(node);

        foreach (var (endpoint, node) in _nodes.ToArray())
            if (!retained.Contains(node)) _nodes.Remove(endpoint);
        foreach (var (id, node) in _nodesById.ToArray())
            if (!activeNodes.Contains(node)) _nodesById.Remove(id);
        foreach (var (node, id) in _nodeIds.ToArray())
            if (!_nodesById.TryGetValue(id, out var current) || !ReferenceEquals(current, node)) _nodeIds.Remove(node);

        ValidateInvariants();
        return _allNodes.Where(node => !retained.Contains(node)).ToArray();
    }

    internal void Forget(RespireConnectionMultiplexer node)
    {
        AssertAccess();
        Debug.Assert(!IsActive(node), "An active generation cannot be forgotten.");
        _allNodes.Remove(node);
        ValidateInvariants();
    }

    internal RespireConnectionMultiplexer GetOrCreate(RespireEndpoint endpoint)
    {
        AssertAccess();
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
        AssertAccess();
        if (_nodeIds.TryGetValue(node, out var id)
            && _nodesById.TryGetValue(id, out var identified) && IsCurrentTransport(identified))
        {
            return identified;
        }
        // The router checks active membership before publishing this fallback as its seed.
        return _nodes.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var current) ? current : node;
    }

    internal List<(ClusterTopologyRange Range, RespireConnectionMultiplexer Node)> ApplySnapshot(
        List<ClusterTopologyRange> ranges, HashSet<RespireConnectionMultiplexer>? protectedNodes = null)
    {
        AssertAccess();
        var advertisedById = CollectAdvertisedEndpoints(ranges);
        // Reserve preferred addresses across the entire snapshot before considering aliases.
        // A later range must not lose its transport to an earlier range's metadata.
        var preferredOwners = new Dictionary<RespireEndpoint, string?>(EndpointComparer.Instance);
        foreach (var range in ranges)
        {
            preferredOwners.TryAdd(range.Preferred, range.NodeId);
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
                node = ResolveTopologyNode(range, advertised, preferredOwners, selectedEndpoints, selectedNodeIds, protectedNodes);
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

        PublishSnapshot(resolved, selectedById, protectedNodes);
        return resolved;
    }

    private static Dictionary<string, HashSet<RespireEndpoint>> CollectAdvertisedEndpoints(
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
        return advertisedById;
    }

    private void PublishSnapshot(List<(ClusterTopologyRange Range, RespireConnectionMultiplexer Node)> resolved,
        Dictionary<string, RespireConnectionMultiplexer> selectedById,
        HashSet<RespireConnectionMultiplexer>? protectedNodes)
    {
        // Publish all preferred endpoints before metadata. An alias reassigned in this
        // snapshot replaces its old owner, but cannot override another current preferred endpoint.
        HashSet<RespireEndpoint>? protectedEndpoints = null;
        if (protectedNodes is not null)
        {
            foreach (var (endpoint, node) in _nodes)
            {
                if (protectedNodes.Contains(node))
                {
                    (protectedEndpoints ??= new(EndpointComparer.Instance)).Add(endpoint);
                }
            }
        }
        // MOVED and ASK routes learned after discovery began own their endpoint mappings too.
        var published = protectedEndpoints is null
            ? new HashSet<RespireEndpoint>(EndpointComparer.Instance)
            : new HashSet<RespireEndpoint>(protectedEndpoints, EndpointComparer.Instance);
        foreach (var (range, node) in resolved)
        {
            if (protectedEndpoints?.Contains(range.Preferred) != true)
            {
                _nodes[range.Preferred] = node;
                published.Add(range.Preferred);
            }
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

        // Withdrawn aliases must not redirect a future MOVED/ASK to a different host.
        // Keep immutable endpoints until the owner selects active nodes and configured seeds
        // in DetachInactive; retaining an endpoint never substitutes another host.
        foreach (var (endpoint, node) in _nodes.ToArray())
        {
            if (!published.Contains(endpoint)
                && !EndpointComparer.Instance.Equals(endpoint, new RespireEndpoint(node.Host, node.Port)))
            {
                _nodes.Remove(endpoint);
            }
        }

        foreach (var (id, node) in selectedById)
        {
            if (protectedNodes is not null && (protectedNodes.Contains(node)
                || (_nodesById.TryGetValue(id, out var current) && protectedNodes.Contains(current))))
            {
                continue;
            }
            _nodesById[id] = node;
            _nodeIds[node] = id;
        }
        ValidateInvariants();
    }

    [Conditional("DEBUG")]
    private void ValidateInvariants()
    {
        AssertAccess();
        foreach (var node in _nodes.Values)
        {
            Debug.Assert(_allNodes.Contains(node), "Every endpoint transport must remain owned.");
        }
        foreach (var (id, node) in _nodesById)
        {
            Debug.Assert(_allNodes.Contains(node), "Every identity transport must remain owned.");
            Debug.Assert(_nodeIds.TryGetValue(node, out var reverseId) && reverseId == id,
                "Every current identity must have a matching reverse identity.");
        }
        // Publication and ownership pruning happen in separate steps under the same gate.
        foreach (var node in _nodeIds.Keys)
        {
            Debug.Assert(_allNodes.Contains(node), "Every historical identity transport must remain owned.");
        }
    }

    [Conditional("DEBUG")]
    private void AssertAccess()
        => Debug.Assert(Monitor.IsEntered(_gate), "Cluster identity access requires the router node gate.");

    // Reuse a transport only while its immutable address remains
    // advertised for this node; a stable node ID alone does not make an old host reachable.
    private RespireConnectionMultiplexer ResolveTopologyNode(
        ClusterTopologyRange range, HashSet<RespireEndpoint> advertised,
        Dictionary<RespireEndpoint, string?> preferredOwners,
        Dictionary<RespireEndpoint, RespireConnectionMultiplexer> selectedEndpoints,
        Dictionary<RespireConnectionMultiplexer, string> selectedNodeIds,
        HashSet<RespireConnectionMultiplexer>? protectedNodes)
    {
        if (range.NodeId is { } id && _nodesById.TryGetValue(id, out var identified)
            && IsCurrentTransport(identified) && CanReuse(identified))
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
                && HasCompatibleId(selected) && CanReuse(selected))
            {
                return selected;
            }

            if (_nodes.TryGetValue(endpoint, out var existing) && IsCurrentTransport(existing)
                && HasCompatibleId(existing) && CanReuse(existing))
            {
                return existing;
            }
            return null;
        }

        bool CanReuse(RespireConnectionMultiplexer node)
        {
            var endpoint = new RespireEndpoint(node.Host, node.Port);
            // A newer route cannot be borrowed as metadata for an older node advertisement.
            if (protectedNodes?.Contains(node) == true
                && !EndpointComparer.Instance.Equals(endpoint, range.Preferred))
            {
                return false;
            }
            if (!EndpointComparer.Instance.Equals(endpoint, range.Preferred)
                && preferredOwners.TryGetValue(endpoint, out var preferredId)
                && (range.NodeId is null || preferredId != range.NodeId))
            {
                return false;
            }
            // An alias that never connected may have failed TLS or DNS. Only reuse an
            // established alias; otherwise try the server's preferred authentication name.
            return advertised.Contains(endpoint)
                && (node.IsInitialized || EndpointComparer.Instance.Equals(endpoint, range.Preferred));
        }

        bool HasCompatibleId(RespireConnectionMultiplexer node)
            => range.NodeId is null
                || !(selectedNodeIds.TryGetValue(node, out var knownId) || _nodeIds.TryGetValue(node, out knownId))
                || knownId == range.NodeId;
    }

    private bool IsCurrentTransport(RespireConnectionMultiplexer node)
        => _nodes.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var owner)
            && ReferenceEquals(owner, node);

    // The factory runs under the router node gate. It must not publish health events or
    // acquire the ClientCore health gate; observation starts only after construction.
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
