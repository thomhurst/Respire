using System.Threading.RateLimiting;
using Respire.Commands;
using Respire.Protocol;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

[NotInParallel]
public class RedisRateLimiterTests
{
    [Test]
    public async Task FixedWindowUsesIncrexOnRedis88AndLimitsPermits()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:8.10-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "fixed", permitLimit: 2, TimeSpan.FromSeconds(5));

        using var acquired = await limiter.AcquireAsync(2);
        await Assert.That(acquired.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync(1);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)).IsTrue();
        await Assert.That(retry > TimeSpan.Zero).IsTrue();
    }

    [Test]
    public async Task SynchronousAttemptAcquireExplainsAsyncRequirement()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:8.10-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "sync", permitLimit: 1, TimeSpan.FromSeconds(1));

        await Assert.That(() => limiter.AttemptAcquire()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task FixedWindowFallsBackOnRedis74AndExpiresWindow()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "fixed", permitLimit: 1, TimeSpan.FromSeconds(2));

        using var recovered = await limiter.AcquireAsync(1);
        await Assert.That(recovered.IsAcquired).IsTrue();
        using var denied = await limiter.AcquireAsync(1);
        await Assert.That(denied.IsAcquired).IsFalse();
        await Task.Delay(2100);
        using var acquired = await limiter.AcquireAsync(1);
        await Assert.That(acquired.IsAcquired).IsTrue();
    }

    [Test]
    public async Task FixedWindowSerializesContentionAcrossPrefixedClientsAndBinaryKey()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var firstClient = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var secondClient = await RespireClient.ConnectAsync(fixture.CreateOptions());
        RespireKey key = new byte[] { 0xff, 0x00, 0x42 };
        await using var first = new RespireCoordination(firstClient.WithKeyPrefix("tenant:")).RateLimiters.FixedWindow(
            key, permitLimit: 3, TimeSpan.FromSeconds(10));
        await using var second = new RespireCoordination(secondClient.WithKeyPrefix("tenant:")).RateLimiters.FixedWindow(
            key, permitLimit: 3, TimeSpan.FromSeconds(10));

        var acquisitions = Enumerable.Range(0, 20).Select(async index =>
        {
            using var lease = await (index % 2 == 0 ? first : second).AcquireAsync(1);
            return lease.IsAcquired;
        });
        var outcomes = await Task.WhenAll(acquisitions);
        await Assert.That(outcomes.Count(acquired => acquired)).IsEqualTo(3);
    }

    [Test]
    public async Task TokenBucketRunsAtomicallyOnClusterSlotOwner()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with
        {
            Protocol = RespProtocol.Resp3,
            Connections = 1,
        });
        await using var limiter = new RespireCoordination(client).RateLimiters.TokenBucket(
            "{rate-limit}:bucket", tokenLimit: 1, tokensPerPeriod: 1,
            replenishmentPeriod: TimeSpan.FromMilliseconds(250));

        using var first = await limiter.AcquireAsync(1);
        await Task.Delay(300);
        using var second = await limiter.AcquireAsync(1);
        await Assert.That(first.IsAcquired).IsTrue();
        await Assert.That(second.IsAcquired).IsTrue();
    }

    [Test]
    public async Task FixedWindowStateSurvivesSentinelPrimaryFailover()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Sentinel });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var sentinel = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [fixture.SentinelEndpoints[0]],
            Protocol = RespProtocol.Resp2,
        });
        await using var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "failover", permitLimit: 1, TimeSpan.FromSeconds(15));
        using var acquired = await limiter.AcquireAsync(1);
        await Assert.That(acquired.IsAcquired).IsTrue();

        using (var failover = await sentinel.ExecuteAsync(
            RespireCommands.Sentinel.SENTINEL_FAILOVER, RespireContainerFixture.SentinelServiceName))
            await Assert.That(failover.AsString()).IsEqualTo("OK");
        var oldPort = fixture.DataEndpoints[0].Port;
        var newPort = oldPort;
        for (var attempt = 0; attempt < 100 && newPort == oldPort; attempt++)
        {
            await Task.Delay(100);
            using var response = await sentinel.ExecuteAsync(
                RespireCommands.Sentinel.SENTINEL_GET_MASTER_ADDR_BY_NAME,
                RespireContainerFixture.SentinelServiceName);
            newPort = int.Parse(response[1].AsString(), System.Globalization.CultureInfo.InvariantCulture);
        }
        await Assert.That(newPort != oldPort).IsTrue();

        await using var afterFailover = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiterAfterFailover = new RespireCoordination(afterFailover).RateLimiters.FixedWindow(
            "failover", permitLimit: 1, TimeSpan.FromSeconds(15));
        using var denied = await limiterAfterFailover.AcquireAsync(1);
        await Assert.That(denied.IsAcquired).IsFalse();
    }

    [Test]
    public async Task SlidingWindowExpiresOldestSegmentAndHonorsQueueCancellation()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.SlidingWindow(
            "sliding", permitLimit: 1, TimeSpan.FromMilliseconds(200), segments: 2,
            queueLimit: 1, QueueProcessingOrder.OldestFirst);

        using var acquired = await limiter.AcquireAsync(1);
        await Assert.That(acquired.IsAcquired).IsTrue();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.That(async () => await limiter.AcquireAsync(1, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Task.Delay(210);
        await Assert.That((await limiter.AcquireAsync(1)).IsAcquired).IsTrue();
    }

    [Test]
    public async Task TokenBucketRefillsFromRedisTimeAndWaitsForQueuedPermit()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.TokenBucket(
            "bucket", tokenLimit: 2, tokensPerPeriod: 1, TimeSpan.FromMilliseconds(100), queueLimit: 1);

        using var acquired = await limiter.AcquireAsync(2);
        await Assert.That(acquired.IsAcquired).IsTrue();
        var waiting = limiter.AcquireAsync(1).AsTask();
        using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(lease.IsAcquired).IsTrue();
    }

    [Test]
    public async Task NewestFirstQueueDropsOldestWaiter()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        await using var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "newest", permitLimit: 1, window: TimeSpan.FromSeconds(2), queueLimit: 1,
            queueProcessingOrder: QueueProcessingOrder.NewestFirst);
        using var initial = await limiter.AcquireAsync(1);
        await Assert.That(initial.IsAcquired).IsTrue();

        var oldest = limiter.AcquireAsync(1).AsTask();
        await Task.Delay(20);
        var newest = limiter.AcquireAsync(1).AsTask();
        using var evicted = await oldest.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.That(evicted.IsAcquired).IsFalse();
        using var granted = await newest.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(granted.IsAcquired).IsTrue();
    }

    [Test]
    public async Task DisposingLimiterFailsQueuedAcquisition()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new() { Image = "redis:7.4-alpine" });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
        var limiter = new RespireCoordination(client).RateLimiters.FixedWindow(
            "dispose", permitLimit: 1, window: TimeSpan.FromSeconds(10), queueLimit: 1);
        using var initial = await limiter.AcquireAsync(1);
        var queued = limiter.AcquireAsync(1).AsTask();

        await limiter.DisposeAsync();
        await Assert.That(async () => await queued).Throws<ObjectDisposedException>();
    }
}
