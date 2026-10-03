using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

public class DistributedCacheAccessorTests
{
    [Test]
    public async Task ConcreteAndInterfaceReceiversCreateIndependentAdaptersWithoutConnecting()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", 1) },
        });
        IRespireClient abstraction = client;
        await using var first = client.AsDistributedCache();
        await using var second = abstraction.AsDistributedCache();

        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task NullReceiverThrows()
    {
        IRespireClient client = null!;
        await Assert.That(() => client.AsDistributedCache()).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task OptionsComposePrefixesAndDisposalLeavesClientUsable()
    {
        await using var server = new FakeRespServer("*2\r\n:0\r\n$5\r\nhello\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient prefixed = client.WithKeyPrefix("tenant:");
        await using (var cache = prefixed.AsDistributedCache(new RespireCacheOptions
        {
            InstanceName = "cache:",
        }))
        {
            await Assert.That(Encoding.UTF8.GetString((await cache.GetAsync("key"))!)).IsEqualTo("hello");
            cache.Dispose();
        }

        await using var next = prefixed.AsDistributedCache(new RespireCacheOptions { InstanceName = "other:" });
        await Assert.That(Encoding.UTF8.GetString((await next.GetAsync("key"))!)).IsEqualTo("hello");
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Any(command => command.Contains(" 1 tenant:cache:key "))).IsTrue();
        await Assert.That(commands.Any(command => command.Contains(" 1 tenant:other:key "))).IsTrue();
    }
}
