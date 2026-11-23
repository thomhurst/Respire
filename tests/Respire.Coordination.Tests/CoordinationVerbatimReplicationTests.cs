using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public sealed class Redis62VerbatimReplication() : RedisTestContainer("--lua-replicate-commands", "no")
{
    protected override string ServerImage => "redis:6.2.14-alpine";
}

[ClassDataSource<Redis62VerbatimReplication>(Shared = SharedType.PerTestSession)]
public class CoordinationVerbatimReplicationTests(Redis62VerbatimReplication fixture)
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    [Test]
    [Arguments(nameof(RespireRateLimiters.FixedWindow))]
    [Arguments(nameof(RespireRateLimiters.SlidingWindow))]
    [Arguments(nameof(RespireRateLimiters.TokenBucket))]
    [Arguments(nameof(RespireRateLimiters.Gcra))]
    [Arguments(nameof(RespireRateLimiters.Composite))]
    public async Task RateLimiterAcquiresAndEnforcesCapacity(string algorithm)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var limits = client.Coordination.RateLimiters;
        var key = SharedRespireContainer.Key("verbatim-limit");
        await using var limiter = algorithm switch
        {
            nameof(RespireRateLimiters.FixedWindow) => limits.FixedWindow(key, 2, Period),
            nameof(RespireRateLimiters.SlidingWindow) => limits.SlidingWindow(key, 2, Period, segments: 4),
            nameof(RespireRateLimiters.TokenBucket) => limits.TokenBucket(key, 2, tokensPerPeriod: 1, Period),
            nameof(RespireRateLimiters.Gcra) => limits.Gcra(key, 2, Period),
            nameof(RespireRateLimiters.Composite) => limits.Composite(key, [RespireRateLimit.FixedWindow(2, Period)]),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        using var first = await limiter.AcquireAsync();
        await Assert.That(first.IsAcquired).IsTrue();
        using var second = await limiter.AcquireAsync();
        await Assert.That(second.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();
    }

    [Test]
    public async Task CompositeMigrationPreservesConsumedPermits()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var limits = client.Coordination.RateLimiters;
        await using var limiter = limits.Composite(SharedRespireContainer.Key("verbatim-migration"),
            [RespireRateLimit.FixedWindow(2, Period)]);
        using var first = await limiter.AcquireAsync();
        await Assert.That(first.IsAcquired).IsTrue();

        await limits.UpdateAsync(limiter, [RespireRateLimit.FixedWindow(1, Period)]);

        using var denied = await limiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();
    }

    [Test]
    public async Task FencedLockAcquiresAfterReadingCounterTtl()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = SharedRespireContainer.Key("verbatim-fence");
        await using var attempt = await client.Coordination.TryAcquireFencedLockAsync(key, key + ":counter", Period);

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(attempt.Lock.FencingToken).IsEqualTo(1L);
    }

    [Test]
    public async Task FairLockAcquiresAfterReadingServerTime()
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = RespireProtocol.Resp3,
            ClientSideCache = new(),
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var key = SharedRespireContainer.Key("verbatim-fair");
        var fair = client.Coordination.CreateFairLock(key, key + ":counter", key + ":queue");
        await using var lease = await fair.AcquireAsync(Period, Period);

        await Assert.That(lease.FencingToken).IsEqualTo(1L);
    }

    [Test]
    public async Task ReadWriteLockAcquiresAndRenewsAfterReadingServerTime()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = SharedRespireContainer.Key("verbatim-read-write");
        await using var attempt = await client.Coordination.TryAcquireReadLockAsync(key, Period);

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(await attempt.Lock.ResetExpiryAsync(Period)).IsTrue();
        await Assert.That(await attempt.Lock.VerifyStillHeldAsync()).IsTrue();
    }
}
