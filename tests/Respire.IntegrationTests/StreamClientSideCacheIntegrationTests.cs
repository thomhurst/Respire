using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamClientSideCacheIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.OptOut)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task ExternalConsumerGroupChanges_ReturnFreshMetadataAndMemory(RespireClientTrackingMode trackingMode)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = RespireProtocol.Resp3,
            Connections = 1,
        };
        await using var writer = await RespireClient.ConnectAsync(options);
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = new() { TrackingMode = trackingMode, LocalExpiration = null },
        });

        var key = $"cache:stream-groups:{Guid.NewGuid():N}";
        const string group = "workers";
        var id = await writer.Streams.AddAsync(key, ("type", "first"));
        await writer.Streams.CreateGroupAsync(key, group, RespireStreamId.Beginning);
        await AssertReadsAsync(pending: 0, groupCount: 1);
        await AssertReadsAsync(pending: 0, groupCount: 1);

        using (var delivery = await writer.ExecuteAsync("XREADGROUP", "GROUP", group, "alice", "STREAMS", key, ">"))
        {
            await Assert.That(delivery.IsNull).IsFalse();
        }

        await AssertReadsAsync(pending: 1, groupCount: 1);

        await Assert.That(await writer.Streams.AcknowledgeAsync(key, group, id)).IsEqualTo(1);
        await AssertReadsAsync(pending: 0, groupCount: 1);

        await writer.Streams.CreateGroupAsync(key, "other-workers", RespireStreamId.Beginning);
        await AssertReadsAsync(pending: 0, groupCount: 2);
        await Assert.That(reader.ClientSideCache!.Count).IsEqualTo(0);

        async Task AssertReadsAsync(long pending, int groupCount)
        {
            await reader.PingAsync();
            await Assert.That((await reader.Streams.PendingSummaryAsync(key, group)).Count).IsEqualTo(pending);
            var groups = await reader.Streams.GroupInfoAsync(key);
            await Assert.That(groups.Single(info => info.Name == group).Pending).IsEqualTo(pending);
            await Assert.That((await reader.Streams.InfoAsync(key)).Groups).IsEqualTo(groupCount);

            using var full = await reader.ExecuteAsync("XINFO STREAM", key, "FULL", "COUNT", 10);
            var fullGroups = GetField(full, "groups");
            var fullGroup = fullGroups.Single(info => GetField(info, "name").AsString() == group);
            await Assert.That(fullGroups.Count).IsEqualTo(groupCount);
            await Assert.That(GetField(fullGroup, "pel-count").AsInteger()).IsEqualTo(pending);

            var memory = await writer.Server.MemoryUsageAsync(key, samples: 0);
            await Assert.That(memory).IsNotNull();
            await Assert.That(await reader.Server.MemoryUsageAsync(key, samples: 0)).IsEqualTo(memory);
        }
    }

    private static RespireResult GetField(RespireResult reply, string field)
    {
        for (var index = 0; index < reply.Count; index += 2)
        {
            if (reply[index].AsString() == field)
            {
                return reply[index + 1];
            }
        }

        throw new InvalidOperationException($"Missing {field} in XINFO STREAM FULL reply.");
    }
}
