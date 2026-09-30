using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>The value or failure from one endpoint in an explicit server fan-out.</summary>
public sealed class RespireServerResult<T>
{
    private readonly T _value;
    private RespireServerResult(RespireEndpoint endpoint, T value, Exception? error)
    {
        Endpoint = endpoint;
        _value = value;
        Error = error;
    }

    /// <summary>The endpoint queried for this result.</summary>
    public RespireEndpoint Endpoint { get; }
    /// <summary>Whether the endpoint returned a successfully parsed reply.</summary>
    public bool IsSuccess => Error is null;
    /// <summary>The original node failure, including cancellation, or null on success.</summary>
    public Exception? Error { get; }
    /// <summary>The owned result. Throws InvalidOperationException with Error as its inner exception on failure.</summary>
    public T Value => Error is null ? _value : throw new InvalidOperationException("The server operation failed.", Error);
    internal static RespireServerResult<T> Success(RespireEndpoint endpoint, T value) => new(endpoint, value, null);
    internal static RespireServerResult<T> Failure(RespireEndpoint endpoint, Exception error) => new(endpoint, default!, error);
}

internal sealed partial class ServerCommands
{
    private static readonly Verb ClusterNodes = new(-1, "CLUSTER", "NODES");

    private async ValueTask<RespireServerResult<T>[]> FanOutAsync<TCommand, T>(
        string operation, TCommand command, CancellationToken cancellationToken,
        ResponseConverter<ServerCommands, T> convert) where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = await DiscoverServerEndpointsAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new Task<RespireServerResult<T>>[endpoints.Length];
        for (var index = 0; index < endpoints.Length; index++)
            tasks[index] = ExecuteOnNodeAsync(endpoints[index], operation, command, convert, cancellationToken);
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<RespireServerResult<T>> ExecuteOnNodeAsync<TCommand, T>(
        RespireEndpoint endpoint, string operation, TCommand command,
        ResponseConverter<ServerCommands, T> convert, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pool = client.Core.Cluster is { } cluster ? cluster.GetDedicatedPool(endpoint) : client.Core.DedicatedPool;
            RespireConnection? connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var reply = await client.SendOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
                var result = convert(this, in reply);
                pool.Return(connection);
                connection = null;
                return RespireServerResult<T>.Success(endpoint, result);
            }
            finally
            {
                if (connection is not null) await pool.DiscardAsync(connection).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            // Preserve successes from other nodes, including when cancellation occurs after discovery.
            return RespireServerResult<T>.Failure(endpoint, error);
        }
    }

    private async ValueTask<RespireEndpoint[]> DiscoverServerEndpointsAsync(CancellationToken cancellationToken)
    {
        var connection = await client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        var source = new RespireEndpoint(connection.Host, connection.Port);
        if (client.Core.Cluster is null) return [source];
        // The routing table contains primaries only. CLUSTER NODES also includes replicas and
        // slotless members, which can have their own subscribers. Never silently omit them.
        using var reply = await client.SendOnConnectionAsync("CLUSTER NODES", connection,
            new Cmd(ClusterNodes), cancellationToken).ConfigureAwait(false);
        if (reply.Type is not (RespDataType.BulkString or RespDataType.SimpleString))
            throw new RespireProtocolException("CLUSTER NODES must return topology text.");
        return ParseServerEndpoints(reply.AsString(), source);
    }

    private sealed class ServerEndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly ServerEndpointComparer Instance = new();
        public bool Equals(RespireEndpoint left, RespireEndpoint right)
            => left.Port == right.Port && StringComparer.OrdinalIgnoreCase.Equals(left.Host, right.Host);
        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
    }

    internal static RespireEndpoint[] ParseServerEndpoints(string topology, RespireEndpoint source)
    {
        var byId = new Dictionary<string, RespireEndpoint>(StringComparer.Ordinal);
        var endpoints = new HashSet<RespireEndpoint>(ServerEndpointComparer.Instance);
        foreach (var line in topology.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8) throw new RespireProtocolException("CLUSTER NODES contains an incomplete node row.");
            var flags = fields[2].Split(',');
            // A handshake entry is not yet a Cluster member.
            if (flags.Contains("handshake", StringComparer.Ordinal)) continue;
            RespireEndpoint endpoint;
            if (flags.Contains("myself", StringComparer.Ordinal))
            {
                // Keep the reachable source endpoint, including an explicitly configured NAT port.
                endpoint = source;
            }
            else
            {
                if (flags.Contains("noaddr", StringComparer.Ordinal))
                    throw new RespireConnectionException($"Cluster node {fields[0]} has no usable address; fan-out was not started.");
                var address = fields[1].Split(',');
                var transport = address[0].Split('@')[0];
                var colon = transport.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(transport.AsSpan(colon + 1), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                    throw new RespireProtocolException($"Cluster node {fields[0]} has an invalid client endpoint.");
                var host = transport[..colon].Trim('[', ']');
                if (address.Length > 1 && !string.IsNullOrEmpty(address[1]) && address[1] != "?") host = address[1];
                if (string.IsNullOrEmpty(host) || host == "?")
                    throw new RespireConnectionException($"Cluster node {fields[0]} has no usable host; fan-out was not started.");
                endpoint = new RespireEndpoint(host, port);
            }
            if (byId.TryGetValue(fields[0], out var existing))
            {
                if (!ServerEndpointComparer.Instance.Equals(existing, endpoint))
                    throw new RespireProtocolException("CLUSTER NODES contains conflicting endpoints for a node id.");
                continue;
            }
            if (!endpoints.Add(endpoint))
                throw new RespireProtocolException("CLUSTER NODES associates multiple node ids with one endpoint.");
            byId.Add(fields[0], endpoint);
        }
        if (byId.Count == 0) throw new RespireProtocolException("CLUSTER NODES returned no usable members.");
        return byId.Values.ToArray();
    }
}
