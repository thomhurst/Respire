using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public class BroadcastClientCacheIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExternalInvalidationAndReconnectPreserveBroadcastTracking(bool usePrefix)
    {
        var prefix = $"broadcast:{Guid.NewGuid():N}:";
        var key = prefix + "key";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { AllowAdmin = true, Connections = 1 };
        await using var writer = await RespireClient.ConnectAsync(options);
        await writer.SetAsync(key, "old");
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = Broadcast(usePrefix ? [prefix] : []),
        });
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("old");
        var hits = reader.ClientSideCache!.GetStatistics().Hits;
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("old");
        await Assert.That(reader.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits + 1);
        await writer.SetAsync(key, "new");
        await UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("new");

        using var identity = await reader.ExecuteAsync("CLIENT", "ID");
        var flushes = reader.ClientSideCache.GetStatistics().ContinuityFlushes;
        using var killed = await writer.ExecuteAsync("CLIENT", "KILL", "ID", identity.AsInteger());
        await UntilAsync(() => reader.ClientSideCache.GetStatistics().ContinuityFlushes > flushes && reader.IsConnected);
        await writer.SetAsync(key, "after reconnect");
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("after reconnect");
        await writer.SetAsync(key, "tracking restored");
        await UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("tracking restored");
    }

    [Test]
    public async Task LiteralBinaryPrefixCachesOnlyCoveredKeys()
    {
        byte[] prefix = [255, 0, (byte)'*', .. Guid.NewGuid().ToByteArray()];
        byte[] covered = [.. prefix, 0, 128];
        byte[] uncovered = [254, .. prefix, 0, 128];
        var options = RespireOptions.Parse(fixture.ConnectionString);
        await using var writer = await RespireClient.ConnectAsync(options);
        await writer.SetAsync(covered, "old");
        await writer.SetAsync(uncovered, "outside");
        await using var reader = await RespireClient.ConnectAsync(options with { ClientSideCache = Broadcast([prefix]) });
        await reader.GetStringAsync(covered);
        await reader.GetStringAsync(uncovered);
        await Assert.That(reader.ClientSideCache!.Count).IsEqualTo(1);
        await writer.SetAsync(uncovered, "outside changed");
        await Assert.That(await reader.GetStringAsync(uncovered)).IsEqualTo("outside changed");
        await writer.SetAsync(covered, "new");
        await UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(covered)).IsEqualTo("new");
    }

    [Test]
    public async Task HashAndMultiKeyDependenciesInvalidateInSelectedDatabase()
    {
        var prefix = $"broadcast:{Guid.NewGuid():N}:";
        var options = RespireOptions.Parse(fixture.ConnectionString);
        await using var writer = await RespireClient.ConnectAsync(options);
        await using var reader = await RespireClient.ConnectAsync(options with { ClientSideCache = Broadcast([prefix]) });
        var hash = prefix + "hash";
        await writer.Hashes.SetAsync(hash, "field", "old");
        await Assert.That(await reader.Hashes.GetStringAsync(hash, "field")).IsEqualTo("old");
        await writer.Hashes.SetAsync(hash, "field", "new");
        await UntilAsync(() => reader.ClientSideCache!.Count == 0);
        await Assert.That(await reader.Hashes.GetStringAsync(hash, "field")).IsEqualTo("new");
        reader.ClientSideCache!.Clear();
        await writer.Sets.AddAsync(prefix + "a", "one", "two");
        await writer.Sets.AddAsync(prefix + "b", "one");
        await Assert.That(await reader.Sets.IntersectAsync(prefix + "a", prefix + "b")).IsEquivalentTo(["one"]);
        await writer.Sets.AddAsync(prefix + "b", "two");
        await UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.Sets.IntersectAsync(prefix + "a", prefix + "b")).IsEquivalentTo(["one", "two"]);
    }

    internal static RespireClientSideCacheOptions Broadcast(IReadOnlyList<RespireKey> prefixes)
        => new() { TrackingMode = RespireClientTrackingMode.Broadcast, KeyPrefixes = prefixes };

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}

[NotInParallel]
public class BroadcastClientCacheTopologyTests
{
    [Test]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Cluster)]
    [Arguments(RespireContainerServer.Redis, RespireContainerTopology.Sentinel)]
    [Arguments(RespireContainerServer.Valkey, RespireContainerTopology.Sentinel)]
    public async Task DiscoveredDataConnectionsKeepTrackingConfiguration(RespireContainerServer server, RespireContainerTopology topology)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Server = server, Topology = topology });
        var options = fixture.CreateOptions();
        await using var writer = await RespireClient.ConnectAsync(options);
        // The fixture partitions slots into thirds; touch every primary in Cluster mode.
        var keys = Enumerable.Range(0, 100).Select(index => $"hot:{index}")
            .GroupBy(key => Math.Min(new RespireKey(key).ClusterSlot / 5461, 2))
            .Select(group => group.First()).ToArray();
        await Assert.That(keys.Length).IsEqualTo(3);
        // Seed before enabling BCAST so setup invalidations cannot race the cache assertions.
        foreach (var key in keys) await writer.SetAsync(key, "old");
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = BroadcastClientCacheIntegrationTests.Broadcast(["hot:"]),
        });
        foreach (var key in keys)
            await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("old");
        await Assert.That(reader.ClientSideCache!.Count).IsEqualTo(3);
        foreach (var key in keys) await writer.SetAsync(key, "new");
        await BroadcastClientCacheIntegrationTests.UntilAsync(() => reader.ClientSideCache.Count == 0);
        foreach (var key in keys)
            await Assert.That(await reader.GetStringAsync(key)).IsEqualTo("new");
    }
}
