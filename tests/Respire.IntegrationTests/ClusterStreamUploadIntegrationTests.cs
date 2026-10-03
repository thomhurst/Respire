using System.Buffers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterStreamUploadIntegrationTests
{
    [Test]
    [Arguments(RespProtocol.Resp2, false, false)]
    [Arguments(RespProtocol.Resp2, false, true)]
    [Arguments(RespProtocol.Resp2, true, false)]
    [Arguments(RespProtocol.Resp2, true, true)]
    [Arguments(RespProtocol.Resp3, false, false)]
    [Arguments(RespProtocol.Resp3, false, true)]
    [Arguments(RespProtocol.Resp3, true, false)]
    [Arguments(RespProtocol.Resp3, true, true)]
    public async Task RedirectReplaysTheEntireUpload(RespProtocol protocol, bool asking, bool sequence)
    {
        await using var cluster = await RedisClusterTestContainer.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = protocol, Connections = 1,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        var key = Enumerable.Range(0, 100).Select(index => $"upload:{index}")
            .First(candidate => ClusterHash.GetSlot(candidate) < 5461);
        var slot = ClusterHash.GetSlot(key);
        client.Core.Cluster!.GetSlotOwnerEndpoint(slot)!.Value.Port.Should().Be(cluster.Port(0));
        await cluster.MoveKeysAsync(slot, source: 0, target: 1);
        if (!asking) await cluster.FinishMoveAsync(slot, target: 1);

        // Larger than several upload chunks, including binary bytes and a nonzero stream origin.
        var payload = Enumerable.Range(0, 96 * 1024 + 7).Select(index => (byte)(index % 251)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (sequence)
        {
            (await client.Strings.SetAsync(key, new ReadOnlySequence<byte>(payload),
                cancellationToken: deadline.Token)).Should().BeTrue();
        }
        else
        {
            await using var source = new MemoryStream([255, .. payload, 254]) { Position = 1 };
            (await client.Strings.SetAsync(key, source, payload.Length,
                cancellationToken: deadline.Token)).Should().BeTrue();
            source.Position.Should().Be(payload.Length + 1);
        }

        using var stored = await client.ExecuteAsync(RespireCommands.String.GET, [key], cancellationToken: deadline.Token);
        stored.AsBytes().Should().Equal(payload);
        // ASK is temporary; MOVED publishes the new slot owner.
        client.Core.Cluster.GetSlotOwnerEndpoint(slot)!.Value.Port.Should().Be(cluster.Port(asking ? 0 : 1));
    }
}
