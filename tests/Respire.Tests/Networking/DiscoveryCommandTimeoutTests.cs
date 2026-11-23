using System.Globalization;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DiscoveryCommandTimeoutTests
{
    [Test]
    public async Task SentinelPrimaryDiscoveryAcceptsFiftyDayCommandTimeout()
    {
        await using var primary = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : null,
        };
        var port = primary.Port.ToString(CultureInfo.InvariantCulture);
        var address = Encoding.ASCII.GetBytes("*2\r\n$9\r\n127.0.0.1\r\n$" + $"{port.Length}\r\n{port}\r\n");
        await using var sentinel = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME primary" => address,
                "SENTINEL MASTER primary" or "SENTINEL SENTINELS primary" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));

#pragma warning disable RESP010 // Test-owned client verifies discovery and ROLE against scripted loopback servers.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            Protocol = RespireProtocol.Resp2,
            SentinelPrimaryName = "primary",
            CommandTimeout = TimeSpan.FromDays(50),
        }, caller.Token);
#pragma warning restore RESP010

        await Assert.That(sentinel.ReceivedCommands).Contains("SENTINEL GET-MASTER-ADDR-BY-NAME primary");
        await Assert.That(primary.ReceivedCommands).Contains("ROLE");
    }

    [Test]
    public async Task SentinelReplicaDiscoveryAcceptsMaximumCommandTimeout()
    {
        await using var sentinel = new FakeRespServer("*1\r\n*4\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n$4\r\n6380\r\n"u8.ToArray());
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "primary",
            CommandTimeout = TimeSpan.MaxValue,
        };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var replicas = await SentinelResolver.DiscoverReplicaEndpointsAsync(options, options.Endpoints, caller.Token);

        await Assert.That(replicas).IsEquivalentTo([new RespireEndpoint("127.0.0.1", 6380)]);
        await Assert.That(sentinel.ReceivedCommands).Contains("SENTINEL REPLICAS primary");
    }

    [Test]
    public async Task ClusterSeedDiscoveryAcceptsMaximumCommandTimeout()
    {
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));

#pragma warning disable RESP010 // Test-owned client verifies seed discovery against a scripted loopback server.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", seed.Port)],
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            CommandTimeout = TimeSpan.MaxValue,
        }, caller.Token);
#pragma warning restore RESP010

        await Assert.That(seed.ReceivedCommands).Contains("CLUSTER SLOTS");
        await Assert.That(client.Core.Cluster!.GetKnownSlotOwner(0)).IsNotNull();
    }
}
