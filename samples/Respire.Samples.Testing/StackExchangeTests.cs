using Respire.Testing.Containers;
using Respire.Testing.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Samples.Testing;

// The same application assertions run unchanged: only connection setup differs.
internal static class StackExchangeScenarios
{
    internal static async Task RunAsync(IConnectionMultiplexer connection)
    {
        IDatabase database = connection.GetDatabase();
        await Assert.That(await database.HashSetAsync("session", "status", "active")).IsTrue();
        await Assert.That((string?)await database.HashGetAsync("session", "status")).IsEqualTo("active");
        await Assert.That(await database.StringIncrementAsync("visits")).IsEqualTo(1L);
        await Assert.That((string?)await database.StringGetAsync("visits")).IsEqualTo("1");
        await database.HashSetAsync("user", [new HashEntry("name", "Ada")]);
        await Assert.That((string?)database.HashGet("user", "name")).IsEqualTo("Ada");
        await database.ListRightPushAsync("jobs", "send-email");
        await Assert.That((string?)await database.ListGetByIndexAsync("jobs", 0)).IsEqualTo("send-email");
        await Assert.That(await database.KeyDeleteAsync("session")).IsTrue();
        await Assert.That((await database.HashGetAsync("session", "status")).IsNull).IsTrue();
    }
}

public class StackExchangeFakeTests
{
    [Test]
    public async Task SupportedDatabaseSuite()
    {
        await using var fixture = new RespireFakeConnection();
        await StackExchangeScenarios.RunAsync(fixture.Connection);
    }
}

public class StackExchangeContainerTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task SupportedDatabaseSuite(RespireContainerServer server)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Image = server == RespireContainerServer.Redis ? "redis:7.2-alpine" : "valkey/valkey:8.1-alpine",
            StartupTimeout = TimeSpan.FromMinutes(2),
        });
        var endpoint = fixture.DataEndpoints[0];
        using var connection = await ConnectionMultiplexer.ConnectAsync($"{endpoint.Host}:{endpoint.Port}");
        await StackExchangeScenarios.RunAsync(connection);
    }
}
