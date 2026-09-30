using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterDatabaseIntegrationTests
{
    [Test]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(2, 3)]
    [Arguments(3, 3)]
    public async Task ValkeyClusterSelectsDatabaseOnCommandDedicatedAndPubSubConnections(int protocol, int database)
    {
        await using var container = Build("valkey/valkey:9.0-alpine", clusterDatabases: 4);
        await StartCluster(container, "valkey-cli");
        var endpoint = new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379));
        var name = $"selected-db-{database}-{protocol}";
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Database = database, Connections = 2, ClientName = name,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ClientSideCache = protocol == 3 ? new() : null,
            Endpoints = { endpoint },
        });
        await using var observer = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = { endpoint }, Connections = 1 });
        await observer.SetAsync("key", "db-zero");
        await client.SetAsync("key", "selected-db");
        (await client.GetStringAsync("key")).Should().Be("selected-db");
        (await observer.GetStringAsync("key")).Should().Be("db-zero");

        using (var batch = client.CreateBatch())
        {
            var first = batch.Set("batch", "selected-db");
            var second = batch.GetString("key");
            await batch.ExecuteAsync();
            first.Result.Should().BeTrue();
            second.Result.Should().Be("selected-db");
        }
        await using (var transaction = client.CreateTransaction())
        {
            var set = transaction.Set("{same}:transaction", "selected-db");
            await transaction.CommitAsync();
            set.Result.Should().BeTrue();
        }
        (await observer.GetStringAsync("batch")).Should().BeNull();
        (await observer.GetStringAsync("{same}:transaction")).Should().BeNull();

        await client.Lists.RightPushAsync("queue", "selected-db");
        (await client.Lists.LeftPopAsync("queue", waitFor: TimeSpan.FromSeconds(1))).Should().Be("selected-db");
        await using var subscription = await client.SubscribeAsync("channel");
        var rows = (await observer.Server.ClientsAsync()).Where(row => row.Name == name).ToArray();
        // Two multiplexed sockets, one blocking lease, and one subscription socket.
        rows.Length.Should().BeGreaterThanOrEqualTo(4);
        rows.Should().OnlyContain(row => row.Database == database);
        rows.Should().Contain(row => row.Flags.Contains('P'));
    }

    [Test]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    [Arguments("valkey/valkey:8.1-alpine", 2)]
    [Arguments("valkey/valkey:8.1-alpine", 3)]
    public async Task UnsupportedClustersKeepConfigurationError(string image, int protocol)
    {
        await using var container = Build(image);
        var cli = image.StartsWith("redis:", StringComparison.Ordinal) ? "redis-cli" : "valkey-cli";
        await StartCluster(container, cli);
        Func<Task> connect = async () =>
        {
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                UseCluster = true, Database = 1, Connections = 1,
                Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
                Endpoints = { new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379)) },
            });
        };
        await connect.Should().ThrowAsync<RespireConfigurationException>().WithMessage("*database 0*");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ValkeyDefaultClusterDatabaseCountRejectsOutOfRangeSelection(int protocol)
    {
        await using var container = Build("valkey/valkey:9.0-alpine");
        await StartCluster(container, "valkey-cli");
        Func<Task> connect = async () =>
        {
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                UseCluster = true, Database = 1, Connections = 1,
                Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
                Endpoints = { new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379)) },
            });
        };
        var result = await connect.Should().ThrowAsync<RespireConnectionException>();
        result.Which.ToString().Should().Contain("SELECT failed").And.Contain("DB index is out of range");
    }

    private static IContainer Build(string image, int? clusterDatabases = null)
    {
        var command = image.StartsWith("redis:", StringComparison.Ordinal) ? "redis-server" : "valkey-server";
        List<string> arguments = [command, "--cluster-enabled", "yes", "--cluster-config-file", "nodes.conf",
            "--cluster-announce-ip", "127.0.0.1", "--appendonly", "no"];
        if (clusterDatabases.HasValue) arguments.AddRange(["--cluster-databases", clusterDatabases.Value.ToString()]);
        return new ContainerBuilder(image).WithPortBinding(6379, true).WithCommand(arguments.ToArray())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    }

    private static async Task StartCluster(IContainer container, string cli)
    {
        await container.StartAsync();
        await Run(container, [cli, "-e", "CONFIG", "SET", "cluster-announce-port", container.GetMappedPublicPort(6379).ToString()]);
        await Run(container, [cli, "-e", "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var result = await container.ExecAsync([cli, "CLUSTER", "INFO"], ready.Token);
            if (result.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) return;
            await Task.Delay(100, ready.Token);
        }
    }

    private static async Task Run(IContainer container, string[] command)
    {
        var result = await container.ExecAsync(command);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Cluster setup failed: {result.Stdout} {result.Stderr}");
    }
}
