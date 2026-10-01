using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterScanIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SerializedCursorReturnsEveryPersistentKeyWhileSlotMovesToCompletedPrimary(int protocol)
    {
        await using var cluster = await RedisClusterTestContainer.StartAsync();
        var options = new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1,
            Endpoints = { new RespireEndpoint(cluster.Host, cluster.Port(0)) },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var tag = TagForSlot(6000);
        var prefix = Guid.NewGuid().ToString("N");
        var expected = Enumerable.Range(0, 256).Select(index => $"{prefix}:{{{tag}}}:{index}").ToHashSet();
        expected.Add($"{prefix}:{{{TagForSlot(1)}}}:stable-first");
        expected.Add($"{prefix}:{{{TagForSlot(12000)}}}:stable-third");
        foreach (var key in expected) await client.SetAsync(key, "present throughout scan");
        var seen = new HashSet<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var firstCheckpoint = RespireClusterScanCursor.Start;
        RespireClusterScanPage page;
        do
        {
            page = await client.Keys.ScanClusterPageAsync(firstCheckpoint, countHint: 1, cancellationToken: deadline.Token);
            seen.UnionWith(page.Keys);
            firstCheckpoint = page.Cursor;
        }
        while (page.Cursor.CompletedSlotCount == 0);
        page.Cursor.CompletedSlotCount.Should().Be(5461);
        page = await client.Keys.ScanClusterPageAsync(page.Cursor, countHint: 1);
        seen.UnionWith(page.Keys);
        page.Cursor.IsComplete.Should().BeFalse();
        seen.Count.Should().BeLessThan(expected.Count);

        // The destination has already completed its scan. Move keys before publishing the
        // new owner, exercising the importing/migrating window as well as final ownership.
        await cluster.MoveKeysAsync(6000, source: 1, target: 0);
        page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Parse(page.Cursor.ToString()), countHint: 1);
        seen.UnionWith(page.Keys);
        page.Cursor.IsComplete.Should().BeFalse();
        await cluster.FinishMoveAsync(6000, target: 0);

        // A new client has no process-local cursor registry or previous connection state.
        await using var resumed = await RespireClient.ConnectAsync(options);
        var checkpoint = RespireClusterScanCursor.Parse(page.Cursor.ToString());
        var pages = 0;
        do
        {
            page = await resumed.Keys.ScanClusterPageAsync(checkpoint, countHint: 13, cancellationToken: deadline.Token);
            seen.UnionWith(page.Keys);
            checkpoint = RespireClusterScanCursor.Parse(page.Cursor.ToString());
            pages++;
        }
        while (!checkpoint.IsComplete);
        pages.Should().BeGreaterThan(1);
        seen.Should().BeEquivalentTo(expected);
        foreach (var key in expected) (await resumed.GetStringAsync(key)).Should().Be("present throughout scan");
    }

    private static string TagForSlot(int slot)
    {
        for (var index = 0; ; index++)
        {
            var tag = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ClusterHash.GetSlot(tag) == slot) return tag;
        }
    }
}
