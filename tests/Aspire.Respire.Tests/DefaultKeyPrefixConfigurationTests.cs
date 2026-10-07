using System.Text;
using Aspire.Respire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Aspire;

public class DefaultKeyPrefixConfigurationTests
{
    /// <summary>Verifies configuration precedence, explicit clearing and callback-owned binary snapshots for both registrations.</summary>
    [Test]
    [Arguments(false, "absent")]
    [Arguments(true, "absent")]
    [Arguments(false, "global")]
    [Arguments(true, "global")]
    [Arguments(false, "named")]
    [Arguments(true, "named")]
    [Arguments(false, "empty-global")]
    [Arguments(true, "empty-global")]
    [Arguments(false, "empty-named")]
    [Arguments(true, "empty-named")]
    [Arguments(false, "callback")]
    [Arguments(true, "callback")]
    [Arguments(false, "binary-callback")]
    [Arguments(true, "binary-callback")]
    public async Task ConfigurationLayersPreserveReplaceOrClearRootPrefix(bool keyed, string mode)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var values = new Dictionary<string, string?> { ["ConnectionStrings:cache"] = "localhost,keyPrefix=parsed%3A" };
        if (mode != "absent")
            values["Aspire:Respire:Options:KeyPrefix"] = mode == "empty-global" ? "" : "global:%";
        if (mode is "named" or "empty-named" or "callback" or "binary-callback")
            values["Aspire:Respire:cache:Options:KeyPrefix"] = mode == "empty-named" ? "" : "named:";
        builder.Configuration.AddInMemoryCollection(values);
        var expectedConfigured = mode switch
        {
            "absent" => "parsed:",
            "empty-global" or "empty-named" => "",
            "global" => "global:%",
            _ => "named:",
        };
        byte[] binary = [255, 0];
        RespireOptions? observed = null;
        RespireOptions Configure(IServiceProvider _, RespireOptions options)
        {
            observed = options;
            return mode switch
            {
                "callback" => options with { KeyPrefix = "callback:" },
                "binary-callback" => options with { KeyPrefix = binary },
                _ => options,
            };
        }
        if (keyed) builder.AddKeyedRespireClient("cache", configureOptions: Configure);
        else builder.AddRespireClient("cache", configureOptions: Configure);
        using var host = builder.Build();
        var client = keyed ? host.Services.GetRequiredKeyedService<IRespireClient>("cache")
            : host.Services.GetRequiredService<IRespireClient>();
        binary[0] = 254;
        await Assert.That(observed!.KeyPrefix).IsEqualTo((RespireKey)expectedConfigured);
        var effective = mode == "callback" ? "callback:" : expectedConfigured;
        byte[] expected = mode == "binary-callback" ? [255, 0, .. "key"u8] : Encoding.UTF8.GetBytes(effective + "key");
        byte[] expectedNested = mode == "binary-callback" ? [255, 0, .. "nested:key"u8]
            : Encoding.UTF8.GetBytes(effective + "nested:key");
        await Assert.That(client.ResolveKey("key")).IsEqualTo((RespireKey)expected);
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(client.WithKeyPrefix("nested:").ResolveKey("key"))
            .IsEqualTo((RespireKey)expectedNested);
    }
}
