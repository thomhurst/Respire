using System.Net;
using Respire.Internal;
using StackExchange.Redis;

#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

public sealed partial class RespireConnectionMultiplexer
{
    private readonly Dictionary<RespireEndpoint, CompatServer> _servers = [];
    private CompatSubscriber? _subscriber;

    internal CompatDatabase DefaultDatabase => (CompatDatabase)GetDatabase();

    /// <inheritdoc />
    public EndPoint[] GetEndPoints(bool configuredOnly = false)
    {
        lock (_gate) ThrowIfClosed();
        if (configuredOnly) return _configuration.Endpoints.Concat(_configuration.ReplicaEndpoints)
            .Distinct().Select(ToEndPoint).ToArray();
        var client = DefaultDatabase.Client;
        return Wait(Run(async token =>
        {
            var discovered = await ((ServerCommands)client.Server).DiscoverServerEndpointsAsync(token).ConfigureAwait(false);
            var replicas = client.Core.Sentinel is { } sentinel
                ? await sentinel.DiscoverReplicaEndpointsAsync(token).ConfigureAwait(false)
                : _configuration.ReplicaEndpoints.ToArray();
            return discovered.Concat(replicas).Distinct().Select(ToEndPoint).ToArray();
        }));
    }

    internal static EndPoint ToEndPoint(RespireEndpoint endpoint) => endpoint.Port == 0
        ? new System.Net.Sockets.UnixDomainSocketEndPoint(endpoint.Host)
        : IPAddress.TryParse(endpoint.Host, out var address) ? new IPEndPoint(address, endpoint.Port)
        : new DnsEndPoint(endpoint.Host, endpoint.Port);

    private static RespireEndpoint FromEndPoint(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => new(ip.Address.ToString(), ip.Port),
        DnsEndPoint dns => new(dns.Host, dns.Port),
        System.Net.Sockets.UnixDomainSocketEndPoint socket => new(socket.ToString(), 0),
        _ => throw Compatibility.Unsupported("GetServer endpoint type"),
    };

    /// <inheritdoc />
    public IServer GetServer(EndPoint endpoint, object? asyncState = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (asyncState is not null) throw Compatibility.Unsupported("asyncState");
        var native = FromEndPoint(endpoint);
        lock (_gate)
        {
            ThrowIfClosed();
            if (!_servers.TryGetValue(native, out var server))
            {
                // A physical server handle must never redirect to a different node or own the wrapped client.
                var client = RespireClient.Create(_configuration with
                {
                    Endpoints = [native], ReplicaEndpoints = [], SentinelPrimaryName = null,
                    UseCluster = false, ReadFrom = RespireReadFrom.Primary, Connections = 1, ClientSideCache = null,
                });
                server = new(this, client, ToEndPoint(native));
                _servers.Add(native, server);
            }
            return server;
        }
    }

    /// <inheritdoc />
    public IServer GetServer(string host, int port, object? asyncState = null) => GetServer(new DnsEndPoint(host, port), asyncState);
    /// <inheritdoc />
    public IServer GetServer(IPAddress host, int port) => GetServer(new IPEndPoint(host, port));
    /// <inheritdoc />
    public IServer GetServer(string hostAndPort, object? asyncState = null)
    {
        var options = ConfigurationOptions.Parse(hostAndPort);
        if (options.EndPoints.Count != 1) throw new ArgumentException("Specify exactly one endpoint.", nameof(hostAndPort));
        return GetServer(options.EndPoints[0], asyncState);
    }
    /// <inheritdoc />
    public IServer GetServer(RedisKey key, object? asyncState = null, CommandFlags flags = CommandFlags.None)
    {
        if (asyncState is not null) throw Compatibility.Unsupported("asyncState");
        return GetServer(DefaultDatabase.IdentifyEndpoint(key, flags)!);
    }
    /// <inheritdoc />
    public IServer[] GetServers() => GetEndPoints().Select(endpoint => GetServer(endpoint)).ToArray();
    /// <inheritdoc />
    public ISubscriber GetSubscriber(object? asyncState = null)
    {
        if (asyncState is not null) throw Compatibility.Unsupported("asyncState");
        lock (_gate)
        {
            ThrowIfClosed();
            return _subscriber ??= new(this);
        }
    }
}
