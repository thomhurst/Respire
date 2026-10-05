using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
[NotInParallel(TestConstraints.ClientCacheHits)]
public class ClientCacheScopeIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task OptInKeyPrefixesCacheOnlyCoveredKeysAndInvalidateThem()
    {
        var root = $"scope:{Guid.NewGuid():N}:";
        var hot = root + "hot:key";
        var cold = root + "cold:key";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { Connections = 1 };
        await using var writer = await RespireClient.ConnectAsync(options);
        await writer.SetAsync(hot, "old");
        await writer.SetAsync(cold, "outside");
        await using var reader = await RespireClient.ConnectAsync(options with
        {
            ClientSideCache = new() { KeyPrefixes = [root + "hot:"] },
        });

        await Assert.That(await reader.GetStringAsync(hot)).IsEqualTo("old");
        await Assert.That(await reader.GetStringAsync(cold)).IsEqualTo("outside");
        await Assert.That(reader.ClientSideCache!.Count).IsEqualTo(1);

        await writer.SetAsync(cold, "outside changed");
        await Assert.That(await reader.GetStringAsync(cold)).IsEqualTo("outside changed");
        await writer.SetAsync(hot, "new");
        await UntilAsync(() => reader.ClientSideCache.Count == 0);
        await Assert.That(await reader.GetStringAsync(hot)).IsEqualTo("new");
    }

    [Test]
    public async Task WithoutClientCacheReadsServerWhileCachedEntryRemains()
    {
        var key = $"scope:{Guid.NewGuid():N}:fresh";
        var options = RespireOptions.Parse(fixture.ConnectionString) with { Connections = 1 };
        await using var client = await RespireClient.ConnectAsync(options with { ClientSideCache = new() });
        await client.SetAsync(key, "old");
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("old");
        var statistics = client.ClientSideCache!.GetStatistics();

        var fresh = client.WithoutClientCache();
        await Assert.That(await fresh.GetStringAsync(key)).IsEqualTo("old");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(statistics.Hits);
        await Assert.That(client.ClientSideCache.GetStatistics().Misses).IsEqualTo(statistics.Misses);

        await fresh.SetAsync(key, "new");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync(key)).IsEqualTo("new");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
