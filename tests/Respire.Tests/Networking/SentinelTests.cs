using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelTests
{
    [Test]
    public async Task ConnectionString_ParsesSentinelOptions()
    {
        var options = RespireOptions.Parse(
            "redis://sentinel.example?serviceName=mymaster&sentinelUser=sentinel&sentinelPassword=secret");

        await Assert.That(options.PrimaryEndpoint).IsEqualTo(new RespireEndpoint("sentinel.example", 26379));
        await Assert.That(options.ServiceName).IsEqualTo("mymaster");
        await Assert.That(options.SentinelUsername).IsEqualTo("sentinel");
        await Assert.That(options.SentinelPassword).IsEqualTo("secret");
    }

    [Test]
    public async Task ConnectionString_RejectsEmptyServiceName()
    {
        var error = Assert.Throws<ArgumentException>(
            () => RespireOptions.Parse("redis://sentinel.example?serviceName=%20%20"));

        await Assert.That(error.Message).Contains("serviceName");
    }

    [Test]
    public async Task ConnectAsync_DiscoversPrimaryFromSentinel()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            ServiceName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_UsesSentinelCredentialsForDiscovery()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(
            FakeRespServer.OkReply,
            PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", sentinel.Port) },
            ServiceName = "mymaster",
            Username = "redis-user",
            Password = "redis-secret",
            SentinelUsername = "sentinel-user",
            SentinelPassword = "sentinel-secret",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH sentinel-user sentinel-secret",
            "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-user redis-secret",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConnectAsync_EmptySentinelPasswordDisablesInheritedAuthentication()
    {
        await using var primary = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(
            $"redis://:redis-secret@127.0.0.1:{sentinel.Port}?serviceName=mymaster&sentinelPassword=");

        _ = await client.PingAsync();

        await Assert.That(sentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(
        [
            "AUTH redis-secret",
            "PING",
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConnectAsync_FallsBackWhenSentinelReturnsInvalidPort()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var invalidSentinel = new FakeRespServer(PrimaryReply(65536));
        await using var validSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", invalidSentinel.Port),
                new RespireEndpoint("127.0.0.1", validSentinel.Port),
            },
            ServiceName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(invalidSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(validSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_TimesOutUnresponsiveSentinelAndFallsBack()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var unresponsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));
        unresponsiveSentinel.DelayReply(0, 2_000);
        await using var responsiveSentinel = new FakeRespServer(PrimaryReply(primary.Port));

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", unresponsiveSentinel.Port),
                new RespireEndpoint("127.0.0.1", responsiveSentinel.Port),
            },
            ServiceName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
            CommandTimeout = TimeSpan.FromMilliseconds(100),
        });

        _ = await client.PingAsync();

        await Assert.That(unresponsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(responsiveSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task ConnectAsync_FallsBackWhenSentinelReportsUnreachablePrimary()
    {
        await using var stalePrimary = new FakeRespServer(FakeRespServer.PongReply);
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var staleSentinel = new FakeRespServer(PrimaryReply(stalePrimary.Port));
        await using var currentSentinel = new FakeRespServer(PrimaryReply(primary.Port));
        await stalePrimary.DisposeAsync();

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints =
            {
                new RespireEndpoint("127.0.0.1", staleSentinel.Port),
                new RespireEndpoint("127.0.0.1", currentSentinel.Port),
            },
            ServiceName = "mymaster",
            ConnectTimeout = TimeSpan.FromSeconds(1),
        });

        _ = await client.PingAsync();

        await Assert.That(staleSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(currentSentinel.ReceivedCommands).IsEquivalentTo(
            ["SENTINEL GET-MASTER-ADDR-BY-NAME mymaster"]);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task Create_RejectsSentinelBecauseDiscoveryIsNetworked()
    {
        var error = Assert.Throws<NotSupportedException>(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", 26379) },
            ServiceName = "mymaster",
        }));

        await Assert.That(error.Message).Contains("ConnectAsync");
    }

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");
}
