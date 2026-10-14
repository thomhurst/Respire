using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class RateLimiterMetricsTests
{
    [Test]
    [NotInParallel]
    public async Task ThrowingListenerCannotChangeAdmissionOutcomes()
    {
        var callbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name == "Respire.Coordination.RateLimiting") target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref callbacks);
            throw new InvalidOperationException("Exporter failed");
        });
        listener.Start();
        await using var server = new FakeRespServer(
            "*3\r\n:1\r\n:0\r\n:0\r\n"u8.ToArray(),
            "*3\r\n:0\r\n:60000\r\n:0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var limiter = client.Coordination.RateLimiters
            .Composite("limit", [RespireRateLimit.FixedWindow(1, TimeSpan.FromMinutes(1))]);
        using var allowed = await limiter.AcquireAsync();
        using var capacity = await limiter.AcquireAsync();
        await Assert.That(allowed.IsAcquired).IsTrue();
        await Assert.That(capacity.IsAcquired).IsFalse();
        await Assert.That(limiter.GetStatistics()!.TotalSuccessfulLeases).IsEqualTo(1);
        await Assert.That(limiter.GetStatistics()!.TotalFailedLeases).IsEqualTo(1);

        var list = new RespireRateLimitDenyList((_, _) => ValueTask.FromResult(true),
            (_, _, _) => ValueTask.CompletedTask, TimeProvider.System, TimeSpan.Zero, 1);
        await using var deniedLimiter = client.Coordination.RateLimiters.WithDenyList(list, "a")
            .Gcra("denied", 1, TimeSpan.FromMinutes(1));
        using var denied = await deniedLimiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();
        await Assert.That(denied.TryGetMetadata(MetadataName.ReasonPhrase, out string? reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("deny_list");
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        await Assert.That(callbacks).IsEqualTo(3);
    }

    [Test]
    [NotInParallel]
    public async Task FinalOutcomesCountOnceIncludingQueueRetriesAndConcurrentRequests()
    {
        var measurements = new ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name == "Respire.Coordination.RateLimiting") target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        await using var server = new FakeRespServer(
            "*3\r\n:1\r\n:0\r\n:2\r\n"u8.ToArray(),
            "*3\r\n:0\r\n:1\r\n:0\r\n"u8.ToArray(),
            "*3\r\n:1\r\n:0\r\n:2\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var factory = client.Coordination.RateLimiters;
        await using var limiter = factory.Composite("private-key", [RespireRateLimit.FixedWindow(3, TimeSpan.FromMinutes(1))], 1);
        using var sync = limiter.AttemptAcquire();
        using var allowed = await limiter.AcquireAsync(2);
        using var queued = await limiter.AcquireAsync();
        using var oversized = await limiter.AcquireAsync(4);
        await Assert.That(allowed.IsAcquired && queued.IsAcquired).IsTrue();
        await Assert.That(oversized.IsAcquired).IsFalse();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(3);

        var deniedList = new RespireRateLimitDenyList((_, _) => ValueTask.FromResult(true),
            (_, _, _) => ValueTask.CompletedTask, TimeProvider.System, TimeSpan.Zero, 1);
        await using var deniedLimiter = factory.WithDenyList(deniedList, "private-identity")
            .Gcra("other-private-key", 1, TimeSpan.FromMinutes(1));
        using var denied = await deniedLimiter.AcquireAsync();
        await Assert.That(denied.IsAcquired).IsFalse();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            using var lease = await limiter.AcquireAsync(0);
            await Assert.That(lease.IsAcquired).IsTrue();
        }));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.That(async () => await limiter.AcquireAsync(1, cancellation.Token)).Throws<OperationCanceledException>();
        using var pendingCancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReplyOverride = (_, _) =>
        {
            received.TrySetResult();
            return "*3\r\n:0\r\n:60000\r\n:0\r\n"u8.ToArray();
        };
        var cancelledRequest = limiter.AcquireAsync(1, pendingCancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingCancellation.CancelAsync();
        await Assert.That(async () => await cancelledRequest).Throws<OperationCanceledException>();
        server.ReplyOverride = (_, _) => "-ERR unavailable\r\n"u8.ToArray();
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<RespireException>();
        await limiter.DisposeAsync();
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<ObjectDisposedException>();

        var allowedMeasurements = measurements.Where(item => item.Name == "respire.rate_limit.allowed").ToArray();
        var blockedMeasurements = measurements.Where(item => item.Name == "respire.rate_limit.blocked").ToArray();
        await Assert.That(allowedMeasurements.Sum(item => item.Value)).IsEqualTo(34);
        await Assert.That(allowedMeasurements.All(item => item.Tags.Length == 0)).IsTrue();
        await Assert.That(blockedMeasurements.Sum(item => item.Value)).IsEqualTo(2);
        await Assert.That(blockedMeasurements.All(item => item.Tags.Length == 1 && item.Tags[0].Key == "reason")).IsTrue();
        await Assert.That(blockedMeasurements.Count(item => Equals(item.Tags[0].Value, "deny_list"))).IsEqualTo(1);
        await Assert.That(blockedMeasurements.Count(item => Equals(item.Tags[0].Value, "capacity"))).IsEqualTo(1);
    }

    [Test]
    public async Task QueuedRetryRechecksDenyListWithoutConsumingCapacity()
    {
        await using var server = new FakeRespServer("*3\r\n:0\r\n:1\r\n:0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var reads = 0;
        var list = new RespireRateLimitDenyList((_, _) => ValueTask.FromResult(Interlocked.Increment(ref reads) > 1),
            (_, _, _) => ValueTask.CompletedTask, TimeProvider.System, TimeSpan.Zero, 1);
        await using var limiter = client.Coordination.RateLimiters.WithDenyList(list, "a")
            .Composite("limit", [RespireRateLimit.FixedWindow(1, TimeSpan.FromMinutes(1))], 1);
        using var result = await limiter.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.IsAcquired).IsFalse();
        await Assert.That(result.TryGetMetadata(MetadataName.ReasonPhrase, out string? reason)).IsTrue();
        await Assert.That(reason).IsEqualTo("deny_list");
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
        await Assert.That(limiter.GetStatistics()!.CurrentQueuedCount).IsEqualTo(0);
        await Assert.That(limiter.GetStatistics()!.TotalFailedLeases).IsEqualTo(1);
    }

    [Test]
    public async Task DenyListReadFailurePreventsAdmission()
    {
        await using var server = new FakeRespServer("*3\r\n:1\r\n:0\r\n:0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var list = new RespireRateLimitDenyList((_, _) => ValueTask.FromException<bool>(new IOException("offline")),
            (_, _, _) => ValueTask.CompletedTask, TimeProvider.System, TimeSpan.Zero, 1);
        await using var limiter = client.Coordination.RateLimiters.WithDenyList(list, "a")
            .Composite("limit", [RespireRateLimit.FixedWindow(1, TimeSpan.FromMinutes(1))]);
        await Assert.That(async () => await limiter.AcquireAsync()).Throws<IOException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);
        await Assert.That(limiter.GetStatistics()!.TotalFailedLeases).IsEqualTo(0);
    }
}
