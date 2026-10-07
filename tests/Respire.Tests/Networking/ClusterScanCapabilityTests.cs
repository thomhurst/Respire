using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterScanCapabilityTests
{
    [Test]
    [Arguments("-NOPERM metadata denied\r\n")]
    [Arguments("-ERR unknown command 'COMMAND'\r\n")]
    [Arguments("-ERR unsupported operation: COMMAND INFO\r\n")]
    [Arguments("-ERR metadata temporarily unavailable\r\n")]
    [Arguments("+malformed\r\n")]
    public async Task UnknownMetadataIsProbedOncePerRecoveryAndRecheckedNextPage(string unknown)
    {
        var metadata = System.Text.Encoding.UTF8.GetBytes(unknown);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "COMMAND INFO CLUSTERSCAN" ? metadata : FakeRespServer.PongReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var cache = new ClusterScanCapabilityCache();
        var round = new ClusterScanCapabilityCache.ProbeRound();
        await Assert.That(await cache.SupportsAsync(client, connection, "run", round, default)).IsFalse();
        metadata = "*1\r\n*1\r\n$11\r\nclusterscan\r\n"u8.ToArray();
        await Assert.That(await cache.SupportsAsync(client, connection, "run", round, default)).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "COMMAND INFO CLUSTERSCAN")).IsEqualTo(1);
        await Assert.That(await cache.SupportsAsync(client, connection, "run", new(), default)).IsTrue();
        metadata = "*1\r\n$-1\r\n"u8.ToArray();
        await Assert.That(await cache.SupportsAsync(client, connection, "run", new(), default)).IsTrue();
        await Assert.That(await cache.SupportsAsync(client, connection, "replacement-run", new(), default)).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "COMMAND INFO CLUSTERSCAN")).IsEqualTo(3);
    }

    [Test]
    [Arguments("ERR unknown command 'CLUSTERSCAN'", true)]
    [Arguments("ERR unknown command \"clusterscan\", with args beginning with: '0'", true)]
    [Arguments("ERR unknown command CLUSTERSCAN", true)]
    [Arguments("ERR unknown command 'SCAN'", false)]
    [Arguments("ERR unknown command 'CLUSTERSCAN", false)]
    [Arguments("NOPERM CLUSTERSCAN denied", false)]
    [Arguments("ERR CLUSTERSCAN failed", false)]
    public async Task UnknownCommandClassifierRequiresDefinitiveNamedAbsence(string message, bool expected)
    {
        var error = new RespireServerException(message);
        await Assert.That(ClusterScanCommandErrors.IsUnknown(error, "CLUSTERSCAN")).IsEqualTo(expected);
        await Assert.That(ClusterScanCommandErrors.IsDenied(error)).IsEqualTo(message.StartsWith("NOPERM "));
    }

    [Test]
    public async Task CapabilityParserDistinguishesAbsentFromUnknown()
    {
        using var absent = RespValue.Array(RespValue.Null);
        using var supported = RespValue.Array(RespValue.Array(RespValue.BulkString("CLUSTERSCAN")));
        using var malformed = RespValue.Array(RespValue.Integer(1));
        await Assert.That(ClusterScanCapabilityCache.ReadSupport(absent)).IsFalse();
        await Assert.That(ClusterScanCapabilityCache.ReadSupport(supported)).IsTrue();
        await Assert.That(ClusterScanCapabilityCache.ReadSupport(malformed)).IsNull();
    }

    [Test]
    public async Task MetadataFallbackDoesNotHideOperationalErrors()
    {
        await Assert.That(ClusterScanCommandErrors.IsMetadataUnavailable(
            new RespireServerException("LOADING dataset is loading"))).IsFalse();
        await Assert.That(ClusterScanCommandErrors.IsMetadataUnavailable(
            new RespireServerException("CLUSTERDOWN cluster is unavailable"))).IsFalse();
    }
}
