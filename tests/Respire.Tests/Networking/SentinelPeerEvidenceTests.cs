using Respire.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelPeerEvidenceTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task NonAcceptingSlotIsOmittedAndPreventsConfirmation(int stoppedSlot)
    {
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        await using var node = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 2);
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        var ready = node.CaptureSentinelPeers();
        await Assert.That(ready.Peers.Length).IsEqualTo(2);
        await Assert.That(ready.ConfirmedPeer).IsEqualTo(endpoint);
        await Assert.That(node.GetConfirmedCurrentPeer()).IsEqualTo(endpoint);

        var stopped = node.GetConnection(stoppedSlot);
        stopped.StopAcceptingCommands();
        await Assert.That(stopped.IsConnected).IsTrue();
        await Assert.That(stopped.IsAcceptingCommands).IsFalse();
        var partial = node.CaptureSentinelPeers();
        await Assert.That(partial.Peers.Length).IsEqualTo(1);
        await Assert.That(partial.Peers[0]).IsEqualTo(endpoint);
        await Assert.That(partial.ConfirmedPeer).IsNull();
        await Assert.That(node.GetConfirmedCurrentPeer()).IsNull();
        await Assert.That(node.AllCurrentPeersMatch(endpoint.Host, endpoint.Port)).IsFalse();

        await node.RetireAsync();
        var retired = node.CaptureSentinelPeers();
        await Assert.That(retired.Peers.Length).IsEqualTo(0);
        await Assert.That(retired.ConfirmedPeer).IsNull();
    }
}
