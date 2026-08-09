using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed class ClusterRouter : IAsyncDisposable
{
    private const int MaxRedirects = 5;
    private static readonly RawCommand Asking = new("*1\r\n$6\r\nASKING\r\n"u8.ToArray());

    private readonly RespireOptions _options;
    private readonly RespireEndpoint[] _seeds;
    private readonly RespireConnectionMultiplexer _primary;
    private readonly Dictionary<RespireEndpoint, RespireConnectionMultiplexer> _nodes = [];
    private readonly Dictionary<RespireEndpoint, DedicatedConnectionPool> _dedicatedPools = [];
    private readonly object _nodesGate = new();
    private readonly RespireConnectionMultiplexer?[] _slots = new RespireConnectionMultiplexer?[ClusterHash.SlotCount];
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private RespireConnectionMultiplexer? _seed;
    private int _disposed;

    internal ClusterRouter(RespireOptions options, RespireConnectionMultiplexer primary)
    {
        _options = options;
        _seeds = options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost")]
            : options.Endpoints.ToArray();
        _primary = primary;
        _nodes.Add(options.PrimaryEndpoint, primary);
        primary.StateChanged += OnNodeStateChanged;
    }

    internal bool IsConnected => Volatile.Read(ref _seed)?.IsConnected == true;
    internal event Action<RespireConnectionState>? StateChanged;

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
                    await TryLoadSlotsAsync(node, cancellationToken).ConfigureAwait(false);
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
        if (slot is { } cachedSlot && Volatile.Read(ref _slots[cachedSlot]) is { } cachedNode)
        {
            try
            {
                await cachedNode.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                return cachedNode.GetConnection();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Refresh through the seeds below when the cached owner is unavailable.
            }
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var node = slot is { } value ? Volatile.Read(ref _slots[value]) : null;
        node ??= Volatile.Read(ref _seed)!;
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return node.GetConnection();
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

        var node = GetOrCreateNode(endpoint);
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (error.Code == "MOVED")
        {
            Volatile.Write(ref _slots[slot], node);
        }

        return node.GetConnection();
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
                // Refresh through the seeds below when the cached owner is unavailable.
            }
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

        var node = GetOrCreateNode(endpoint);
        await node.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (error.Code == "MOVED")
        {
            Volatile.Write(ref _slots[slot], node);
        }

        return GetOrCreateDedicatedPool(endpoint);
    }

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

    internal static ValueTask<Respire.Protocol.RespValue> SendAskingTransactionAsync(
        RespireConnection connection,
        ReadOnlyMemory<byte> serializedCommands,
        int commandCount,
        CancellationToken cancellationToken)
        => connection.SendPrefixedTransactionAsync(
            in Asking, serializedCommands, commandCount, cancellationToken);

    internal async ValueTask<RespireConnection[]> GetMasterConnectionsAsync(CancellationToken cancellationToken)
    {
        var masters = new HashSet<RespireConnectionMultiplexer>(ReferenceEqualityComparer.Instance);
        AddKnownMasters(masters);

        if (masters.Count == 0)
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            AddKnownMasters(masters);

            if (masters.Count == 0)
            {
                masters.Add(Volatile.Read(ref _seed)!);
            }
        }

        var connections = new RespireConnection[masters.Count];
        var index = 0;
        foreach (var master in masters)
        {
            await master.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            connections[index++] = master.GetConnection();
        }

        return connections;
    }

    private void AddKnownMasters(HashSet<RespireConnectionMultiplexer> masters)
    {
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            if (Volatile.Read(ref _slots[slot]) is { } node)
            {
                masters.Add(node);
            }
        }
    }

    private RespireConnectionMultiplexer GetOrCreateNode(RespireEndpoint endpoint)
    {
        lock (_nodesGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_nodes.TryGetValue(endpoint, out var existing))
            {
                return existing;
            }

            var node = RespireConnectionMultiplexer.Create(
                endpoint.Host,
                endpoint.Port,
                _options.Connections,
                _options.ToConnectionOptions(),
                _options.CreateLogger($"Respire.Cluster.{endpoint.Host}:{endpoint.Port}"));
            node.StateChanged += OnNodeStateChanged;
            _nodes.Add(endpoint, node);
            return node;
        }
    }

    private void OnNodeStateChanged(RespireConnectionState state) => StateChanged?.Invoke(state);

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

    private async ValueTask TryLoadSlotsAsync(
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
                    return;
                }

                foreach (ref readonly var range in reply.AsArray())
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
                    for (var slot = (int)start; slot <= end; slot++)
                    {
                        Volatile.Write(ref _slots[slot], node);
                    }
                }
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RespireConnectionMultiplexer[] nodes;
        DedicatedConnectionPool[] dedicatedPools;
        lock (_nodesGate)
        {
            nodes = _nodes.Values.ToArray();
            dedicatedPools = _dedicatedPools.Values.ToArray();
        }

        foreach (var pool in dedicatedPools)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var node in nodes)
        {
            node.StateChanged -= OnNodeStateChanged;
            if (!ReferenceEquals(node, _primary))
            {
                await node.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
