using Microsoft.Extensions.Caching.Distributed;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[ClassDataSource<Redis62VerbatimReplication>(Shared = SharedType.PerTestSession)]
public class CacheVerbatimReplicationTests(Redis62VerbatimReplication fixture)
{
    [Test]
    public async Task StampedeProtectionPublishesWithAbsoluteExpiration()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        const string key = "absolute-publication";
        var deadline = (await client.Server.TimeAsync()).AddMinutes(2);
        var options = new RespireCacheAsideOptions { StaleWhileRevalidate = TimeSpan.FromSeconds(30) };

        var value = await client.GetOrSetAsync(key, options, _ => ValueTask.FromResult(42), deadline);

        await Assert.That(value).IsEqualTo(42);
        await Assert.That(await client.GetOrSetAsync<int>(key, options, _ => throw new Exception("cache hit"), deadline))
            .IsEqualTo(42);

        // Redis 6.2 has no PEXPIRETIME. Bracket the TTL observation with samples of Redis's clock.
        long expiresAt = (deadline + options.StaleWhileRevalidate).ToUnixTimeMilliseconds();
        long before = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        long ttl = await ReadTtlAsync(client, key);
        long after = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        await Assert.That(ttl).IsGreaterThanOrEqualTo(expiresAt - after);
        await Assert.That(ttl).IsLessThanOrEqualTo(expiresAt - before);
    }

    [Test]
    public async Task StampedeProtectionExtendsExistingEntryWhileFactoryHoldsLease()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        const string key = "refresh-lease";
        var expiry = TimeSpan.FromMinutes(5);
        var options = new RespireCacheAsideOptions { LockTimeout = TimeSpan.FromMinutes(1) };
        await client.GetOrSetAsync(key, options, _ => ValueTask.FromResult(1), expiry);
        await client.ExpireAsync(key, TimeSpan.FromSeconds(15));

        var refreshed = await client.GetOrSetAsync<int>(key, options with { EarlyRefreshBeta = double.MaxValue },
            async _ =>
            {
                long ttl = await ReadTtlAsync(client, key);
                await Assert.That(ttl).IsGreaterThan(15_000L);
                await Assert.That(ttl).IsLessThanOrEqualTo(60_000L);
                return 2;
            }, expiry);

        await Assert.That(refreshed).IsEqualTo(2);
        await Assert.That(await client.GetOrSetAsync<int>(key, options, _ => throw new Exception("cache hit"), expiry))
            .IsEqualTo(2);
    }

    [Test]
    public async Task DistributedCacheReadRefreshesTtlWithinAbsoluteDeadline()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        await using var cache = new RespireDistributedCache(client);
        const string key = "read-refresh";
        await cache.SetAsync(key, [42], new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2),
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });
        await client.ExpireAsync(key, TimeSpan.FromSeconds(15));

        await Assert.That(await cache.GetAsync(key)).IsEquivalentTo(new byte[] { 42 });

        long ttl = await ReadTtlAsync(client, key);
        await Assert.That(ttl).IsGreaterThan(15_000L);
        await Assert.That(ttl).IsLessThanOrEqualTo(120_000L);
    }

    [Test]
    public async Task DistributedCacheSetCorrectionShrinksTtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        await using var cache = new RespireDistributedCache(client);
        const string key = "set-correction";
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        await cache.SetAsync(key, [42], new DistributedCacheEntryOptions { AbsoluteExpiration = deadline });

        // Supply the remainder a delayed set would compute once its reply arrived.
        using var result = await client.Scripts.ExecuteAsync(
            RespireDistributedCache.CapTtlScript, [key], [deadline.UtcTicks, -1L, 30_000L]);

        await Assert.That(result.AsInteger()).IsEqualTo(1L);
        long ttl = await ReadTtlAsync(client, key);
        await Assert.That(ttl).IsGreaterThan(0L);
        await Assert.That(ttl).IsLessThanOrEqualTo(30_000L);
        await Assert.That(await cache.GetAsync(key)).IsEquivalentTo(new byte[] { 42 });
    }

    [Test]
    public async Task DistributedCacheRefreshCorrectionShrinksTtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.IsolatedConnectionString);
        await using var cache = new RespireDistributedCache(client);
        const string key = "refresh-correction";
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        await cache.SetAsync(key, [42], new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = deadline,
            SlidingExpiration = TimeSpan.FromMinutes(5)
        });

        // Use the no-proof path. Redis 6.2 cannot supply PEXPIRETIME for a refresh.
        using var correction = await client.Scripts.ExecuteAsync(
            RespireDistributedCache.CapRefreshedTtlScript, [key], [deadline.AddSeconds(-30).UtcTicks]);

        long ttl = await ReadTtlAsync(client, key);
        await Assert.That(ttl).IsGreaterThan(0L);
        await Assert.That(ttl).IsLessThanOrEqualTo(30_000L);
        using var payload = await client.ExecuteAsync("HGET", key, "data");
        await Assert.That(payload.AsBytes()).IsEquivalentTo(new byte[] { 42 });
    }

    private static async Task<long> ReadTtlAsync(RespireClient client, string key)
    {
        using var result = await client.ExecuteAsync("PTTL", key);
        return result.AsInteger();
    }
}
