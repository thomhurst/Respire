using Redis.Search;
using Respire.Extensions.Json;
using Respire.Extensions.Probabilistic;
using Respire.Extensions.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ModuleClientExtensionsTests
{
    [Test]
    public async Task ConcreteAndInterfaceReceiversShareWrappersWithoutConnecting()
    {
        await using var client = CreateClient();
        IRespireClient abstraction = client;

        await Assert.That(client.Json).IsSameReferenceAs(abstraction.Json);
        await Assert.That(client.Search).IsSameReferenceAs(abstraction.Search);
        await Assert.That(client.TimeSeries).IsSameReferenceAs(abstraction.TimeSeries);
        await Assert.That(client.Probabilistic).IsSameReferenceAs(abstraction.Probabilistic);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    [Arguments("Json")]
    [Arguments("Search")]
    [Arguments("TimeSeries")]
    [Arguments("Probabilistic")]
    public async Task WrappersBelongToEachClientAndPrefixedView(string module)
    {
        await using var client = CreateClient();
        await using var other = CreateClient();
        var prefixed = client.WithKeyPrefix("tenant:");
        var wrapper = GetModule(client, module);
        var prefixedWrapper = GetModule(prefixed, module);

        await Assert.That(GetModule(client, module)).IsSameReferenceAs(wrapper);
        await Assert.That(GetModule(prefixed, module)).IsSameReferenceAs(prefixedWrapper);
        await Assert.That(ReferenceEquals(wrapper, prefixedWrapper)).IsFalse();
        await Assert.That(ReferenceEquals(wrapper, GetModule(other, module))).IsFalse();
    }

    [Test]
    [Arguments("Json")]
    [Arguments("Search")]
    [Arguments("TimeSeries")]
    [Arguments("Probabilistic")]
    public async Task ConcurrentFirstAccessReturnsSameWrapper(string module)
    {
        await using var client = CreateClient();
        var wrappers = new object[32];
        Parallel.For(0, wrappers.Length, i => wrappers[i] = GetModule(client, module));

        foreach (var wrapper in wrappers)
            await Assert.That(wrapper).IsSameReferenceAs(wrappers[0]);
    }

    [Test]
    [Arguments("Json")]
    [Arguments("Search")]
    [Arguments("TimeSeries")]
    [Arguments("Probabilistic")]
    public async Task NullReceiverThrowsArgumentNullException(string module)
    {
        await Assert.That(() => GetModule(null!, module)).Throws<ArgumentNullException>();
    }

    private static object GetModule(IRespireClient client, string module) => module switch
    {
        "Json" => client.Json,
        "Search" => client.Search,
        "TimeSeries" => client.TimeSeries,
        "Probabilistic" => client.Probabilistic,
        _ => throw new ArgumentOutOfRangeException(nameof(module)),
    };

    private static RespireClient CreateClient() => RespireClient.Create(new RespireOptions
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
    });
}
