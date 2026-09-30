using Microsoft.Extensions.DependencyInjection;
using Respire.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

public class ProtocolRegistrationTests
{
    [Test]
    [Arguments(false, null)]
    [Arguments(true, null)]
    [Arguments(false, RespProtocol.Auto)]
    [Arguments(true, RespProtocol.Auto)]
    [Arguments(false, RespProtocol.Resp2)]
    [Arguments(true, RespProtocol.Resp2)]
    [Arguments(false, RespProtocol.Resp3)]
    [Arguments(true, RespProtocol.Resp3)]
    public async Task BuilderProtocol_ReachesResolvedClient(bool keyed, RespProtocol? protocol)
    {
        var services = new ServiceCollection();
        void Configure(RespireOptionsBuilder options)
        {
            options.Endpoints.Add(new RespireEndpoint("127.0.0.1", 1));
            if (protocol.HasValue) options.Protocol = protocol.Value;
        }
        if (keyed) services.AddKeyedRespire("protocol", Configure);
        else services.AddRespire(Configure);
        await using var provider = services.BuildServiceProvider();
        var client = keyed ? provider.GetRequiredKeyedService<RespireClient>("protocol")
            : provider.GetRequiredService<RespireClient>();

        // Lazy construction does not connect; defaults and explicit modes survive registration.
        await Assert.That(client.Core.Options.Protocol).IsEqualTo(protocol ?? new RespireOptions().Protocol);
    }
}
