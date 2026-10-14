using System.Threading.RateLimiting;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimiterUpdateTests
{
    [ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74 Redis { get; init; }

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static RateLimiter Create(RespireRateLimiters factory, RespireKey key, int kind, int limit, int queue = 0)
        => kind switch
        {
            0 => factory.FixedWindow(key, limit, Hour, queue),
            1 => factory.SlidingWindow(key, limit, Hour, 4, queue),
            2 => factory.TokenBucket(key, limit, 1, Hour, queue),
            _ => factory.Gcra(key, limit, Hour, queue),
        };

    private static RespireRateLimit Define(int kind, int limit, TimeSpan? period = null)
        => kind switch
        {
            0 => RespireRateLimit.FixedWindow(limit, period ?? Hour),
            1 => RespireRateLimit.SlidingWindow(limit, period ?? Hour, 8),
            2 => RespireRateLimit.TokenBucket(limit, 1, period ?? Hour),
            _ => RespireRateLimit.Gcra(limit, period ?? Hour),
        };

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CapacityChangesPreserveUsageAndRejectOversizedRequests(int kind)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = Create(factory, SharedRespireContainer.Key("update-capacity"), kind, 4);
        using var initial = await limiter.AcquireAsync(3);
        await Assert.That(initial.IsAcquired).IsTrue();
        await factory.UpdateAsync(limiter, Define(kind, 6));
        // A larger bucket preserves its available tokens; it earns the added capacity by refilling.
        using var increased = await limiter.AcquireAsync(kind == 2 ? 1 : 3);
        await Assert.That(increased.IsAcquired).IsTrue();
        using var empty = await limiter.AcquireAsync();
        await Assert.That(empty.IsAcquired).IsFalse();
        await factory.UpdateAsync(limiter, Define(kind, 2));
        using var oversized = await limiter.AcquireAsync(3);
        await Assert.That(oversized.IsAcquired).IsFalse();
        await Assert.That(oversized.MetadataNames).IsEmpty();
        using var stillCharged = await limiter.AcquireAsync();
        await Assert.That(stillCharged.IsAcquired).IsFalse();
    }

    [Test]
    public async Task FixedWindowKeepsDeadlineAndUsesNewWidthForTheNextWindow()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("update-fixed");
        await using var limiter = factory.FixedWindow(key, 2, Hour);
        using var first = await limiter.AcquireAsync(2);
        await factory.UpdateAsync(limiter, RespireRateLimit.FixedWindow(3, TimeSpan.FromHours(2)));
        using var ttl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(ttl.AsInteger() <= Hour.TotalMilliseconds).IsTrue();
        using var remaining = await limiter.AcquireAsync();
        await Assert.That(remaining.IsAcquired).IsTrue();
        // Model expiration without a wall-clock sleep.
        using var deleted = await client.ExecuteAsync("DEL", [key]);
        using var nextWindow = await limiter.AcquireAsync(3);
        using var newTtl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(newTtl.AsInteger() > Hour.TotalMilliseconds).IsTrue();
    }

    [Test]
    public async Task SlidingWindowDoesNotResurrectExpiredHistoryAndRetainsFutureSegmentEnds()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("update-sliding");
        await using var limiter = factory.SlidingWindow(key, 10, Hour, 1);
        var now = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        using var seeded = await client.ExecuteAsync("ZADD", [key, now - 7_200_000, "expired:8", now + 3_600_000, "future:3"]);
        await factory.UpdateAsync(limiter, RespireRateLimit.SlidingWindow(10, TimeSpan.FromHours(3), 12));
        using var acquired = await limiter.AcquireAsync(7);
        await Assert.That(acquired.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();
        await factory.UpdateAsync(limiter, RespireRateLimit.SlidingWindow(11, TimeSpan.FromMinutes(1), 4));
        using var fresh = await limiter.AcquireAsync();
        await Assert.That(fresh.IsAcquired).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(ttl.AsInteger() > TimeSpan.FromMinutes(30).TotalMilliseconds).IsTrue();
    }

    [Test]
    public async Task TokenBucketSettlesOldRefillClampsAndStartsNewPeriod()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("update-tokens");
        await using var limiter = factory.TokenBucket(key, 10, 2, Hour);
        var now = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        using var seeded = await client.ExecuteAsync("HSET", [key, "time", now - 5_400_000, "tokens", 1]);
        await factory.UpdateAsync(limiter, RespireRateLimit.TokenBucket(2, 1, TimeSpan.FromHours(2)));
        using var available = await limiter.AcquireAsync(2);
        await Assert.That(available.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(119)).IsTrue();
    }

    [Test]
    public async Task GcraRescalesPermitDebtWhenTheEmissionIntervalChanges()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Gcra(SharedRespireContainer.Key("update-gcra"), 4, Hour);
        using var initial = await limiter.AcquireAsync(4);
        await factory.UpdateAsync(limiter, RespireRateLimit.Gcra(2, TimeSpan.FromHours(2)));
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromHours(5.9) && retry <= TimeSpan.FromHours(6)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task IncreasingCapacityWakesQueuedRequestsWithoutWaitingForOldRetry(int kind)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = Create(factory, SharedRespireContainer.Key("update-queue"), kind, 1, 10);
        using var initial = await limiter.AcquireAsync();
        var pending = limiter.AcquireAsync().AsTask();
        await factory.UpdateAsync(limiter, Define(kind, 2));
        using var acquired = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(acquired.IsAcquired).IsTrue();
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DecreasingCapacityCompletesNowImpossibleQueuedRequests(int kind)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = Create(factory, SharedRespireContainer.Key("update-queue-decrease"), kind, 3, 10);
        using var initial = await limiter.AcquireAsync(3);
        var pending = limiter.AcquireAsync(3).AsTask();
        await factory.UpdateAsync(limiter, Define(kind, 2));
        using var denied = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.MetadataNames).IsEmpty();
    }

    [Test]
    public async Task FasterTokenRefillWakesAQueuedRequest()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.TokenBucket(SharedRespireContainer.Key("update-token-queue"), 1, 1, Hour, queueLimit: 2);
        using var initial = await limiter.AcquireAsync();
        var queued = limiter.AcquireAsync().AsTask();
        await factory.UpdateAsync(limiter, RespireRateLimit.TokenBucket(2, 2, TimeSpan.FromMilliseconds(20)));
        using var acquired = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(acquired.IsAcquired).IsTrue();
    }

    [Test]
    public async Task CompositeSegmentChangesRetainSortedHistoryAndItsFullLifetime()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("update-composite-history");
        await using var limiter = factory.Composite(key, [RespireRateLimit.SlidingWindow(10, Hour, 1)]);
        var now = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        using var seeded = await client.ExecuteAsync("HSET", [key, "config", "v1|1,10,3600000,1",
            "1", (now + 3_600_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",3"]);
        await factory.UpdateAsync(limiter, [RespireRateLimit.SlidingWindow(10, TimeSpan.FromMinutes(1), 4)]);
        using var acquired = await limiter.AcquireAsync(7);
        await Assert.That(acquired.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry <= TimeSpan.FromSeconds(75)).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(ttl.AsInteger() > TimeSpan.FromMinutes(30).TotalMilliseconds).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentUpdatesAndAcquisitionsPreserveExactCapacity(int kind)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = Create(factory, SharedRespireContainer.Key("update-concurrent"), kind, 100);
        var updates = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
                await factory.UpdateAsync(limiter, Define(kind, 100, i % 2 == 0 ? TimeSpan.FromHours(2) : Hour));
        });
        var attempts = Enumerable.Range(0, 200).Select(async _ =>
        {
            using var lease = await limiter.AcquireAsync();
            return lease.IsAcquired;
        }).ToArray();
        var outcomes = await Task.WhenAll(attempts);
        await updates;
        await Assert.That(outcomes.Count(static acquired => acquired)).IsEqualTo(100);
    }

    [Test]
    public async Task CompositeConfigurationMismatchLeavesLimiterUsableAfterAdoption()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("update-mismatch");
        RespireRateLimit[] old = [Define(0, 4)];
        RespireRateLimit[] changed = [Define(0, 6)];
        await using var updater = factory.Composite(key, old);
        await using var follower = factory.Composite(key, old);
        using var initial = await updater.AcquireAsync(3);
        await Assert.That(initial.IsAcquired).IsTrue();
        await factory.UpdateAsync(updater, changed);

        // A second migration with the stale signature must reject before modifying shared usage.
        await Assert.That(async () => await factory.UpdateAsync(follower, changed))
            .Throws<RespireServerException>();
        await factory.UpdateAsync(follower, changed, RespireRateLimitUpdateMode.Adopt);
        using var remaining = await follower.AcquireAsync(3);
        await Assert.That(remaining.IsAcquired).IsTrue();
        using var exhausted = await updater.AcquireAsync();
        await Assert.That(exhausted.IsAcquired).IsFalse();
    }

    [Test]
    public async Task CompositeMigratesAllComponentsAndOtherInstancesAdoptOnce()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        await using var otherClient = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.WithKeyPrefix(SharedRespireContainer.Prefix("update-composite")).Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("limits");
        RespireRateLimit[] old = [Define(0, 4), Define(1, 4), Define(2, 4)];
        await using var limiter = factory.Composite(key, old);
        using var initial = await limiter.AcquireAsync(3);
        RespireRateLimit[] changed = [Define(0, 6, TimeSpan.FromHours(2)), Define(1, 6, TimeSpan.FromMinutes(30)), Define(2, 6)];
        await factory.UpdateAsync(limiter, changed);
        using var remaining = await limiter.AcquireAsync();
        await Assert.That(remaining.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();

        // Standalone GCRA migration must also happen exactly once across processes.
        var sharedKey = SharedRespireContainer.Key("update-adopt");
        var firstFactory = client.Coordination.RateLimiters;
        var secondFactory = otherClient.Coordination.RateLimiters;
        await using var first = firstFactory.Gcra(sharedKey, 4, Hour);
        await using var second = secondFactory.Gcra(sharedKey, 4, Hour);
        using var charged = await first.AcquireAsync(4);
        var newSettings = RespireRateLimit.Gcra(6, TimeSpan.FromHours(2));
        await firstFactory.UpdateAsync(first, newSettings);
        await secondFactory.UpdateAsync(second, newSettings, RespireRateLimitUpdateMode.Adopt);
        using var free = await second.AcquireAsync(2);
        await Assert.That(free.IsAcquired).IsTrue();
        using var exhausted = await first.AcquireAsync();
        await Assert.That(exhausted.IsAcquired).IsFalse();
    }
}

public class RateLimiterUpdateRedis810Tests
{
    [ClassDataSource<SharedRedis810>(Shared = SharedType.PerTestSession)]
    public required SharedRedis810 Redis { get; init; }

    [Test]
    public async Task FixedWindowIncrexPreservesUsageAboveTheReducedLimit()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.FixedWindow(SharedRespireContainer.Key("update-increx"), 4, TimeSpan.FromHours(1));
        using var initial = await limiter.AcquireAsync(4);
        await factory.UpdateAsync(limiter, RespireRateLimit.FixedWindow(2, TimeSpan.FromHours(2)));
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(59)).IsTrue();
        await factory.UpdateAsync(limiter, RespireRateLimit.FixedWindow(5, TimeSpan.FromHours(2)));
        using var remaining = await limiter.AcquireAsync();
        await Assert.That(remaining.IsAcquired).IsTrue();
        await Assert.That(((RedisRateLimiter)limiter).IncrexUnsupported).IsFalse();
    }
}
