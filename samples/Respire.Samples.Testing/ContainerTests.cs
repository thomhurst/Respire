using Respire.Testing.Containers;
using TUnit.Core;

namespace Respire.Samples.Testing;

public class ContainerTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis, RespireProtocol.Resp2)]
    [Arguments(RespireContainerServer.Redis, RespireProtocol.Resp3)]
    [Arguments(RespireContainerServer.Valkey, RespireProtocol.Resp2)]
    [Arguments(RespireContainerServer.Valkey, RespireProtocol.Resp3)]
    public async Task SharedClientScenarios(RespireContainerServer server, RespireProtocol protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Server = server,
            Image = server == RespireContainerServer.Redis ? "redis:7.2-alpine" : "valkey/valkey:8.1-alpine",
            StartupTimeout = TimeSpan.FromMinutes(2),
        });
        // RunAsync disposes both clients before the owning fixture is disposed.
        await SharedScenarios.RunAsync(fixture.CreateOptions() with { Protocol = protocol });
    }
}
