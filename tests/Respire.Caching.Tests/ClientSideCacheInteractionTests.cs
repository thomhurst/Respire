using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ClientSideCacheInteractionTests(RedisTestContainer fixture)
{
    [Test]
    public async Task CacheOperationsKeepUnrelatedClientSideCacheEntries()
    {
        await using RespireClient client = await ConnectWithClientCacheAsync();
        var cache = new RespireDistributedCache(client);
        string unrelated = $"unrelated:{Guid.NewGuid():N}";
        string entry = $"entry:{Guid.NewGuid():N}";
        await client.SetAsync(unrelated, (RespireValue)"value");
        await client.GetStringAsync(unrelated);
        int resident = client.ClientSideCache!.Count;
        await Assert.That(resident).IsGreaterThanOrEqualTo(1);

        // The cache scripts touch only their own key, so they must not flush other local entries.
        await cache.SetAsync(entry, new byte[16], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) });
        await cache.GetAsync(entry);
        await cache.TryGetAsync(entry, new ArrayBufferWriter<byte>());
        await cache.RefreshAsync(entry);

        long hits = client.ClientSideCache.GetStatistics().Hits;
        await Assert.That(await client.GetStringAsync(unrelated)).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsEqualTo(hits + 1);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(resident);
    }

    // Process-wide bytes, because reply continuations run on the receive loop. The budget is a
    // fixed ceiling well above the ~1 KB a hit costs, and far below the ~6.9 KB it cost when every
    // read flushed the client-side cache twice.
    [Test, NotInParallel]
    public async Task BufferWriterHitStaysWithinAllocationBudget()
    {
        const int Iterations = 2_000;
        const long BudgetPerCall = 2_048;
        await using RespireClient client = await ConnectWithClientCacheAsync();
        var cache = new RespireDistributedCache(client);
        string key = $"budget:{Guid.NewGuid():N}";
        await cache.SetAsync(key, new byte[64], new DistributedCacheEntryOptions());
        var writer = new ArrayBufferWriter<byte>(256);

        for (int i = 0; i < Iterations; i++)
        {
            writer.ResetWrittenCount();
            await cache.TryGetAsync(key, writer);
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < Iterations; i++)
        {
            writer.ResetWrittenCount();
            await cache.TryGetAsync(key, writer);
        }

        long perCall = (GC.GetTotalAllocatedBytes(precise: true) - before) / Iterations;

        await Assert.That(perCall).IsLessThanOrEqualTo(BudgetPerCall);
    }

    private async Task<RespireClient> ConnectWithClientCacheAsync()
    {
        return await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.IsolatedConnectionString) with
        {
            ClientSideCache = new RespireClientSideCacheOptions(),
        });
    }
}
