using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// Rows share one single-node cluster per image and configuration; none of them moves slots.
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class ClusterDatabaseIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(2, 3)]
    [Arguments(3, 3)]
    public async Task ValkeyClusterSelectsDatabaseOnCommandDedicatedAndPubSubConnections(int protocol, int database)
    {
        var container = await servers.SingleNodeClusterAsync("valkey/valkey:9.0-alpine", clusterDatabases: 4);
        var endpoint = new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(6379));
        var name = $"selected-db-{database}-{protocol}";
        // Rows for both protocols share the cluster's databases, so each row owns its keys.
        var tag = $"{{{Guid.NewGuid():N}}}";
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Database = database, Connections = 2, ClientName = name,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            ClientSideCache = protocol == 3 ? new() : null,
            Endpoints = { endpoint },
        });
        await using var observer = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = { endpoint }, Connections = 1 });
        await observer.SetAsync(tag + "key", "db-zero");
        await client.SetAsync(tag + "key", "selected-db");
        (await client.GetStringAsync(tag + "key")).Should().Be("selected-db");
        (await observer.GetStringAsync(tag + "key")).Should().Be("db-zero");

        using (var batch = client.CreateBatch())
        {
            var first = batch.Set(tag + "batch", "selected-db");
            var second = batch.GetString(tag + "key");
            await batch.ExecuteAsync();
            first.Result.Should().BeTrue();
            second.Result.Should().Be("selected-db");
        }
        await using (var transaction = client.CreateTransaction())
        {
            var set = transaction.Set(tag + ":transaction", "selected-db");
            await transaction.CommitAsync();
            set.Result.Should().BeTrue();
        }
        (await observer.GetStringAsync(tag + "batch")).Should().BeNull();
        (await observer.GetStringAsync(tag + ":transaction")).Should().BeNull();

        await client.Lists.RightPushAsync(tag + "queue", "selected-db");
        (await client.Lists.LeftPopAsync(tag + "queue", waitFor: TimeSpan.FromSeconds(1))).Should().Be("selected-db");
        await using var subscription = await client.SubscribeAsync(tag + "channel");
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
        var container = await servers.SingleNodeClusterAsync(image);
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
        var container = await servers.SingleNodeClusterAsync("valkey/valkey:9.0-alpine");
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
}
