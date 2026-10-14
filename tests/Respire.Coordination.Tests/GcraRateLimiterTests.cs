using System.Threading.RateLimiting;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class GcraRateLimiterTests
{
    [ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74 Redis { get; init; }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ConcurrentClientsCannotExceedBurstCapacity(int permits)
    {
        await using var firstClient = await RespireClient.ConnectAsync(Redis.CreateOptions());
        await using var secondClient = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var prefix = SharedRespireContainer.Prefix("gcra");
        RespireKey key = new byte[] { 0xff, 0x00, 0x42 };
        await using var first = firstClient.WithKeyPrefix(prefix).Coordination.RateLimiters.Gcra(
            key, burstLimit: 12, emissionInterval: TimeSpan.FromMinutes(1));
        await using var second = secondClient.WithKeyPrefix(prefix).Coordination.RateLimiters.Gcra(
            key, burstLimit: 12, emissionInterval: TimeSpan.FromMinutes(1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(async index =>
        {
            using var lease = await (index % 2 == 0 ? first : second).AcquireAsync(permits);
            return lease.IsAcquired;
        }));

        await Assert.That(outcomes.Count(acquired => acquired) * permits).IsEqualTo(12);
        await Assert.That(first.GetStatistics()!.TotalSuccessfulLeases + second.GetStatistics()!.TotalSuccessfulLeases)
            .IsEqualTo(12 / permits);
        await Assert.That(first.GetStatistics()!.TotalFailedLeases + second.GetStatistics()!.TotalFailedLeases)
            .IsEqualTo(40 - 12 / permits);
    }

    [Test]
    public async Task ProbesAndRejectedBulkRequestsDoNotConsumeCapacity()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-probe");
        await using var limiter = client.Coordination.RateLimiters.Gcra(key, 3, TimeSpan.FromMinutes(1));

        using var sync = limiter.AttemptAcquire();
        await Assert.That(sync.IsAcquired).IsFalse();
        await Assert.That(sync.MetadataNames).IsEmpty();
        using var probe = await limiter.AcquireAsync(0);
        await Assert.That(probe.IsAcquired).IsTrue();
        using (var exists = await client.ExecuteAsync("EXISTS", [key]))
            await Assert.That(exists.AsInteger()).IsEqualTo(0);

        using var first = await limiter.AcquireAsync(1);
        using var denied = await limiter.AcquireAsync(3);
        using var remaining = await limiter.AcquireAsync(2);
        await Assert.That(first.IsAcquired).IsTrue();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.Zero && retry <= TimeSpan.FromMinutes(1)).IsTrue();
        await Assert.That(remaining.IsAcquired).IsTrue();
        using var fullProbe = await limiter.AcquireAsync(0);
        await Assert.That(fullProbe.IsAcquired).IsFalse();
        await Assert.That(fullProbe.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan probeRetry)).IsTrue();
        await Assert.That(probeRetry > TimeSpan.Zero).IsTrue();
        await Assert.That(limiter.GetStatistics()!.CurrentAvailablePermits).IsEqualTo(0);
        using var oversized = await limiter.AcquireAsync(4);
        await Assert.That(oversized.IsAcquired).IsFalse();
        await Assert.That(oversized.MetadataNames).IsEmpty();
    }

    [Test]
    public async Task BulkRetryIncludesEveryRequestedPermitAndStateSurvivesReplacement()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-retry");
        using (var original = client.Coordination.RateLimiters.Gcra(key, 3, TimeSpan.FromHours(1)))
        {
            using var lease = await original.AcquireAsync(3);
            await Assert.That(lease.IsAcquired).IsTrue();
        }

        await using var replacement = client.Coordination.RateLimiters.Gcra(key, 3, TimeSpan.FromHours(1));
        using var denied = await replacement.AcquireAsync(2);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(119) && retry <= TimeSpan.FromHours(2)).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(ttl.AsInteger() > TimeSpan.FromMinutes(179).TotalMilliseconds).IsTrue();
        await Assert.That(ttl.AsInteger() <= TimeSpan.FromHours(3).TotalMilliseconds).IsTrue();
    }

    [Test]
    public async Task PartialReplenishmentRestoresOnlyElapsedCapacity()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-partial");
        // A full burst originally acquired just over an hour ago has restored exactly one permit.
        var debt = (long)TimeSpan.FromMinutes(119).TotalMilliseconds;
        var tat = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds() + debt;
        using var seeded = await client.ExecuteAsync("SET", [key, tat, "PX", debt]);
        await using var limiter = client.Coordination.RateLimiters.Gcra(key, 3, TimeSpan.FromHours(1));

        using var probe = await limiter.AcquireAsync(0);
        await Assert.That(probe.IsAcquired).IsTrue();
        await Assert.That(limiter.GetStatistics()!.CurrentAvailablePermits).IsEqualTo(1);
        using var tooMany = await limiter.AcquireAsync(2);
        await Assert.That(tooMany.IsAcquired).IsFalse();
        using var one = await limiter.AcquireAsync(1);
        await Assert.That(one.IsAcquired).IsTrue();
        using var stored = await client.ExecuteAsync("GET", [key]);
        await Assert.That(long.Parse(stored.AsString(), System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo(tat + (long)TimeSpan.FromHours(1).TotalMilliseconds);
        using var exhausted = await limiter.AcquireAsync(1);
        await Assert.That(exhausted.IsAcquired).IsFalse();
    }

    [Test]
    public async Task LargeArrivalTimestampsRetainIntegerPrecision()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-precision");
        const long tat = 800_000_000_000_123;
        using var seeded = await client.ExecuteAsync("SET", [key, tat, "PX", 60_000]);
        // Large enough capacity to admit a permit while preserving the seeded future arrival time.
        await using var limiter = client.Coordination.RateLimiters.Gcra(key, 900_000_000, TimeSpan.FromSeconds(1000));
        using var acquired = await limiter.AcquireAsync(1);
        await Assert.That(acquired.IsAcquired).IsTrue();
        using var stored = await client.ExecuteAsync("GET", [key]);
        await Assert.That(long.Parse(stored.AsString(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(tat + 1_000_000);
        // Keep the shared fixture's test data short lived after exercising a very long valid refill horizon.
        using var cleanup = await client.ExecuteAsync("DEL", [key]);
    }

    [Test]
    public async Task QueuedBulkAcquisitionWaitsForRefill()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-refill");
        await using var limiter = client.Coordination.RateLimiters.Gcra(
            key, 2, TimeSpan.FromMilliseconds(100), queueLimit: 2);
        using var initial = await limiter.AcquireAsync(2);
        await Assert.That(initial.IsAcquired).IsTrue();
        using var next = await limiter.AcquireAsync(2).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(next.IsAcquired).IsTrue();
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(0);
    }

    [Test]
    public async Task QueuedAcquisitionHonorsCancellationAndDisposal()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        await using var limiter = client.Coordination.RateLimiters.Gcra(
            SharedRespireContainer.Key("gcra-cancel"), 1, TimeSpan.FromHours(1), queueLimit: 1);
        using var initial = await limiter.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var queued = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (limiter.GetStatistics()!.CurrentQueuedCount != 1)
            await Task.Delay(1, deadline.Token);
        await cancellation.CancelAsync();
        await Assert.That(async () => await queued.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(0);
        var disposed = limiter.AcquireAsync().AsTask();
        limiter.Dispose();
        await Assert.That(async () => await disposed.WaitAsync(deadline.Token)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task FractionalMillisecondsRoundUpAndExpiredStateRestoresBurst()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("gcra-rounded");
        await using var limiter = client.Coordination.RateLimiters.Gcra(key, 1_000_000, TimeSpan.FromTicks(10_001));
        using var initial = await limiter.AcquireAsync(1_000_000);
        using var denied = await limiter.AcquireAsync(1_000_000);
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromSeconds(1990) && retry <= TimeSpan.FromSeconds(2000)).IsTrue();

        // Simulate idle expiry without waiting for a long real-time replenishment period.
        using var expired = await client.ExecuteAsync("PEXPIRE", [key, 0]);
        using var restored = await limiter.AcquireAsync(1_000_000);
        await Assert.That(restored.IsAcquired).IsTrue();
    }
}

public class GcraRateLimiterValidationTests
{
    [Test]
    public async Task RejectsInvalidSettingsAndUnrepresentableBurstDuration()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", 6379)] });
        var factory = client.Coordination.RateLimiters;
        await Assert.That(() => factory.Gcra(default, 1, TimeSpan.FromSeconds(1))).Throws<ArgumentException>();
        await Assert.That(() => factory.Gcra("key", 0, TimeSpan.FromSeconds(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Gcra("key", 1, TimeSpan.FromTicks(9999))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Gcra("key", int.MaxValue, TimeSpan.FromHours(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Gcra("key", 1, TimeSpan.FromSeconds(1), queueLimit: -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => factory.Gcra("key", 1, TimeSpan.FromSeconds(1), queueProcessingOrder: (QueueProcessingOrder)99))
            .Throws<ArgumentOutOfRangeException>();
    }
}
