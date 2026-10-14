using System.Globalization;
using System.Threading.RateLimiting;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class CompositeRateLimiterTests
{
    [ClassDataSource<SharedRedis74>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74 Redis { get; init; }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ConcurrentClientsConsumeOnlyTheSmallestCapacity(int permits)
    {
        await using var firstClient = await RespireClient.ConnectAsync(Redis.CreateOptions());
        await using var secondClient = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var prefix = SharedRespireContainer.Prefix("composite");
        RespireKey key = new byte[] { 0xff, 0, 0x42 };
        RespireRateLimit[] limits = [
            RespireRateLimit.FixedWindow(18, TimeSpan.FromMinutes(1)),
            RespireRateLimit.SlidingWindow(24, TimeSpan.FromMinutes(1), 4),
            RespireRateLimit.TokenBucket(12, 1, TimeSpan.FromMinutes(1))];
        await using var first = firstClient.WithKeyPrefix(prefix).Coordination.RateLimiters.Composite(key, limits);
        await using var second = secondClient.WithKeyPrefix(prefix).Coordination.RateLimiters.Composite(key, limits);
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
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task AnyRejectedComponentLeavesEveryComponentUnchanged(int rejectingIndex)
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("composite-atomic");
        RespireRateLimit[] limits = [
            RespireRateLimit.FixedWindow(rejectingIndex == 0 ? 3 : 10, TimeSpan.FromMinutes(1)),
            RespireRateLimit.SlidingWindow(rejectingIndex == 1 ? 3 : 10, TimeSpan.FromMinutes(1), 4),
            RespireRateLimit.TokenBucket(rejectingIndex == 2 ? 3 : 10, 1, TimeSpan.FromMinutes(1))];
        await using var limiter = client.Coordination.RateLimiters.Composite(key, limits);
        using var emptyProbe = await limiter.AcquireAsync(0);
        await Assert.That(emptyProbe.IsAcquired).IsTrue();
        using (var exists = await client.ExecuteAsync("EXISTS", [key]))
            await Assert.That(exists.AsInteger()).IsEqualTo(0);
        using var first = await limiter.AcquireAsync(2);
        await Assert.That(first.IsAcquired).IsTrue();
        var before = await ReadStateAsync(client, key);
        using var probe = await limiter.AcquireAsync(0);
        using var denied = await limiter.AcquireAsync(2);
        await Assert.That(probe.IsAcquired).IsTrue();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(await ReadStateAsync(client, key)).IsEqualTo(before);
        using var remaining = await limiter.AcquireAsync(1);
        await Assert.That(remaining.IsAcquired).IsTrue();
        using var fullProbe = await limiter.AcquireAsync(0);
        await Assert.That(fullProbe.IsAcquired).IsFalse();
        await Assert.That(limiter.GetStatistics()!.CurrentAvailablePermits).IsEqualTo(0);
    }

    [Test]
    public async Task RetryIncludesAllComponentsAndBulkRefillAndSurvivesReplacement()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("composite-retry");
        RespireRateLimit[] limits = [
            RespireRateLimit.FixedWindow(3, TimeSpan.FromMinutes(1)),
            RespireRateLimit.SlidingWindow(3, TimeSpan.FromMinutes(2), 2),
            RespireRateLimit.TokenBucket(3, 1, TimeSpan.FromHours(1))];
        using (var original = client.Coordination.RateLimiters.Composite(key, limits))
        {
            using var lease = await original.AcquireAsync(3);
            await Assert.That(lease.IsAcquired).IsTrue();
        }
        await using var replacement = client.Coordination.RateLimiters.Composite(key, limits);
        using var denied = await replacement.AcquireAsync(2);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(119) && retry <= TimeSpan.FromHours(2)).IsTrue();
        using var ttl = await client.ExecuteAsync("PTTL", [key]);
        await Assert.That(ttl.AsInteger() > TimeSpan.FromMinutes(179).TotalMilliseconds).IsTrue();
        await using var mismatch = client.Coordination.RateLimiters.Composite(key, limits.Reverse().ToArray());
        var before = await ReadStateAsync(client, key);
        await Assert.That(async () => await mismatch.AcquireAsync()).Throws<RespireServerException>();
        await Assert.That(await ReadStateAsync(client, key)).IsEqualTo(before);
    }

    [Test]
    public async Task ExpiredWindowsAndElapsedBucketPeriodsRestoreCapacity()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("composite-refill");
        RespireRateLimit[] limits = [
            RespireRateLimit.FixedWindow(3, TimeSpan.FromHours(1)),
            RespireRateLimit.SlidingWindow(3, TimeSpan.FromHours(1), 2),
            RespireRateLimit.TokenBucket(3, 1, TimeSpan.FromHours(1))];
        await using var limiter = client.Coordination.RateLimiters.Composite(key, limits);
        using var initial = await limiter.AcquireAsync(3);
        var now = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        // Seed elapsed server time deterministically; only one bucket token has refilled.
        using var seed = await client.ExecuteAsync("HSET", [key,
            "1", string.Create(CultureInfo.InvariantCulture, $"{now - 1},3"),
            "2", string.Create(CultureInfo.InvariantCulture, $"{now - 3_600_001},3"),
            "3", string.Create(CultureInfo.InvariantCulture, $"{now - 3_601_000},0")]);
        using var tooMany = await limiter.AcquireAsync(2);
        await Assert.That(tooMany.IsAcquired).IsFalse();
        using var one = await limiter.AcquireAsync(1);
        await Assert.That(one.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync(1);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(59) && retry < TimeSpan.FromHours(1)).IsTrue();
    }

    [Test]
    public async Task SlidingBulkRetryWaitsForEnoughSegments()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var key = SharedRespireContainer.Key("composite-sliding");
        await using var limiter = client.Coordination.RateLimiters.Composite(key, [
            RespireRateLimit.SlidingWindow(3, TimeSpan.FromHours(1), 4),
            RespireRateLimit.FixedWindow(10, TimeSpan.FromHours(1))]);
        using var initial = await limiter.AcquireAsync(3);
        var now = (await client.Server.TimeAsync()).ToUnixTimeMilliseconds();
        using var seed = await client.ExecuteAsync("HSET", [key, "1",
            string.Create(CultureInfo.InvariantCulture, $"{now - 1_800_000},1,{now - 900_000},2")]);
        using var denied = await limiter.AcquireAsync(2);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.FromMinutes(44) && retry <= TimeSpan.FromMinutes(45)).IsTrue();
    }

    [Test]
    public async Task QueuedAcquisitionsUseCompositeRetryAndHonorCancellationAndDisposal()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        await using var refill = client.Coordination.RateLimiters.Composite(SharedRespireContainer.Key("composite-queue"), [
            RespireRateLimit.FixedWindow(1, TimeSpan.FromMilliseconds(50)),
            RespireRateLimit.TokenBucket(1, 1, TimeSpan.FromMilliseconds(100))], queueLimit: 1);
        using var first = await refill.AcquireAsync();
        using var next = await refill.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(next.IsAcquired).IsTrue();

        await using var limiter = client.Coordination.RateLimiters.Composite(SharedRespireContainer.Key("composite-cancel"), [
            RespireRateLimit.FixedWindow(1, TimeSpan.FromHours(1))], queueLimit: 1);
        using var initial = await limiter.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = limiter.AcquireAsync(1, cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (limiter.GetStatistics()!.CurrentQueuedCount != 1) await Task.Delay(1, deadline.Token);
        await cancellation.CancelAsync();
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        var disposed = limiter.AcquireAsync().AsTask();
        limiter.Dispose();
        await Assert.That(async () => await disposed.WaitAsync(deadline.Token)).Throws<ObjectDisposedException>();
    }

    private static async Task<string> ReadStateAsync(RespireClient client, RespireKey key)
    {
        using var state = await client.ExecuteAsync("HMGET", [key, "config", "1", "2", "3"]);
        return string.Join('|', Enumerable.Range(0, state.Count).Select(i => state[i].AsString()));
    }
}

public class CompositeRateLimiterClusterTests
{
    [ClassDataSource<SharedRedis74Cluster>(Shared = SharedType.PerTestSession)]
    public required SharedRedis74Cluster Redis { get; init; }

    [Test]
    public async Task CompositeUsesOneClusterKeyWithoutRequiringHashTags()
    {
        await using var client = await RespireClient.ConnectAsync(Redis.CreateOptions());
        var prefixed = client.WithKeyPrefix(SharedRespireContainer.Prefix("composite-cluster"));
        RespireKey key = new byte[] { 0xff, 0, 0x42 };
        await using var limiter = prefixed.Coordination.RateLimiters.Composite(key, [
            RespireRateLimit.FixedWindow(2, TimeSpan.FromMinutes(1)),
            RespireRateLimit.TokenBucket(1, 1, TimeSpan.FromMinutes(1))]);
        using var accepted = await limiter.AcquireAsync();
        using var denied = await limiter.AcquireAsync();
        await Assert.That(accepted.IsAcquired).IsTrue();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(await prefixed.Keys.TypeAsync(key)).IsEqualTo(RespireKeyType.Hash);
        using var exists = await client.ExecuteAsync("EXISTS", [key]);
        await Assert.That(exists.AsInteger()).IsEqualTo(0);
    }
}
