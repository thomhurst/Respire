using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Testing.Containers;
using System.Net;
using System.Net.Sockets;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

// Opt in before fixture initialization, so ordinary CI never pulls these images.
[AttributeUsage(AttributeTargets.Class)]
public sealed class ServerCompatibilityOnlyAttribute() : SkipAttribute(
    "Set RESPIRE_COMPATIBILITY_SERVER to garnet, dragonfly, keydb, or valkey92 to run the compatibility suite.")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RESPIRE_COMPATIBILITY_SERVER")));
}

public sealed class CompatibleServer : IAsyncInitializer, IAsyncDisposable
{
    private const string GarnetImage = "ghcr.io/microsoft/garnet:2.1.8";
    public static string Name => Environment.GetEnvironmentVariable("RESPIRE_COMPATIBILITY_SERVER") ?? "";
    private IContainer? _container;
    public RespireOptions Options(RespireProtocol protocol) => new()
    {
        Endpoints = [new(_container!.Hostname, _container.GetMappedPublicPort(6379))],
        Protocol = protocol,
        ClientName = "respire-compatibility",
        Connections = 1,
        CommandTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task InitializeAsync() => _container = await StartAsync();

    public static async Task<(IContainer Container, int Port)> StartClusterAsync()
    {
        if (Name == "dragonfly") return (await StartAsync(cluster: true), 6379);

        // Garnet requires advertised and listening ports to match; KeyDB needs its
        // advertised port at startup. Reserve a host port and retry only bind collisions.
        for (var attempt = 0; ; attempt++)
        {
            int port;
            using (var reservation = new TcpListener(IPAddress.Loopback, 0))
            {
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var containerPort = Name == "garnet" ? port : 6379;
            ContainerBuilder builder;
            if (Name == "garnet")
                builder = new ContainerBuilder(GarnetImage)
                .WithCommand("--cluster", "--port", portText,
                    "--cluster-announce-ip", "127.0.0.1", "--memory", "128m", "--page", "4m", "--segment", "16m", "--index", "8m");
            else if (Name == "valkey92")
                builder = new ContainerBuilder(Valkey92IntegrationTests.Image)
                .WithCommand("valkey-server", "--save", "", "--appendonly", "no",
                    "--cluster-enabled", "yes", "--cluster-announce-ip", "127.0.0.1", "--cluster-announce-port", portText);
            else
                builder = new ContainerBuilder("eqalpha/keydb:x86_64_v6.3.4")
                .WithCommand("keydb-server", "--server-threads", "1", "--save", "", "--appendonly", "no",
                    "--cluster-enabled", "yes", "--cluster-announce-ip", "127.0.0.1", "--cluster-announce-port", portText);
            var container = builder
                .WithPortBinding(port, containerPort)
                .WithCreateParameterModifier(parameters =>
                {
                    foreach (var bindings in parameters.HostConfig!.PortBindings.Values)
                        foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
                })
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(containerPort))
                .Build();
            try
            {
                await container.StartAsync();
                return (container, containerPort);
            }
            catch (Exception error)
            {
                await container.DisposeAsync();
                if (attempt >= 2 || !ContainerPortCollision.IsMatch(error, [port])) throw;
            }
        }
    }

    public static Task<IContainer> StartAsync(bool cluster = false)
    {
        var (image, command) = Name switch
        {
            "garnet" => (GarnetImage, new[]
                { "--lua", "--memory", "128m", "--page", "4m", "--segment", "16m", "--index", "8m" }
                .Concat(cluster ? ["--cluster"] : Array.Empty<string>()).ToArray()),
            "dragonfly" => ("docker.dragonflydb.io/dragonflydb/dragonfly:v2.0.0", new[]
                { "--proactor_threads=1", "--maxmemory=256mb", "--logtostderr" }
                .Concat(cluster ? ["--cluster_mode=emulated"] : Array.Empty<string>()).ToArray()),
            "keydb" => ("eqalpha/keydb:x86_64_v6.3.4", new[]
                { "keydb-server", "--server-threads", "1", "--save", "", "--appendonly", "no" }
                .Concat(cluster ? ["--cluster-enabled", "yes", "--cluster-announce-ip", "127.0.0.1"] : Array.Empty<string>()).ToArray()),
            "valkey92" => (Valkey92IntegrationTests.Image, new[] { "valkey-server", "--save", "", "--appendonly", "no" }),
            _ => throw new InvalidOperationException($"Unknown RESPIRE_COMPATIBILITY_SERVER '{Name}'."),
        };
        return ContainerStartRetry.StartAsync(() => new ContainerBuilder(image)
            .WithCommand(command)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build(), [6379]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

[ServerCompatibilityOnly]
[Category(TestCategories.ProtocolIndependent)]
[Category("ServerCompatibility")]
[ClassDataSource<CompatibleServer>(Shared = SharedType.PerClass)]
public class ServerCompatibilityTests(CompatibleServer server)
{
    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    [Arguments(RespireProtocol.Auto)]
    public async Task HandshakeAndCoreDataTypes(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol));
        var key = $"compat:{Guid.NewGuid():N}:";
        await client.SetAsync(key + "string", "value");
        (await client.GetStringAsync(key + "string")).Should().Be("value");
        using var hashSet = await client.ExecuteAsync(RespireCommands.Hash.HSET, key + "hash", "field", "value");
        using var hashGet = await client.ExecuteAsync(RespireCommands.Hash.HGET, key + "hash", "field");
        hashGet.AsString().Should().Be("value");
        using var listPush = await client.ExecuteAsync(RespireCommands.List.RPUSH, key + "list", "first", "second");
        using var listPop = await client.ExecuteAsync(RespireCommands.List.LPOP, key + "list");
        listPop.AsString().Should().Be("first");
        using var setAdd = await client.ExecuteAsync(RespireCommands.Set.SADD, key + "set", "member");
        using var setContains = await client.ExecuteAsync(RespireCommands.Set.SISMEMBER, key + "set", "member");
        setContains.AsBoolean().Should().BeTrue();
        using var sortedAdd = await client.ExecuteAsync(RespireCommands.SortedSet.ZADD, key + "sorted", 42, "member");
        using var sortedScore = await client.ExecuteAsync(RespireCommands.SortedSet.ZSCORE, key + "sorted", "member");
        sortedScore.AsDouble().Should().Be(42);
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task ClientSetInfo(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol) with { AllowAdmin = true });
        var connection = await client.Server.GetClientConnectionAsync();
        if (CompatibleServer.Name == "keydb")
        {
            Func<Task> setInfo = async () => await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "Respire.Compatibility");
            await setInfo.Should().ThrowAsync<RespireServerException>()
                .WithMessage("*Unknown subcommand*SETINFO*");
        }
        else
        {
            await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "Respire.Compatibility");
        }
        // Check the same physical connection after success or the known optional-command error.
        (await connection.GetNameAsync()).Should().Be("respire-compatibility");
        Skip.When(CompatibleServer.Name == "keydb", "KeyDB 6.3.4 rejects CLIENT SETINFO; connection usability after rejection is verified.");
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    [Arguments(RespireProtocol.Auto)]
    public async Task LibraryInfoHandshakeIsOptional(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol));
        // KeyDB rejects SETINFO; Garnet and Dragonfly may accept it. In every case,
        // both optional replies must be consumed before publishing this connection.
        var connection = await client.Server.GetClientConnectionAsync();
        (await connection.GetNameAsync()).Should().Be("respire-compatibility");
        await client.SetAsync("compat:library-info", "usable");
        (await client.GetStringAsync("compat:library-info")).Should().Be("usable");
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task PubSub(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol));
        var channel = $"compat:channel:{Guid.NewGuid():N}";
        await using var subscription = await client.SubscribeAsync(channel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = subscription.GetAsyncEnumerator(timeout.Token);
        (await client.PublishAsync(channel, "message")).Should().Be(1);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Text.Should().Be("message");
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task Scripting(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol));
        var key = $"compat:lua:{Guid.NewGuid():N}";
        using var result = await client.ExecuteAsync(RespireCommands.Scripting.EVAL,
            "redis.call('SET', KEYS[1], ARGV[1]); return redis.call('GET', KEYS[1])", 1, key, "script-value");
        result.AsString().Should().Be("script-value");
        (await client.GetStringAsync(key)).Should().Be("script-value");
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task Transactions(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(server.Options(protocol));
        var key = $"compat:tx:{Guid.NewGuid():N}";
        await using var transaction = client.CreateTransaction();
        var set = transaction.Set(key, "41");
        var increment = transaction.Increment(key);
        var get = transaction.GetString(key);
        await transaction.CommitAsync();
        set.Result.Should().BeTrue();
        increment.Result.Should().Be(42);
        get.Result.Should().Be("42");
    }

    [Test]
    public async Task ClientSideCacheInvalidation()
    {
        Skip.When(CompatibleServer.Name == "garnet", "Garnet 2.1.8 rejects CLIENT TRACKING with ERR unknown subcommand 'TRACKING'.");
        var options = server.Options(RespireProtocol.Resp3);
        await using var writer = await RespireClient.ConnectAsync(options);
        await using var cached = await RespireClient.ConnectAsync(options with { ClientSideCache = new() });
        var key = $"compat:cache:{Guid.NewGuid():N}";
        await writer.SetAsync(key, "before");
        (await cached.GetStringAsync(key)).Should().Be("before");
        (await cached.GetStringAsync(key)).Should().Be("before");
        cached.ClientSideCache!.GetStatistics().Hits.Should().BeGreaterThan(0);
        await writer.SetAsync(key, "after");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (cached.ClientSideCache.Count != 0) await Task.Delay(10, timeout.Token);
        (await cached.GetStringAsync(key)).Should().Be("after");
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task ClusterDiscoveryAndRouting(RespireProtocol protocol)
    {
        var started = await CompatibleServer.StartClusterAsync();
        await using var container = started.Container;
        var options = new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(started.Port))],
            Protocol = protocol,
            Connections = 1,
            CommandTimeout = TimeSpan.FromSeconds(10),
        };
        await using (var admin = await RespireClient.ConnectAsync(options))
        {
            if (CompatibleServer.Name == "dragonfly")
            {
                using var announceIp = await admin.ExecuteAsync("CONFIG", "SET", "cluster_announce_ip", container.Hostname);
                using var announcePort = await admin.ExecuteAsync("CONFIG", "SET", "announce_port", container.GetMappedPublicPort(started.Port));
            }
            if (CompatibleServer.Name != "dragonfly")
            {
                for (var first = 0; first < 16384; first += 1024)
                {
                    RespireValue[] slots = ["ADDSLOTS", .. Enumerable.Range(first, 1024).Select(i => (RespireValue)i)];
                    using var assigned = await admin.ExecuteAsync("CLUSTER", slots);
                }
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var info = await admin.ExecuteAsync("CLUSTER", ["INFO"], cancellationToken: timeout.Token);
                if (info.AsString()!.Contains("cluster_state:ok", StringComparison.Ordinal)) break;
                await Task.Delay(100, timeout.Token);
            }
        }
        await using var client = await RespireClient.ConnectAsync(options with { UseCluster = true });
        // Seed fallback can serve commands even when discovery leaves the slot table empty.
        // Check every slot before commands can populate ownership through redirects.
        var cluster = client.Core.Cluster;
        cluster.Should().NotBeNull();
        for (var slot = 0; slot < 16384; slot++)
        {
            var owner = cluster!.GetSlotOwnerEndpoint(slot);
            owner.Should().NotBeNull("slot {0} must have a discovered primary", slot);
            owner!.Value.Port.Should().Be(container.GetMappedPublicPort(started.Port));
        }
        // Different hash slots exercise the discovered slot table, even on a single primary.
        foreach (var key in new[] { "compat:{first}", "compat:{second}" })
        {
            await client.SetAsync(key, key);
            (await client.GetStringAsync(key)).Should().Be(key);
        }
    }
}
