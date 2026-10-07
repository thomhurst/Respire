using System.Text;
using Aspire.Respire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Aspire;

public class PubSubPrefixConfigurationTests
{
    [Test]
    [MatrixDataSource]
    public async Task ConfigurationLayersReplaceOrClearSeparateNamespaces(
        [Matrix(false, true)] bool keyed,
        [Matrix("absent", "global", "named", "empty-global", "empty-named", "callback", "binary-callback")] string mode)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:cache"] = "localhost,keyPrefix=keys%3A,pubSubPrefix=parsed%3A",
        };
        if (mode != "absent") values["Aspire:Respire:Options:PubSubPrefix"] = mode == "empty-global" ? "" : "global:%";
        if (mode is "named" or "empty-named" or "callback" or "binary-callback")
            values["Aspire:Respire:cache:Options:PubSubPrefix"] = mode == "empty-named" ? "" : "named:";
        builder.Configuration.AddInMemoryCollection(values);
        var configured = mode switch { "absent" => "parsed:", "global" => "global:%", "empty-global" or "empty-named" => "", _ => "named:" };
        byte[] binary = [255, 0];
        RespireOptions? observed = null;
        RespireOptions Configure(IServiceProvider _, RespireOptions options)
        {
            observed = options;
            return mode switch
            {
                "callback" => options with { PubSubPrefix = "callback:" },
                "binary-callback" => options with { PubSubPrefix = binary },
                _ => options,
            };
        }
        if (keyed) builder.AddKeyedRespireClient("cache", configureOptions: Configure);
        else builder.AddRespireClient("cache", configureOptions: Configure);
        using var host = builder.Build();
        var client = keyed ? host.Services.GetRequiredKeyedService<IRespireClient>("cache")
            : host.Services.GetRequiredService<IRespireClient>();
        binary[0] = 1;
        await Assert.That(observed!.PubSubPrefix).IsEqualTo((RespireKey)configured);
        var effective = mode == "callback" ? "callback:" : configured;
        byte[] expected = mode == "binary-callback" ? [255, 0, .. "nested:item"u8] : Encoding.UTF8.GetBytes(effective + "nested:item");
        await Assert.That(client.WithPubSubPrefix("nested:").WithKeyPrefix("nested:").ResolveChannel("item").Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(client.ResolveKey("item")).IsEqualTo((RespireKey)"keys:item");
        await Assert.That(client.IsConnected).IsFalse();
    }
}
