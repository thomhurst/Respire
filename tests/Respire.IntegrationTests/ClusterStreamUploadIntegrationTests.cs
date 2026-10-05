using System.Buffers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// Each row moves its own reserved slot on the shared cluster; slot moves are serialized cluster-wide.
[ClassDataSource<SharedRedisClusterFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(SharedRedisClusterFixture.ReshardingKey)]
public class ClusterStreamUploadIntegrationTests(SharedRedisClusterFixture fixture)
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
        var cluster = fixture.Cluster;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = protocol, Connections = 1,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        var key = $"upload:{{{fixture.ReserveSlot(node: 0)}}}";
        var slot = ClusterHash.GetSlot(key);
        client.Core.Cluster!.GetSlotOwnerEndpoint(slot)!.Value.Port.Should().Be(cluster.Port(0));
        await cluster.MoveKeysAsync(slot, source: 0, target: 1);
        if (!asking) await cluster.FinishMoveAsync(slot, target: 1);

        await using var directSource = RespireClient.Create(new RespireOptions
        {
            Protocol = protocol, Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        Func<Task> rejectedRead = async () => await directSource.Strings.GetBytesAsync(key);
        var rejection = await rejectedRead.Should().ThrowAsync<RespireServerException>();
        rejection.Which.Code.Should().Be(asking ? "ASK" : "MOVED");

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
            await using var source = new ReplayCountingStream([255, .. payload, 254]) { Position = 1 };
            (await client.Strings.SetAsync(key, source, payload.Length,
                cancellationToken: deadline.Token)).Should().BeTrue();
            source.Position.Should().Be(payload.Length + 1);
            source.Rewinds.Should().Be(1);
        }

        using var stored = await client.ExecuteAsync(RespireCommands.String.GET, [key], cancellationToken: deadline.Token);
        stored.AsBytes().Should().Equal(payload);
        // ASK is temporary; MOVED publishes the new slot owner.
        client.Core.Cluster.GetSlotOwnerEndpoint(slot)!.Value.Port.Should().Be(cluster.Port(asking ? 0 : 1));
    }

    private sealed class ReplayCountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Rewinds { get; private set; }
        public override long Position
        {
            get => base.Position;
            set
            {
                if (value < base.Position) Rewinds++;
                base.Position = value;
            }
        }
    }
}
