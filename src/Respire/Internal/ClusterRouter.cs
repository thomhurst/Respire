using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed class ClusterRouter : IAsyncDisposable
{
    private const int MaxRedirects = 5;
    private static readonly RawCommand Asking = new("*1\r\n$6\r\nASKING\r\n"u8.ToArray());
    // Slot parsing has no await while this scratch set is live.
    [ThreadStatic]
    private static HashSet<RespireConnectionMultiplexer>? t_activeTopologyNodes;

    private readonly RespireOptions _options;
    private readonly RespireEndpoint[] _seeds;
    private readonly RespireConnectionMultiplexer _primary;
    private readonly Dictionary<RespireEndpoint, RespireConnectionMultiplexer> _nodes = [];
    private readonly Dictionary<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>> _nodeStateHandlers = [];
    private readonly Dictionary<RespireEndpoint, DedicatedConnectionPool> _dedicatedPools = [];
    private readonly object _nodesGate = new();
    private readonly RespireConnectionMultiplexer?[] _slots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
    private RespireConnectionMultiplexer[] _masters = [];
    private int[] _masterSlotCounts = [];
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private RespireConnectionMultiplexer? _seed;
    private int _hasCompleteTopology;
    private int _disposed;

    internal ClusterRouter(RespireOptions options, RespireConnectionMultiplexer primary)
    {
        _options = options;
        _seeds = options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost")]
            : options.Endpoints.ToArray();
        _primary = primary;
        _nodes.Add(options.PrimaryEndpoint, primary);
        ObserveNode(primary);
    }

    internal bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _seed)?.IsConnected == true)
            {
                return true;
            }

            foreach (var master in Volatile.Read(ref _masters))
            {
                if (master.IsConnected)
                {
                    return true;
                }
            }

            return false;
        }
    }
    internal event Action<RespireConnectionMultiplexer, int, RespireConnectionStateChange>? SlotStateChanged;
    internal event Action<RespireConnectionMultiplexer>? NodeRetired;

    internal bool IsSlotConnected(int slot)
        => Volatile.Read(ref _slots[slot])?.IsConnected == true;

    internal RespireEndpoint SeedEndpoint
    {
        get
        {
            var seed = Volatile.Read(ref _seed) ?? _primary;
            return new RespireEndpoint(seed.Host, seed.Port);
        }
    }

    internal async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _seed) is { IsConnected: true })
        {
            return;
        }

        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _seed) is { IsConnected: true })
            {
                return;
            }

            Exception? lastError = null;
            foreach (var endpoint in _seeds)
            {
                var node = GetOrCreateNode(endpoint);
                try
                {
                    await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _seed, node);
                    _ = await TryLoadSlotsAsync(node, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = ex;
                }
            }

            throw new RespireConnectionException("Unable to connect to any Redis Cluster seed.", lastError!);
        }
        finally
        {
            _seedGate.Release();
        }
    }

    internal async ValueTask<RespireConnection> GetConnectionAsync(int? slot, CancellationToken cancellationToken)
    {
        if (slot is null && TryGetConnectedNode() is { } connectedNode)
        {
            return connectedNode.GetConnection();
        }

        if (slot is { } cachedSlot && Volatile.Read(ref _slots[cachedSlot]) is { } cachedNode)
        {
            try
            {
                await cachedNode.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return cachedNode.GetConnection(cachedSlot);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                ClearSlotOwner(cachedSlot, cachedNode);
                // Refresh through another discovered master before falling back to seeds.
            }
        }

        if (slot is { } refreshSlot
            && await TryRefreshSlotThroughKnownMastersAsync(refreshSlot, cancellationToken).ConfigureAwait(false)
                is { } refreshedNode)
        {
            return refreshedNode.GetConnection(refreshSlot);
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : null;
        node ??= Volatile.Read(ref _seed)!;
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return slot is { } affinity ? node.GetConnection(affinity) : node.GetConnection();
    }

    internal async ValueTask<RespireConnection> GetRedirectConnectionAsync(
        RespireServerException error,
        RespireConnection source,
        CancellationToken cancellationToken)
    {
        if (!TryParseRedirect(error, source.Host, out var slot, out var endpoint))
        {
            throw error;
        }

        var observe = error.Code != "ASK";
        var node = GetOrCreateNode(endpoint, observe);
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (error.Code == "MOVED")
        {
            SetSlotOwner(slot, node);
        }

        return node.GetConnection(slot);
    }

    internal async ValueTask<RespireConnection> GetTrackedConnectionAsync(
        int? slot, bool requireIdentity, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        return await EnableCorrectionOrderingAsync(connection, requireIdentity, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<RespireConnection> GetTrackedRedirectConnectionAsync(
        RespireServerException error,
        RespireConnection source,
        bool requireIdentity,
        CancellationToken cancellationToken)
    {
        var connection = await GetRedirectConnectionAsync(error, source, cancellationToken).ConfigureAwait(false);
        return await EnableCorrectionOrderingAsync(
                connection, requireIdentity, cancellationToken, observe: error.Code != "ASK")
            .ConfigureAwait(false);
    }

    internal async ValueTask<DedicatedConnectionPool> GetDedicatedPoolAsync(
        int? slot,
        CancellationToken cancellationToken)
    {
        if (slot is { } cachedSlot && Volatile.Read(ref _slots[cachedSlot]) is { } cachedNode)
        {
            try
            {
                await cachedNode.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return GetOrCreateDedicatedPool(new RespireEndpoint(cachedNode.Host, cachedNode.Port));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                ClearSlotOwner(cachedSlot, cachedNode);
                // Refresh through another discovered master before falling back to seeds.
            }
        }

        if (slot is { } refreshSlot
            && await TryRefreshSlotThroughKnownMastersAsync(refreshSlot, cancellationToken).ConfigureAwait(false)
                is { } refreshedNode)
        {
            return GetOrCreateDedicatedPool(new RespireEndpoint(refreshedNode.Host, refreshedNode.Port));
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : null;
        node ??= Volatile.Read(ref _seed)!;
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return GetOrCreateDedicatedPool(new RespireEndpoint(node.Host, node.Port));
    }

    internal async ValueTask<DedicatedConnectionPool> GetRedirectDedicatedPoolAsync(
        RespireServerException error,
        RespireConnection source,
        CancellationToken cancellationToken)
    {
        if (!TryParseRedirect(error, source.Host, out var slot, out var endpoint))
        {
            throw error;
        }

        var node = GetOrCreateNode(endpoint, observe: error.Code != "ASK");
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (error.Code == "MOVED")
        {
            SetSlotOwner(slot, node);
        }

        return GetOrCreateDedicatedPool(endpoint);
    }

    internal DedicatedConnectionPool GetDedicatedPool(RespireEndpoint endpoint)
        => GetOrCreateDedicatedPool(endpoint);

    internal ValueTask RetireConnectionAsync(RespireEndpoint endpoint, long serverClientId)
        => GetOrCreateNode(endpoint).RetireConnectionAsync(serverClientId);

    internal RespireConnectionMultiplexer GetMultiplexer(RespireEndpoint endpoint)
        => GetOrCreateNode(endpoint);

    internal bool HasReliableCorrectionOrdering(RespireConnection connection)
        => GetOrCreateNode(new RespireEndpoint(connection.Host, connection.Port), observe: false)
            .HasReliableCorrectionOrdering;

    internal static bool IsRedirect(RespireServerException error)
        => error.Code is "MOVED" or "ASK";

    internal static bool TryParseRedirect(
        RespireServerException error,
        string sourceHost,
        out int slot,
        out RespireEndpoint endpoint)
    {
        slot = 0;
        endpoint = default;
        if (!IsRedirect(error))
        {
            return false;
        }

        var message = error.Message.AsSpan();
        var firstSpace = message.IndexOf(' ');
        if (firstSpace < 0)
        {
            return false;
        }

        message = message[(firstSpace + 1)..];
        var secondSpace = message.IndexOf(' ');
        if (secondSpace < 0 || !int.TryParse(message[..secondSpace], out slot)
            || (uint)slot >= ClusterHash.SlotCount)
        {
            return false;
        }

        var address = message[(secondSpace + 1)..].Trim();
        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address[(colon + 1)..], out var port))
        {
            return false;
        }

        var host = address[..colon].Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        endpoint = new RespireEndpoint(host.IsEmpty ? sourceHost : host.ToString(), port);
        return true;
    }

    internal static int RedirectLimit => MaxRedirects;

    internal static ValueTask<Respire.Protocol.RespValue> SendAskingAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedCheckedAsync(in Asking, in command, cancellationToken);

    internal static ValueTask<Respire.Protocol.RespValue> SendAskingUncheckedAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedAsync(in Asking, in command, throwOnError: false, cancellationToken);

    internal static ValueTask<Respire.Protocol.RespValue> SendBlockingAskingUncheckedAsync<TCommand>(
        RespireConnection connection,
        in TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, Respire.Protocol.IRespCommand
        => connection.SendPrefixedWithoutResponseTimeoutAsync(
            Asking, command, throwOnError: false, cancellationToken);

    internal async ValueTask<RespireConnection[]> GetMasterConnectionsAsync(CancellationToken cancellationToken)
    {
        var masters = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
        AddKnownMasters(masters);

        var refreshed = false;
        if (Volatile.Read(ref _seed) is { IsConnected: true } seed)
        {
            refreshed = await TryRefreshTopologyAsync(seed, cancellationToken).ConfigureAwait(false);
        }

        if (!refreshed)
        {
            foreach (var master in masters)
            {
                if (await TryRefreshTopologyAsync(master, cancellationToken).ConfigureAwait(false))
                {
                    Volatile.Write(ref _seed, master);
                    refreshed = true;
                    break;
                }
            }
        }

        if (!refreshed)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var loaded = await TryLoadSlotsAsync(
                Volatile.Read(ref _seed)!, cancellationToken).ConfigureAwait(false);
            if (!loaded || !HasCompleteTopology())
            {
                throw new RespireConnectionException(
                    "Unable to load a complete Redis Cluster topology for a cluster-wide command.");
            }
        }

        masters.Clear();
        AddKnownMasters(masters);
        if (masters.Count == 0)
        {
            masters.Add(Volatile.Read(ref _seed)!);
        }

        var connections = new RespireConnection[masters.Count];
        var index = 0;
        foreach (var master in masters)
        {
            // TryLoadSlotsAsync replaces stale owners after a successful refresh. Any owner
            // still present is current; omitting it would make SCAN and cluster-wide server
            // commands silently return incomplete results.
            await master.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            connections[index++] = master.GetConnection();
        }

        return connections;
    }

    internal ValueTask<RespireConnectionMultiplexer[]> GetKnownMastersAsync(
        CancellationToken cancellationToken)
    {
        if (!HasCompleteTopology())
        {
            return RefreshKnownMastersAsync(cancellationToken);
        }

        var masters = Volatile.Read(ref _masters);
        foreach (var master in masters)
        {
            if (!master.IsConnected)
            {
                return ConnectKnownMastersAsync(masters, cancellationToken);
            }
        }

        return new ValueTask<RespireConnectionMultiplexer[]>(masters);
    }

    private async ValueTask<RespireConnectionMultiplexer[]> ConnectKnownMastersAsync(
        RespireConnectionMultiplexer[] masters,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var master in masters)
            {
                await master.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            }

            return masters;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await RefreshKnownMastersAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<RespireConnectionMultiplexer[]> RefreshKnownMastersAsync(
        CancellationToken cancellationToken)
    {
        _ = await GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _masters);
    }

    private async ValueTask<bool> TryRefreshTopologyAsync(
        RespireConnectionMultiplexer node,
        CancellationToken cancellationToken)
    {
        try
        {
            await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await TryLoadSlotsAsync(node, cancellationToken).ConfigureAwait(false)
                && HasCompleteTopology();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<RespireConnectionMultiplexer?> TryRefreshSlotThroughKnownMastersAsync(
        int slot,
        CancellationToken cancellationToken)
    {
        foreach (var master in Volatile.Read(ref _masters))
        {
            if (!await TryRefreshTopologyAsync(master, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var owner = Volatile.Read(ref _slots[slot]);
            if (owner is null)
            {
                continue;
            }

            try
            {
                await owner.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _seed, master);
                return owner;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                ClearSlotOwner(slot, owner);
            }
        }

        return null;
    }

    internal async ValueTask<RespireEndpoint> GetPubSubEndpointAsync(
        CancellationToken cancellationToken)
    {
        foreach (var master in Volatile.Read(ref _masters))
        {
            try
            {
                await master.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return new RespireEndpoint(master.Host, master.Port);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Try another discovered master before falling back to configured seeds.
            }
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return SeedEndpoint;
    }

    private bool HasCompleteTopology()
        => Volatile.Read(ref _hasCompleteTopology) != 0;

    private void AddKnownMasters(HashSet<RespireConnectionMultiplexer> masters)
    {
        foreach (var node in Volatile.Read(ref _masters))
        {
            masters.Add(node);
        }
    }

    private RespireConnectionMultiplexer? TryGetConnectedNode()
    {
        if (Volatile.Read(ref _seed) is { IsConnected: true } seed)
        {
            return seed;
        }

        foreach (var master in Volatile.Read(ref _masters))
        {
            if (master.IsConnected)
            {
                return master;
            }
        }

        return null;
    }

    private RespireConnectionMultiplexer GetOrCreateNode(RespireEndpoint endpoint, bool observe = true)
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_nodes.TryGetValue(endpoint, out var existing))
            {
                if (observe)
                {
                    ObserveNode(existing);
                }

                return existing;
            }

            var node = RespireConnectionMultiplexer.Create(
                endpoint.Host,
                endpoint.Port,
                _options.Connections,
                _options.ToConnectionOptions(),
                _options.CreateLogger($"Respire.Cluster.{endpoint.Host}:{endpoint.Port}"));
            if (observe)
            {
                ObserveNode(node);
            }

            _nodes.Add(endpoint, node);
            return node;
        }
    }

    private void ObserveNode(RespireConnectionMultiplexer node)
    {
        if (_nodeStateHandlers.ContainsKey(node))
        {
            return;
        }

        Action<int, RespireConnectionStateChange> handler =
            (slot, change) => SlotStateChanged?.Invoke(node, slot, change);
        _nodeStateHandlers.Add(node, handler);
        node.SlotStateChanged += handler;
    }

    internal void SetSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        lock (_nodesGate)
        {
            ObserveNode(node);
            var previous = Volatile.Read(ref _slots[slot]);
            if (ReferenceEquals(previous, node))
            {
                return;
            }

            Volatile.Write(ref _slots[slot], node);
            AddSlot(node);
            if (previous is not null && RemoveSlot(previous))
            {
                retiredNode = previous;
            }
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
    }

    private void ClearSlotOwner(int slot, RespireConnectionMultiplexer node)
    {
        RespireConnectionMultiplexer? retiredNode = null;
        lock (_nodesGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref _slots[slot]), node))
            {
                return;
            }

            Volatile.Write(ref _slots[slot], null);
            Volatile.Write(ref _hasCompleteTopology, 0);
            if (RemoveSlot(node))
            {
                retiredNode = node;
            }
        }

        if (retiredNode is not null)
        {
            NodeRetired?.Invoke(retiredNode);
        }
    }

    private void AddSlot(RespireConnectionMultiplexer node)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index >= 0)
        {
            _masterSlotCounts[index]++;
            return;
        }

        var expanded = new RespireConnectionMultiplexer[masters.Length + 1];
        var expandedCounts = new int[expanded.Length];
        masters.CopyTo(expanded, 0);
        _masterSlotCounts.CopyTo(expandedCounts, 0);
        expanded[^1] = node;
        expandedCounts[^1] = 1;
        _masterSlotCounts = expandedCounts;
        Volatile.Write(ref _masters, expanded);
    }

    private bool RemoveSlot(RespireConnectionMultiplexer node)
    {
        var masters = Volatile.Read(ref _masters);
        var index = Array.IndexOf(masters, node);
        if (index < 0 || --_masterSlotCounts[index] > 0)
        {
            return false;
        }

        var contracted = new RespireConnectionMultiplexer[masters.Length - 1];
        var contractedCounts = new int[contracted.Length];
        masters.AsSpan(0, index).CopyTo(contracted);
        masters.AsSpan(index + 1).CopyTo(contracted.AsSpan(index));
        _masterSlotCounts.AsSpan(0, index).CopyTo(contractedCounts);
        _masterSlotCounts.AsSpan(index + 1).CopyTo(contractedCounts.AsSpan(index));
        _masterSlotCounts = contractedCounts;
        Volatile.Write(ref _masters, contracted);

        if (_nodeStateHandlers.Remove(node, out var handler))
        {
            node.SlotStateChanged -= handler;
        }

        return true;
    }

    private void ReplaceSlotOwners(
        RespireConnectionMultiplexer?[] refreshedSlots,
        HashSet<RespireConnectionMultiplexer> activeNodes)
    {
        List<RespireConnectionMultiplexer>? retiredNodes = null;
        lock (_nodesGate)
        {
            var masters = activeNodes.ToArray();
            var masterSlotCounts = new int[masters.Length];
            var complete = true;
            for (var slot = 0; slot < refreshedSlots.Length; slot++)
            {
                var node = refreshedSlots[slot];
                Volatile.Write(ref _slots[slot], node);
                complete &= node is not null;
                if (node is not null)
                {
                    masterSlotCounts[Array.IndexOf(masters, node)]++;
                }
            }

            _masterSlotCounts = masterSlotCounts;
            Volatile.Write(ref _masters, masters);
            Volatile.Write(ref _hasCompleteTopology, complete ? 1 : 0);

            foreach (var node in _nodeStateHandlers.Keys)
            {
                if (activeNodes.Contains(node))
                {
                    continue;
                }

                (retiredNodes ??= []).Add(node);
            }

            if (retiredNodes is not null)
            {
                foreach (var node in retiredNodes)
                {
                    node.SlotStateChanged -= _nodeStateHandlers[node];
                    _nodeStateHandlers.Remove(node);
                }
            }
        }

        if (retiredNodes is not null)
        {
            foreach (var node in retiredNodes)
            {
                NodeRetired?.Invoke(node);
            }
        }
    }

    private async ValueTask<RespireConnection> EnableCorrectionOrderingAsync(
        RespireConnection connection,
        bool required,
        CancellationToken cancellationToken,
        bool observe = true)
    {
        var node = GetOrCreateNode(new RespireEndpoint(connection.Host, connection.Port), observe);
        try
        {
            await node.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RespireServerException) when (!required)
        {
            // Non-cancellable cache access remains compatible with restricted ACLs.
        }

        return node.GetConnection();
    }

    private DedicatedConnectionPool GetOrCreateDedicatedPool(RespireEndpoint endpoint)
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_dedicatedPools.TryGetValue(endpoint, out var existing))
            {
                return existing;
            }

            var pool = new DedicatedConnectionPool(
                endpoint.Host,
                endpoint.Port,
                _options.ToConnectionOptions(),
                _options.CreateLogger($"Respire.Cluster.Blocking.{endpoint.Host}:{endpoint.Port}"));
            _dedicatedPools.Add(endpoint, pool);
            return pool;
        }
    }

    private async ValueTask<bool> TryLoadSlotsAsync(
        RespireConnectionMultiplexer seed,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.CommandTimeout ?? _options.ConnectTimeout);
        try
        {
            var reply = await seed.GetConnection().SendAsync(
                new Cmd(Verbs.ClusterSlots), timeoutSource.Token).ConfigureAwait(false);
            try
            {
                if (reply.IsError)
                {
                    return false;
                }

                var refreshedSlots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
                var ranges = reply.AsArray();
                var activeNodes = t_activeTopologyNodes ??=
                    new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
                activeNodes.Clear();
                var discoveredRange = false;
                foreach (ref readonly var range in ranges)
                {
                    var values = range.AsArray();
                    if (values.Length < 3)
                    {
                        continue;
                    }

                    var start = values[0].AsInteger();
                    var end = values[1].AsInteger();
                    var primary = values[2].AsArray();
                    if (start < 0 || end < start || end >= ClusterHash.SlotCount || primary.Length < 2)
                    {
                        continue;
                    }

                    var host = primary[0].AsString();
                    var port = primary[1].AsInteger();
                    for (var metadataIndex = 3; metadataIndex < primary.Length; metadataIndex++)
                    {
                        var metadata = primary[metadataIndex].AsArray();
                        for (var pairIndex = 0; pairIndex + 1 < metadata.Length; pairIndex += 2)
                        {
                            if (metadata[pairIndex].AsSpan().SequenceEqual("hostname"u8)
                                && !metadata[pairIndex + 1].IsNull)
                            {
                                var announcedHost = metadata[pairIndex + 1].AsString();
                                if (!string.IsNullOrEmpty(announcedHost))
                                {
                                    host = announcedHost;
                                }
                            }
                        }
                    }

                    if (port is <= 0 or > 65_535)
                    {
                        continue;
                    }

                    var node = GetOrCreateNode(new RespireEndpoint(
                        string.IsNullOrEmpty(host) ? seed.Host : host, (int)port));
                    activeNodes.Add(node);

                    for (var slot = (int)start; slot <= end; slot++)
                    {
                        refreshedSlots[slot] = node;
                    }

                    discoveredRange = true;
                }

                if (discoveredRange)
                {
                    ReplaceSlotOwners(refreshedSlots, activeNodes);
                }

                activeNodes.Clear();
                return discoveredRange;
            }
            finally
            {
                reply.Dispose();
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // ACLs and Redis-compatible servers may hide CLUSTER SLOTS. MOVED/ASK learning
            // remains sufficient for correctness, so topology discovery is opportunistic.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RespireConnectionMultiplexer[] nodes;
        KeyValuePair<RespireConnectionMultiplexer, Action<int, RespireConnectionStateChange>>[] stateHandlers;
        DedicatedConnectionPool[] dedicatedPools;
        lock (_nodesGate)
        {
            nodes = _nodes.Values.ToArray();
            stateHandlers = _nodeStateHandlers.ToArray();
            dedicatedPools = _dedicatedPools.Values.ToArray();
        }

        foreach (var pool in dedicatedPools)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var (node, handler) in stateHandlers)
        {
            node.SlotStateChanged -= handler;
        }

        foreach (var node in nodes)
        {
            if (!ReferenceEquals(node, _primary))
            {
                await node.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
