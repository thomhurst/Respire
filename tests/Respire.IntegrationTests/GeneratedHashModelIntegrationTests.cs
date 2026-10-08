using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GeneratedHashModelIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task RealRedisRoundTrips(RespProtocol protocol)
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
