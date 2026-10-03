using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterStreamedUploadIntegrationTests
{
    [Test]
    [Arguments(RespProtocol.Resp2, false)]
    [Arguments(RespProtocol.Resp3, false)]
    [Arguments(RespProtocol.Resp2, true)]
    [Arguments(RespProtocol.Resp3, true)]
    public async Task UploadReplaysAfterRealClusterRedirect(RespProtocol protocol, bool asking)
    {
        await using var cluster = await RedisReadReplicaClusterTestContainer.StartAsync();
        var prefix = Guid.NewGuid().ToString("N");
        string key;
        for (var index = 0; ; index++)
        {
            key = $"{{upload-{prefix}-{index}}}:value";
            if (ClusterHash.GetSlot(key) >= 5461) continue;
            // The fixture writes a replication warmup key. Moving its slot would require
            // migrating that unrelated value too, so choose an empty source slot.
            if ((await cluster.CommandAsync(0, "CLUSTER", "COUNTKEYSINSLOT", ClusterHash.GetSlot(key).ToString())).Trim() == "0")
                break;
        }
        var slot = ClusterHash.GetSlot(key).ToString();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = protocol, ClusterTopologyRefreshInterval = null,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        // Cache the source owner before changing server topology. The upload must process
        // the real server's rejection rather than discover the target before its first send.
        (await client.Strings.GetBytesAsync(key)).Should().BeNull();
        var sourceId = (await cluster.CommandAsync(0, "CLUSTER", "MYID")).Trim();
        var targetId = (await cluster.CommandAsync(1, "CLUSTER", "MYID")).Trim();
        await cluster.CommandAsync(1, "CLUSTER", "SETSLOT", slot, "IMPORTING", sourceId);
        await cluster.CommandAsync(0, "CLUSTER", "SETSLOT", slot, "MIGRATING", targetId);
        if (!asking) await CompleteMigrationAsync();

        await using var directSource = RespireClient.Create(new RespireOptions
        {
            Protocol = protocol, Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        Func<Task> rejectedRead = async () => await directSource.Strings.GetBytesAsync(key);
        var rejection = await rejectedRead.Should().ThrowAsync<RespireServerException>();
        rejection.Which.Code.Should().Be(asking ? "ASK" : "MOVED");

        var payload = Enumerable.Range(0, 131_173).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new ReplayCountingStream([.. new byte[11], .. payload]);
        source.Position = 11;
        (await client.Strings.SetAsync(key, source, payload.Length)).Should().BeTrue();
        source.Rewinds.Should().Be(1);
        source.Position.Should().Be(source.Length);
        if (asking) await CompleteMigrationAsync();
        (await client.Strings.GetBytesAsync(key)).Should().Equal(payload);

        async Task CompleteMigrationAsync()
        {
            for (var node = 0; node < 3; node++)
                await cluster.CommandAsync(node, "CLUSTER", "SETSLOT", slot, "NODE", targetId);
        }
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
