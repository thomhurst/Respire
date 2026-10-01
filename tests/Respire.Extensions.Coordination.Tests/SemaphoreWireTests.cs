using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class SemaphoreWireTests
{
    [Test]
    [NotInParallel]
    public async Task FailedReleaseRemainsRetryableForNonExpiringPermit()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(),
            "-ERR release failed\r\n"u8.ToArray(),
            "-ERR cleanup failed\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "{retry}:semaphore", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();

        await Assert.That(async () => await attempt.Permit.ReleaseAsync()).Throws<RespireServerException>();
        await Assert.That(attempt.Permit.IsReleased).IsFalse();
        await Assert.That(await attempt.Permit.ReleaseAsync()).IsTrue();
        await Assert.That(attempt.Permit.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(4);
    }

    [Test]
    [NotInParallel]
    public async Task FailedRenewalCleanupLeavesPermitRetryable()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(),
            "-ERR renewal failed\r\n"u8.ToArray(),
            "-ERR cleanup failed\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "{retry}:renew", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();

        await Assert.That(async () => await attempt.Permit.ResetExpiryAsync(null)).Throws<RespireServerException>();
        await Assert.That(attempt.Permit.IsReleased).IsFalse();
        await Assert.That(await attempt.Permit.ReleaseAsync()).IsTrue();
        await Assert.That(attempt.Permit.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(4);
    }

    [Test]
    [NotInParallel]
    public async Task UncertainAcquisitionCleanupHasIndependentBound()
    {
        await using var server = new FakeRespServer(3, ":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var semaphore = new RespireSemaphore(client, "{retry}:bounded", capacity: 1);

        await Assert.That(async () => await semaphore.TryAcquireAsync(cancellationToken: cancellation.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    [NotInParallel]
    public async Task PostReplyCancellationCleanupHasIndependentBound()
    {
        var evalCount = 0;
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && Interlocked.Increment(ref evalCount) >= 2,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is "EVALSHA"
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);

        var started = Stopwatch.GetTimestamp();
        await Assert.That(async () => await new RespireSemaphore(client, "{retry}:post-reply", capacity: 1)
            .TryAcquireAsync(cancellationToken: cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();
        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }
}
