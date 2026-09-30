using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Respire.Testing.Containers;

/// <summary>Owns a disposable Redis or Valkey deployment without depending on a test framework.</summary>
/// <remarks>Cluster and Sentinel fixtures require a local Docker engine. All processes share one
/// container; this models protocol topology, not independent machine or network failures.</remarks>
public sealed class RespireContainerFixture : IAsyncDisposable
{
    /// <summary>The monitored service name used by Sentinel fixtures.</summary>
    public const string SentinelServiceName = "respire-test";
    private readonly IContainer _container;
    private readonly RespireContainerOptions _options;
    private readonly int[] _ports;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;
    private readonly string _cli;
    private readonly string _server;
    private string _startupStep = "container startup";
    private string? _lastReadinessResponse;

    private RespireContainerFixture(RespireContainerOptions options, int[] ports)
    {
        _options = options;
        _ports = ports;
        _server = options.Server == RespireContainerServer.Redis ? "redis-server" : "valkey-server";
        _cli = options.Server == RespireContainerServer.Redis ? "redis-cli" : "valkey-cli";
        var image = options.Image ?? (options.Server == RespireContainerServer.Redis ? "redis:7.2-alpine" : "valkey/valkey:8.1-alpine");
        var builder = new ContainerBuilder(image)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand("mkdir -p /tmp/respire-fixture; exec tail -f /dev/null")
            .WithLabel("respire.testing.fixture", "true");
        foreach (var port in ports)
            builder = options.Topology == RespireContainerTopology.Standalone
                ? builder.WithPortBinding(port, assignRandomHostPort: true)
                : builder.WithPortBinding(port, port);
        _container = builder.Build();
    }

    /// <summary>The owned container ID, for diagnostics.</summary>
    public string ContainerId => _container.Id;
    /// <summary>Host-accessible data endpoints. In Sentinel mode the first endpoint is the initial primary.</summary>
    public IReadOnlyList<RespireEndpoint> DataEndpoints { get; private set; } = Array.Empty<RespireEndpoint>();
    /// <summary>Host-accessible Sentinel endpoints, empty for other topologies.</summary>
    public IReadOnlyList<RespireEndpoint> SentinelEndpoints { get; private set; } = Array.Empty<RespireEndpoint>();

    /// <summary>Starts a deployment and waits for its complete topology. Failure cleans up the owned container.</summary>
    public static async Task<RespireContainerFixture> StartAsync(
        RespireContainerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (!Enum.IsDefined(options.Server)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown server family.");
        if (!Enum.IsDefined(options.Topology)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown topology.");
        if (options.Image is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.Image);
        if (options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "StartupTimeout must be finite and positive.");
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.StartupTimeout);
        var ports = options.Topology switch
        {
            RespireContainerTopology.Standalone => [6379],
            RespireContainerTopology.Cluster => ChooseLocalPorts(3),
            _ => ChooseLocalPorts(5),
        };
        var fixture = new RespireContainerFixture(options, ports);
        try
        {
            await fixture._container.StartAsync(deadline.Token).ConfigureAwait(false);
            if (options.Topology != RespireContainerTopology.Standalone && !IsLocalHost(fixture._container.Hostname))
                throw new NotSupportedException("Cluster and Sentinel fixtures require a local Docker engine because discovery advertises loopback endpoints.");
            await fixture.InitializeAsync(deadline.Token).ConfigureAwait(false);
            return fixture;
        }
        catch (Exception startupError)
        {
            try { await fixture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new AggregateException("Fixture startup and cleanup both failed.", startupError, cleanupError); }
            if (startupError is OperationCanceledException && !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException($"Fixture {options.Server}/{options.Topology} did not become ready within {options.StartupTimeout}; step: {fixture._startupStep}; last reply: {fixture._lastReadinessResponse}", startupError);
            throw;
        }
    }

    /// <summary>Returns fresh client options for the fixture. Dispose clients before disposing their fixture.</summary>
    public RespireOptions CreateOptions()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new RespireOptions
        {
            Endpoints = (_options.Topology == RespireContainerTopology.Sentinel ? SentinelEndpoints : DataEndpoints).ToList(),
            UseCluster = _options.Topology == RespireContainerTopology.Cluster,
            SentinelPrimaryName = _options.Topology == RespireContainerTopology.Sentinel ? SentinelServiceName : null,
        };
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dataCount = _options.Topology switch
        {
            RespireContainerTopology.Standalone => 1,
            RespireContainerTopology.Cluster => 3,
            _ => 2,
        };
        for (var index = 0; index < dataCount; index++)
        {
            var config = BaseConfiguration(_ports[index]);
            if (_options.Topology == RespireContainerTopology.Cluster)
                config += $"cluster-enabled yes\ncluster-config-file /tmp/respire-fixture/nodes-{index}.conf\ncluster-announce-ip 127.0.0.1\ncluster-port {16379 + index}\ncluster-announce-bus-port {16379 + index}\n";
            if (_options.Topology == RespireContainerTopology.Sentinel && index == 1)
                config += $"replicaof 127.0.0.1 {_ports[0]}\nreplica-announce-ip 127.0.0.1\nreplica-announce-port {_ports[1]}\n";
            await StartServerAsync(index, config, sentinel: false, cancellationToken).ConfigureAwait(false);
        }
        if (_options.Topology == RespireContainerTopology.Cluster)
        {
            for (var index = 0; index < dataCount; index++)
            {
                var first = index * 16384 / dataCount;
                var last = (index + 1) * 16384 / dataCount - 1;
                await CliAsync(_ports[index], ["CLUSTER", "ADDSLOTSRANGE", Number(first), Number(last)], cancellationToken).ConfigureAwait(false);
                if (index != 0)
                    await CliAsync(_ports[0], ["CLUSTER", "MEET", "127.0.0.1", Number(_ports[index]), Number(16379 + index)], cancellationToken).ConfigureAwait(false);
            }
            foreach (var port in _ports)
                await WaitForAsync(port, ["CLUSTER", "INFO"], text => text.Contains("cluster_state:ok", StringComparison.Ordinal)
                    && text.Contains("cluster_known_nodes:3", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }
        else if (_options.Topology == RespireContainerTopology.Sentinel)
        {
            await WaitForAsync(_ports[1], ["INFO", "replication"], text => text.Contains("master_link_status:up", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
            for (var index = 2; index < _ports.Length; index++)
            {
                var config = BaseConfiguration(_ports[index]) +
                    $"sentinel monitor {SentinelServiceName} 127.0.0.1 {_ports[0]} 2\nsentinel down-after-milliseconds {SentinelServiceName} 5000\n" +
                    $"sentinel announce-ip 127.0.0.1\nsentinel announce-port {_ports[index]}\n";
                await StartServerAsync(index, config, sentinel: true, cancellationToken).ConfigureAwait(false);
            }
            foreach (var port in _ports.Skip(2))
                await WaitForAsync(port, ["SENTINEL", "CKQUORUM", SentinelServiceName], text => text.StartsWith("OK", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }
        var host = _options.Topology == RespireContainerTopology.Standalone ? _container.Hostname : "127.0.0.1";
        var endpoints = _ports.Select(port => new RespireEndpoint(host, _container.GetMappedPublicPort(port))).ToArray();
        DataEndpoints = Array.AsReadOnly(endpoints[..dataCount]);
        SentinelEndpoints = Array.AsReadOnly(endpoints[dataCount..]);
    }

    private static string BaseConfiguration(int port)
        => $"port {port}\nbind 0.0.0.0\nprotected-mode no\ndaemonize yes\nsave \"\"\nappendonly no\npidfile /tmp/respire-fixture/{port}.pid\nlogfile /tmp/respire-fixture/{port}.log\ndir /tmp/respire-fixture\ndbfilename {port}.rdb\n";

    private async Task StartServerAsync(int index, string config, bool sentinel, CancellationToken cancellationToken)
    {
        var path = $"/tmp/respire-fixture/{index}.conf";
        await _container.CopyAsync(Encoding.UTF8.GetBytes(config), path, ct: cancellationToken).ConfigureAwait(false);
        string[] command = sentinel ? [_server, path, "--sentinel"] : [_server, path];
        await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        await WaitForAsync(_ports[index], ["PING"], text => text.Trim() == "PONG", cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForAsync(int port, string[] command, Func<string, bool> ready, CancellationToken cancellationToken)
    {
        _startupStep = $"{_cli} {string.Join(' ', command)} on port {port}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Sentinel CKQUORUM returns an error until its peers have been discovered.
            var result = await _container.ExecAsync([_cli, "--raw", "-p", Number(port), .. command], cancellationToken).ConfigureAwait(false);
            _lastReadinessResponse = result.Stdout + result.Stderr;
            if (result.ExitCode == 0 && ready(result.Stdout)) return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<string> CliAsync(int port, string[] command, CancellationToken cancellationToken)
        => ExecuteAsync([_cli, "-e", "--raw", "-p", Number(port), .. command], cancellationToken);

    private async Task<string> ExecuteAsync(string[] command, CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Fixture container {_container.Id} command {command[0]} failed ({result.ExitCode}): {result.Stdout} {result.Stderr}");
        return result.Stdout;
    }

    private static bool IsLocalHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int[] ChooseLocalPorts(int count)
    {
        // Cluster/Sentinel advertise ports in replies. Preserve those ports across local NAT.
        // Keep reservations together to avoid duplicate ephemeral choices within this fixture.
        var listeners = new List<TcpListener>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }
            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally { foreach (var listener in listeners) listener.Stop(); }
    }

    /// <summary>Removes the owned container and all of its server processes. Concurrent calls share cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            Volatile.Write(ref _disposed, 1);
            return new ValueTask(_disposeTask ??= _container.DisposeAsync().AsTask());
        }
    }
}
