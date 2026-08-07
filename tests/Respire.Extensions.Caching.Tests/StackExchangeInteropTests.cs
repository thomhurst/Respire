using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

/// <summary>
/// Proves the stored layout is interchangeable with Microsoft.Extensions.Caching.StackExchangeRedis:
/// entries written by either implementation are readable — with working expiration metadata — by
/// the other. TTLs are whole seconds because the Microsoft implementation truncates to EXPIRE
/// seconds granularity.
/// </summary>
[ClassDataSource<RedisTestFixture>(Shared = SharedType.PerClass)]
[NotInParallel("redis-integration")]
public class StackExchangeInteropTests(RedisTestFixture fixture)
{
    private const string InstanceName = "interop:";

    private RespireClient _client = null!;
    private RespireDistributedCache _respireCache = null!;
    private RedisCache _microsoftCache = null!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        (await _client.ExecuteAsync("FLUSHDB")).Dispose();
        _respireCache = new RespireDistributedCache(_client, new RespireCacheOptions { InstanceName = InstanceName });
        _microsoftCache = new RedisCache(new RedisCacheOptions
        {
            Configuration = fixture.ConnectionString,
            InstanceName = InstanceName,
        });
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        _microsoftCache.Dispose();
        await _respireCache.DisposeAsync();
        await _client.DisposeAsync();
    }

    [Test]
    public async Task MicrosoftWrites_RespireReads()
    {
        var value = Encoding.UTF8.GetBytes("written by Microsoft");
        await _microsoftCache.SetAsync("ms-to-respire", value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        });

        var fetched = await _respireCache.GetAsync("ms-to-respire");

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.SequenceEqual(value)).IsTrue();
    }

    [Test]
    public async Task RespireWrites_MicrosoftReads()
    {
        var value = Encoding.UTF8.GetBytes("written by Respire");
        await _respireCache.SetAsync("respire-to-ms", value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        });

        var fetched = await _microsoftCache.GetAsync("respire-to-ms");

        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.SequenceEqual(value)).IsTrue();
    }

    [Test]
    public async Task SlidingEntryWrittenByMicrosoft_RespireReadReArmsTtl()
    {
        await _microsoftCache.SetAsync("ms-sliding", [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(10),
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(await _respireCache.GetAsync("ms-sliding")).IsNotNull();

        // A Respire read re-arms the TTL back to the full sliding window in the same round trip.
        using var pttl = await _client.ExecuteAsync("PTTL", InstanceName + "ms-sliding");
        await Assert.That(pttl.AsInteger()).IsGreaterThan(8000);
    }

    [Test]
    public async Task SlidingEntryWrittenByRespire_MicrosoftRefreshWorks()
    {
        await _respireCache.SetAsync("respire-sliding", [1], new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromSeconds(10),
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        await _microsoftCache.RefreshAsync("respire-sliding");

        using var pttl = await _client.ExecuteAsync("PTTL", InstanceName + "respire-sliding");
        await Assert.That(pttl.AsInteger()).IsGreaterThan(8000);
    }

    [Test]
    public async Task MicrosoftEntryWithOffsetAbsoluteExpiration_IsNotPrematurelyDeleted()
    {
        // The Microsoft implementation stores AbsoluteExpiration as DateTimeOffset.Ticks — with
        // the caller's offset baked in — and reads them back as UTC. A future instant expressed
        // with a negative offset therefore looks already-expired in the stored metadata, and the
        // true deadline is unknowable. The read script must neither delete the entry (the
        // Microsoft reader does) nor re-arm the sliding window (which could extend it past its
        // real deadline): the value stays readable and the write-time TTL keeps governing.
        var futureWithNegativeOffset = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(-5));
        await _microsoftCache.SetAsync("ms-offset", [1], new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = futureWithNegativeOffset,
            SlidingExpiration = TimeSpan.FromSeconds(30),
        });

        await Task.Delay(TimeSpan.FromSeconds(2));
        await Assert.That(await _respireCache.GetAsync("ms-offset")).IsNotNull();

        // Not deleted, and not re-armed either: the TTL keeps decaying from the armed 30s.
        using var pttl = await _client.ExecuteAsync("PTTL", InstanceName + "ms-offset");
        await Assert.That(pttl.AsInteger()).IsGreaterThan(0);
        await Assert.That(pttl.AsInteger()).IsLessThan(28500);
    }

    [Test]
    public async Task EntryWithoutExpiration_ReadableBothWays()
    {
        await _microsoftCache.SetAsync("ms-forever", [1], new DistributedCacheEntryOptions());
        await _respireCache.SetAsync("respire-forever", [2], new DistributedCacheEntryOptions());

        await Assert.That(await _respireCache.GetAsync("ms-forever")).IsNotNull();
        await Assert.That(await _microsoftCache.GetAsync("respire-forever")).IsNotNull();
    }
}
