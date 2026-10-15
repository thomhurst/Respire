using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GeneratedHashModelIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task RealRedisRoundTrips(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(fixture.Host, fixture.Port) },
            Protocol = protocol,
            Connections = 1,
        });
        await GeneratedHashModelScenarios.RoundTripAsync(client);
    }
}
