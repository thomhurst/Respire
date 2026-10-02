using System.Diagnostics;
using Respire.Infrastructure;

namespace Respire.Internal;

/// <summary>
/// Owns the read-only replica transports and their Redis node identities for one Cluster router.
/// </summary>
/// <remarks>
/// <para>
/// Keeps the endpoint map and the two identity maps consistent through one small API, so the
/// endpoint-to-transport and identity-to-transport invariants live in one place. Every replica
/// transport is stored only under the endpoint it was created for, so membership checks are a
/// single dictionary lookup.
/// </para>
/// <para>
/// The owning <see cref="ClusterNodeIdentityIndex"/> holds the router node gate for every call and
/// retains ownership of the transports themselves; this type only maps them.
/// </para>
/// </remarks>
internal sealed class ClusterReplicaRegistry
{
    private readonly Dictionary<RespireEndpoint, RespireConnectionMultiplexer> _byEndpoint;
    private readonly Dictionary<string, RespireConnectionMultiplexer> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<RespireConnectionMultiplexer, string> _ids = [];

    internal ClusterReplicaRegistry(IEqualityComparer<RespireEndpoint> endpointComparer)
        => _byEndpoint = new Dictionary<RespireEndpoint, RespireConnectionMultiplexer>(endpointComparer);

    internal int Count => _byEndpoint.Count;

    internal bool ContainsEndpoint(RespireEndpoint endpoint) => _byEndpoint.ContainsKey(endpoint);

    internal bool TryGetById(string id, out RespireConnectionMultiplexer node)
        => _byId.TryGetValue(id, out node!);

    internal bool TryGetId(RespireConnectionMultiplexer node, out string id)
        => _ids.TryGetValue(node, out id!);

    /// <summary>True when <paramref name="node"/> is the current transport for its endpoint.</summary>
    internal bool IsCurrent(RespireConnectionMultiplexer node)
        => _byEndpoint.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var owner)
            && ReferenceEquals(owner, node);

    /// <summary>Returns the replica transport for an advertised replica, creating it when needed.</summary>
    internal RespireConnectionMultiplexer GetOrCreate(
        ClusterTopologyReplica replica, Func<RespireEndpoint, RespireConnectionMultiplexer> create)
    {
        if (!_byEndpoint.TryGetValue(replica.Endpoint, out var node))
        {
            node = create(replica.Endpoint);
            _byEndpoint.Add(replica.Endpoint, node);
        }

        if (!string.IsNullOrEmpty(replica.NodeId)) SetId(node, replica.NodeId);
        return node;
    }

    /// <summary>Resolves the current transport for a possibly older replica generation.</summary>
    internal RespireConnectionMultiplexer GetCurrent(RespireConnectionMultiplexer node)
    {
        if (_ids.TryGetValue(node, out var id)
            && _byId.TryGetValue(id, out var identified)
            && IsCurrent(identified))
        {
            return identified;
        }

        return _byEndpoint.TryGetValue(new RespireEndpoint(node.Host, node.Port), out var current) ? current : node;
    }

    /// <summary>
    /// Drops endpoints whose transport is not in <paramref name="retained"/> and identities whose
    /// transport is not in <paramref name="active"/>.
    /// </summary>
    internal void Retain(
        HashSet<RespireConnectionMultiplexer> retained, HashSet<RespireConnectionMultiplexer> active)
    {
        // Dictionary.Remove preserves enumerators on the supported .NET 8+ runtimes.
        foreach (var (endpoint, node) in _byEndpoint)
            if (!retained.Contains(node)) _byEndpoint.Remove(endpoint);
        foreach (var (id, node) in _byId)
            if (!active.Contains(node)) _byId.Remove(id);
        foreach (var (node, id) in _ids)
            if (!_byId.TryGetValue(id, out var current) || !ReferenceEquals(current, node)) _ids.Remove(node);
    }

    /// <summary>Removes every mapping to a transport whose cleanup has completed.</summary>
    internal void Forget(RespireConnectionMultiplexer node)
    {
        var endpoint = new RespireEndpoint(node.Host, node.Port);
        if (_byEndpoint.TryGetValue(endpoint, out var current) && ReferenceEquals(current, node))
        {
            _byEndpoint.Remove(endpoint);
        }

        if (_ids.Remove(node, out var id) && _byId.TryGetValue(id, out var owner) && ReferenceEquals(owner, node))
        {
            _byId.Remove(id);
        }
    }

    [Conditional("DEBUG")]
    internal void ValidateInvariants(Func<RespireConnectionMultiplexer, bool> isOwned)
    {
        foreach (var (endpoint, node) in _byEndpoint)
        {
            Debug.Assert(isOwned(node), "Every replica transport must remain owned.");
            Debug.Assert(node.Options.ReadOnly, "Replica transports must enter Redis Cluster READONLY mode.");
            Debug.Assert(IsCurrent(node) && endpoint.Port == node.Port,
                "A replica transport is stored only under the endpoint it was created for.");
        }
        foreach (var (id, node) in _byId)
        {
            Debug.Assert(isOwned(node), "Every replica identity transport must remain owned.");
            Debug.Assert(node.Options.ReadOnly, "Replica identities must refer to read-only transports.");
            Debug.Assert(_ids.TryGetValue(node, out var reverseId) && reverseId == id,
                "Every replica identity must have a matching reverse identity.");
        }
    }

    private void SetId(RespireConnectionMultiplexer node, string id)
    {
        if (_ids.TryGetValue(node, out var previousId) && previousId != id
            && _byId.TryGetValue(previousId, out var previousOwner) && ReferenceEquals(previousOwner, node))
        {
            _byId.Remove(previousId);
        }
        if (_byId.TryGetValue(id, out var previousNode) && !ReferenceEquals(previousNode, node))
        {
            _ids.Remove(previousNode);
        }
        _byId[id] = node;
        _ids[node] = id;
    }
}
