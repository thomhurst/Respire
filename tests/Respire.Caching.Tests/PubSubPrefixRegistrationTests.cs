using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class PubSubPrefixRegistrationTests
{
    [Test]
    [MatrixDataSource]
    public async Task BuilderOwnsBinaryPrefixForKeyedAndUnkeyedClients(
        [Matrix(false, true)] bool keyed, [Matrix(false, true)] bool binary)
    {
        var services = new ServiceCollection();
        byte[] prefix = [255, 0, (byte)':'];
        void Configure(RespireOptionsBuilder options)
        {
            options.Endpoints.Add(new("localhost", 6379));
            options.PubSubPrefix = binary ? (RespireKey)prefix : "events:";
        }
        if (keyed) services.AddKeyedRespire("prefix", Configure);
        else services.AddRespire(Configure);
        await using var provider = services.BuildServiceProvider();
        var client = keyed ? provider.GetRequiredKeyedService<RespireClient>("prefix")
            : provider.GetRequiredService<RespireClient>();
        prefix[0] = 1;
        byte[] expected = binary ? [255, 0, (byte)':', .. "nested:item"u8] : "events:nested:item"u8.ToArray();
        await Assert.That(client.WithPubSubPrefix("nested:").WithKeyPrefix("keys:")
            .ResolveChannel("item").Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(client.ResolveKey("item")).IsEqualTo((RespireKey)"item");
        await Assert.That(client.IsConnected).IsFalse();
    }
}
