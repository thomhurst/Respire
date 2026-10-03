using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Respire.Testing.Containers;

/// <summary>Owns a disposable Redis or Valkey deployment without depending on a test framework.</summary>
/// <remarks>All fixtures require a local Docker engine and publish ports only on IPv4 loopback. All processes share one
/// container; this models protocol topology, not independent machine or network failures.</remarks>
public sealed class RespireContainerFixture : IAsyncDisposable
{
    /// <summary>The monitored service name used by Sentinel fixtures.</summary>
    public const string SentinelServiceName = "respire-test";
    private const string DefaultRedisImage = "redis:7.2-alpine";
    private const string DefaultValkeyImage = "valkey/valkey:8.1-alpine";
    private const int ClusterPrimaryCount = 3;
    private const int MaximumStartupAttempts = 3;
    // Cluster bus traffic stays inside the shared container; these ports are never published.
    private const int ClusterBusPortStart = 16379;
    private const int SentinelQuorum = 2;
    private const int SentinelDownAfterMilliseconds = 5000;
    // A failed election retries after twice this interval. Keep retries within fixture tests'
    // bounded failover windows instead of the server default's six-minute retry delay.
    private const int SentinelFailoverTimeoutMilliseconds = 10000;
    private readonly IContainer _container;
    private readonly RespireContainerOptions _options;
    private readonly int[] _ports;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;
    private readonly string _cli;
    private readonly string _server;
    // Startup is sequential; only its continuation writes or reads these diagnostics.
    private string _startupStep = "container startup";
    private string? _lastReadinessResponse;

    private RespireContainerFixture(RespireContainerOptions options, int[] ports, IContainer container)
    {
        _options = options;
        _ports = ports;
        _server = options.Server == RespireContainerServer.Redis ? "redis-server" : "valkey-server";
        _cli = options.Server == RespireContainerServer.Redis ? "redis-cli" : "valkey-cli";
        _container = container;
    }

    internal static IContainer BuildContainer(RespireContainerOptions options, int[] ports)
    {
        var image = options.Image ?? (options.Server == RespireContainerServer.Redis ? DefaultRedisImage : DefaultValkeyImage);
        var builder = new ContainerBuilder(image)
            .WithCreateParameterModifier(parameters =>
            {
                var hostConfig = parameters.HostConfig!;
                hostConfig.Init = true;
                foreach (var bindings in hostConfig.PortBindings.Values)
                    foreach (var binding in bindings)
                        binding.HostIP = "127.0.0.1";
            })
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand("exec tail -f /dev/null")
            .WithLabel("respire.testing.fixture", "true");
        foreach (var port in ports)
            builder = options.Topology == RespireContainerTopology.Standalone
                ? builder.WithPortBinding(port, assignRandomHostPort: true)
                : builder.WithPortBinding(port, port);
        return builder.Build();
    }

    /// <summary>The owned container ID, for diagnostics.</summary>
    public string ContainerId => _container.Id;

    internal Task<string> ReadServerLogsAsync(CancellationToken cancellationToken)
        => ExecuteAsync(["cat", .. _ports.Select(port => $"/tmp/respire-fixture/{port}.log")], cancellationToken);
    /// <summary>Host-accessible data endpoints. In Sentinel mode the first endpoint is the initial primary.</summary>
    public IReadOnlyList<RespireEndpoint> DataEndpoints { get; private set; } = Array.Empty<RespireEndpoint>();
    /// <summary>Host-accessible Sentinel endpoints, empty for other topologies.</summary>
    public IReadOnlyList<RespireEndpoint> SentinelEndpoints { get; private set; } = Array.Empty<RespireEndpoint>();

    /// <summary>Starts a deployment and waits for its complete topology. Failure cleans up the owned container.</summary>
    public static Task<RespireContainerFixture> StartAsync(
        RespireContainerOptions? options = null, CancellationToken cancellationToken = default)
        => StartAsync(options ?? new(), cancellationToken, createContainer: null);

    // The instance-local factory lets tests inject a collision between port selection and
    // Docker startup without changing global state or the public fixture API.
    internal static async Task<RespireContainerFixture> StartAsync(
        RespireContainerOptions options, CancellationToken cancellationToken,
        Func<int[], CancellationToken, Task<IContainer>>? createContainer, TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(options.Server)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown server family.");
        if (!Enum.IsDefined(options.Topology)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown topology.");
        if (options.Image is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.Image);
        if (options.StartupTimeout <= TimeSpan.Zero || options.StartupTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "StartupTimeout must be finite and positive.");
        cancellationToken.ThrowIfCancellationRequested();
        using var startupDeadline = new CancellationTokenSource(options.StartupTimeout, timeProvider ?? TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupDeadline.Token);
        var excludedPorts = new HashSet<int>();
        var collisions = new List<Exception>();
        for (var attempt = 1; ; attempt++)
        {
            RespireContainerFixture? fixture = null;
            int[] ports = [];
            var startingContainer = false;
            var containerStarted = false;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                ports = options.Topology switch
                {
                    RespireContainerTopology.Standalone => [6379],
                    RespireContainerTopology.Cluster => ChooseLocalPorts(ClusterPrimaryCount, excludedPorts),
                    _ => ChooseLocalPorts(5, excludedPorts),
                };
                var container = createContainer is null ? BuildContainer(options, ports)
                    : await createContainer(ports, deadline.Token).ConfigureAwait(false);
                fixture = new RespireContainerFixture(options, ports, container);
                deadline.Token.ThrowIfCancellationRequested();
                startingContainer = true;
                await container.StartAsync(deadline.Token).ConfigureAwait(false);
                startingContainer = false;
                containerStarted = true;
                if (!IsLocalHost(container.Hostname))
                    throw new NotSupportedException("Container fixtures require a local Docker engine because published ports bind only to loopback.");
                await fixture.InitializeAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return fixture;
            }
            catch (Exception startupError)
            {
                startupError.Data["RespireFixture.StartupStep"] = fixture?._startupStep ?? "port selection/container creation";
                startupError.Data["RespireFixture.LastReadinessResponse"] = fixture?._lastReadinessResponse;
                startupError.Data["RespireFixture.StartupAttempt"] = attempt;
                startupError.Data["RespireFixture.SelectedPorts"] = ports.ToArray();
                if (collisions.Count != 0) startupError.Data["RespireFixture.PreviousPortCollisions"] = collisions.ToArray();
                // Inspect before removal, without allowing diagnostics to hide the failure.
                string? containerId = null;
                try { containerId = fixture?.ContainerId; }
                catch (Exception) { }
                startupError.Data["RespireFixture.ContainerId"] = containerId;
                if (fixture is not null)
                {
                    string? diagnostics = null;
                    if (containerStarted)
                    {
                        diagnostics = await ContainerStartupDiagnostics.CaptureAsync(fixture._container, ports, timeProvider).ConfigureAwait(false);
                        startupError.Data["RespireFixture.DaemonLogs"] = diagnostics;
                    }
                    try { await fixture.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException($"Fixture startup and cleanup both failed (container: {containerId ?? "unavailable"}).",
                            collisions.Append(startupError).Append(cleanupError));
                    }
                    finally
                    {
                        // Test runners receive the tail automatically, but output cannot
                        // delay container cleanup or replace the original failure.
                        if (diagnostics is not null)
                            await ContainerStartupDiagnostics.ReportAsync(diagnostics).ConfigureAwait(false);
                    }
                }
                // Cleanup is always awaited. No fresh container starts after cancellation,
                // even when the deadline expires while removing a failed attempt.
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Fixture startup was cancelled.", startupError, cancellationToken);
                if (deadline.IsCancellationRequested)
                    throw new TimeoutException($"Fixture {options.Server}/{options.Topology} did not become ready within {options.StartupTimeout}; step: {fixture?._startupStep}; last reply: {fixture?._lastReadinessResponse}", startupError);
                if (startingContainer && options.Topology != RespireContainerTopology.Standalone
                    && attempt < MaximumStartupAttempts && ContainerPortCollision.IsMatch(startupError, ports))
                {
                    collisions.Add(startupError);
                    excludedPorts.UnionWith(ports);
                    continue;
                }
                throw;
            }
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

    /// <summary>Stops one data node in an owned Sentinel fixture.</summary>
    public async Task StopDataNodeAsync(int index, CancellationToken cancellationToken = default)
    {
        ValidateSentinelDataNode(index);
        var port = _ports[index];
        _ = await _container.ExecAsync([_cli, "-p", Number(port), "SHUTDOWN", "NOSAVE"], cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var ping = await _container.ExecAsync([_cli, "-p", Number(port), "PING"], deadline.Token).ConfigureAwait(false);
            if (ping.ExitCode != 0 || ping.Stdout.Trim() != "PONG") return;
            await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Restarts one stopped data node in an owned Sentinel fixture.</summary>
    public async Task StartDataNodeAsync(int index, CancellationToken cancellationToken = default)
    {
        ValidateSentinelDataNode(index);
        await ExecuteAsync([_server, $"/tmp/respire-fixture/{index}.conf"], cancellationToken).ConfigureAwait(false);
        await WaitForAsync(_ports[index], ["PING"], text => text.Trim() == "PONG", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until one Sentinel data node reports itself as a replica.</summary>
    public async Task WaitForDataNodeReplicaAsync(int index, CancellationToken cancellationToken = default)
    {
        ValidateSentinelDataNode(index);
        await WaitForAsync(_ports[index], ["INFO", "replication"],
            text => text.Contains("role:slave", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    private void ValidateSentinelDataNode(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.Topology != RespireContainerTopology.Sentinel)
            throw new InvalidOperationException("Data-node controls require a Sentinel fixture.");
        if ((uint)index >= (uint)DataEndpoints.Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Container startup does not await entrypoint shell commands. Keep directory
        // creation ordered before configuration copies and report mkdir failures directly.
        await ExecuteAsync(["mkdir", "-p", "/tmp/respire-fixture"], cancellationToken).ConfigureAwait(false);
        var dataCount = _options.Topology switch
        {
            RespireContainerTopology.Standalone => 1,
            RespireContainerTopology.Cluster => ClusterPrimaryCount,
            _ => 2,
        };
        for (var index = 0; index < dataCount; index++)
        {
            var config = BaseConfiguration(_ports[index]);
            if (_options.Topology == RespireContainerTopology.Cluster)
                config += $"cluster-enabled yes\ncluster-config-file /tmp/respire-fixture/nodes-{index}.conf\ncluster-announce-ip 127.0.0.1\ncluster-port {ClusterBusPortStart + index}\ncluster-announce-bus-port {ClusterBusPortStart + index}\n";
            if (_options.Topology == RespireContainerTopology.Sentinel && index == 1)
                config += $"replicaof 127.0.0.1 {_ports[0]}\nreplica-announce-ip 127.0.0.1\nreplica-announce-port {_ports[1]}\n";
            await StartServerAsync(index, config, sentinel: false, cancellationToken).ConfigureAwait(false);
        }
        if (_options.Topology == RespireContainerTopology.Cluster)
            await InitializeClusterAsync(dataCount, cancellationToken).ConfigureAwait(false);
        else if (_options.Topology == RespireContainerTopology.Sentinel)
            await InitializeSentinelAsync(cancellationToken).ConfigureAwait(false);
        const string host = "127.0.0.1";
        var endpoints = _ports.Select(port => new RespireEndpoint(host, _container.GetMappedPublicPort(port))).ToArray();
        DataEndpoints = Array.AsReadOnly(endpoints[..dataCount]);
        SentinelEndpoints = Array.AsReadOnly(endpoints[dataCount..]);
    }

    private async Task InitializeClusterAsync(int dataCount, CancellationToken cancellationToken)
    {
        for (var index = 0; index < dataCount; index++)
        {
            var first = index * 16384 / dataCount;
            var last = (index + 1) * 16384 / dataCount - 1;
            await CliAsync(_ports[index], ["CLUSTER", "ADDSLOTSRANGE", Number(first), Number(last)], cancellationToken).ConfigureAwait(false);
            if (index != 0)
                await CliAsync(_ports[0], ["CLUSTER", "MEET", "127.0.0.1", Number(_ports[index]), Number(ClusterBusPortStart + index)], cancellationToken).ConfigureAwait(false);
        }
        foreach (var port in _ports)
            await WaitForAsync(port, ["CLUSTER", "INFO"], text =>
            {
                var fields = text.Split('\n', StringSplitOptions.TrimEntries);
                return fields.Contains("cluster_state:ok", StringComparer.Ordinal)
                    && fields.Contains($"cluster_known_nodes:{dataCount}", StringComparer.Ordinal);
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeSentinelAsync(CancellationToken cancellationToken)
    {
        await WaitForAsync(_ports[1], ["INFO", "replication"], text => text.Contains("master_link_status:up", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        for (var index = 2; index < _ports.Length; index++)
        {
            var config = BaseConfiguration(_ports[index]) +
                $"sentinel monitor {SentinelServiceName} 127.0.0.1 {_ports[0]} {SentinelQuorum}\nsentinel down-after-milliseconds {SentinelServiceName} {SentinelDownAfterMilliseconds}\n" +
                $"sentinel failover-timeout {SentinelServiceName} {SentinelFailoverTimeoutMilliseconds}\n" +
                $"sentinel announce-ip 127.0.0.1\nsentinel announce-port {_ports[index]}\n";
            await StartServerAsync(index, config, sentinel: true, cancellationToken).ConfigureAwait(false);
        }
        foreach (var port in _ports.Skip(2))
        {
            await WaitForAsync(port, ["SENTINEL", "CKQUORUM", SentinelServiceName], text => text.StartsWith("OK", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
            await WaitForAsync(port, ["SENTINEL", "GET-MASTER-ADDR-BY-NAME", SentinelServiceName], text =>
            {
                var fields = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return fields.Length == 2 && fields[0] == "127.0.0.1" && fields[1] == Number(_ports[0]);
            }, cancellationToken).ConfigureAwait(false);
            await WaitForAsync(port, ["SENTINEL", "REPLICAS", SentinelServiceName], ReplicaIsReady, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string DaemonLogPath(int port) => $"/tmp/respire-fixture/{Number(port)}.log";

    private static string BaseConfiguration(int port)
        => $"port {port}\nbind 0.0.0.0\nprotected-mode no\ndaemonize yes\nsave \"\"\nappendonly no\npidfile /tmp/respire-fixture/{port}.pid\nlogfile {DaemonLogPath(port)}\ndir /tmp/respire-fixture\ndbfilename {port}.rdb\n";

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
        var delayMilliseconds = 100;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Sentinel CKQUORUM returns an error until its peers have been discovered.
            var result = await _container.ExecAsync([_cli, "--raw", "-p", Number(port), .. command], cancellationToken).ConfigureAwait(false);
            _lastReadinessResponse = result.Stdout + result.Stderr;
            if (result.ExitCode == 0 && ready(result.Stdout)) return;
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            delayMilliseconds = Math.Min(delayMilliseconds * 2, 1000);
        }
    }

    private bool ReplicaIsReady(string response)
    {
        // This topology has exactly one replica. --raw flattens its field/value pairs.
        var fields = response.Split('\n', StringSplitOptions.TrimEntries);
        var expectedIp = false;
        var expectedPort = false;
        var healthy = false;
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            switch (fields[index])
            {
                case "ip": expectedIp = fields[index + 1] == "127.0.0.1"; break;
                case "port": expectedPort = fields[index + 1] == Number(_ports[1]); break;
                case "flags": healthy = fields[index + 1] == "slave"; break;
            }
        }
        return expectedIp && expectedPort && healthy;
    }

    private Task<string> CliAsync(int port, string[] command, CancellationToken cancellationToken)
        => ExecuteAsync([_cli, "-e", "--raw", "-p", Number(port), .. command], cancellationToken);

    private async Task<string> ExecuteAsync(string[] command, CancellationToken cancellationToken)
    {
        _startupStep = string.Join(' ', command);
        _lastReadinessResponse = null;
        var result = await _container.ExecAsync(command, cancellationToken).ConfigureAwait(false);
        _lastReadinessResponse = result.Stdout + result.Stderr;
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Fixture container {_container.Id} command {command[0]} failed ({result.ExitCode}): {result.Stdout} {result.Stderr}");
        return result.Stdout;
    }

    private static bool IsLocalHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int[] ChooseLocalPorts(int count, HashSet<int> excludedPorts)
    {
        // Cluster/Sentinel advertise ports in replies. Preserve those ports across local NAT.
        // Keep reservations together to avoid duplicate ephemeral choices within this fixture.
        var listeners = new List<TcpListener>();
        var ports = new List<int>(count);
        try
        {
            while (ports.Count < count)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                // Retain excluded reservations too, so the next choice cannot repeat them.
                if (!excludedPorts.Contains(port) && (port < ClusterBusPortStart || port >= ClusterBusPortStart + ClusterPrimaryCount))
                    ports.Add(port);
            }
            return ports.ToArray();
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
