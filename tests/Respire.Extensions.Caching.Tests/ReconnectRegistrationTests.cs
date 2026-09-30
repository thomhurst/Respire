using Microsoft.Extensions.DependencyInjection;
using Respire.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

public class ReconnectRegistrationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Builder_PreservesReconnectPolicy(bool keyed, bool configured)
    {
        var services = new ServiceCollection();
        var policy = configured ? new RespireReconnectPolicy { MaxAttempts = 3 } : null;
        void Configure(RespireOptionsBuilder options)
        {
            options.Endpoints.Add(new RespireEndpoint("127.0.0.1", 1));
            if (configured) options.ReconnectPolicy = policy;
        }
        if (keyed) services.AddKeyedRespire("recovery", Configure);
        else services.AddRespire(Configure);
        await using var provider = services.BuildServiceProvider();
        var client = keyed ? provider.GetRequiredKeyedService<RespireClient>("recovery")
            : provider.GetRequiredService<RespireClient>();

        // Lazy construction never connects; the immutable policy reaches the client unchanged.
        if (configured) await Assert.That(client.Core.Options.ReconnectPolicy).IsSameReferenceAs(policy);
        else await Assert.That(client.Core.Options.ReconnectPolicy).IsNull();
    }
}
