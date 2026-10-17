using System.Reflection;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class LibraryInfoHandshakeIntegrationTests(VersionedServerFixture servers)
{
    [Test]
    [Arguments("redis:7.2-alpine", 2)]
    [Arguments("redis:7.2-alpine", 3)]
    [Arguments("redis:8.10-alpine", 2)]
    [Arguments("redis:8.10-alpine", 3)]
    [Arguments("valkey/valkey:9.0-alpine", 2)]
    [Arguments("valkey/valkey:9.0-alpine", 3)]
    public async Task ClientListReportsPackageIdentity(string image, int protocol)
    {
        var server = await servers.LeaseAsync(image);
        var name = $"library-info-{Guid.NewGuid():N}";
        var options = RespireOptions.Parse(server.ConnectionString(protocol)) with { ClientName = name, Connections = 2 };
        await using var client = await RespireClient.ConnectAsync(options);
        var expectedVersion = typeof(RespireClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var rows = (await client.Server.ClientsAsync()).Where(row => row.Name == name).ToArray();
        rows.Should().HaveCount(2);
        foreach (var row in rows)
        {
            row.Attributes["lib-name"].Should().Be("Respire");
            row.Attributes["lib-ver"].Should().Be(expectedVersion);
        }

        await using var wrapper = await RespireClient.ConnectAsync(options with { Connections = 1, LibraryName = "Respire(wrapper)" });
        var connection = await wrapper.Server.GetClientConnectionAsync();
        (await connection.InfoAsync()).Attributes["lib-name"].Should().Be("Respire(wrapper)");

        await using var disabled = await RespireClient.ConnectAsync(options with { Connections = 1, LibraryName = null });
        var disabledConnection = await disabled.Server.GetClientConnectionAsync();
        var info = await disabledConnection.InfoAsync();
        info.Attributes["lib-name"].Should().BeEmpty();
        info.Attributes["lib-ver"].Should().BeEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Redis62StillConnects(int protocol)
    {
        var server = await servers.LeaseAsync("redis:6.2-alpine");
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        await client.SetAsync("library-info", "usable");
        (await client.GetStringAsync("library-info")).Should().Be("usable");
    }
}
