using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class HashFieldLeaseWireTests
{
    [Test]
    [NotInParallel]
    public async Task CancellationAfterSuccessfulReplyReleasesTheLease()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is string operation
                    && string.Equals(operation, "EVALSHA", StringComparison.Ordinal)
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);

        await Assert.That(async () => await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30), cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();

        var commands = server.ReceivedCommands;
        await Assert.That(commands.Count).IsEqualTo(2);
        await Assert.That(commands.All(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsTrue();
        var owner = server.ReceivedArguments[0][5];
        await Assert.That(server.ReceivedArguments[1][5]).IsEquivalentTo(owner);
        await Assert.That(server.ReceivedArguments[0][1]).IsNotEqualTo(server.ReceivedArguments[1][1]);
    }

    [Test]
    public async Task RenewalCanAskRedisAfterTheLocalEstimateElapsed()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(1))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(lease.RemainingEstimate > TimeSpan.Zero).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task ConcurrentReleaseCallersShareTheServerOperationButNotCallerCancellation()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(1, 500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        using var firstCancellation = new CancellationTokenSource();
        var first = lease.ReleaseAsync(firstCancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(5, timeout.Token);
        var second = lease.ReleaseAsync().AsTask();
        firstCancellation.Cancel();

        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task UncertainRenewalFailsClosedAndCanBeRetriedAgainstRedis()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(1, 250);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        using var cancellation = new CancellationTokenSource();
        var renewal = lease.ResetExpiryAsync(TimeSpan.FromSeconds(1), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(5, timeout.Token);
        await Task.Delay(50, timeout.Token);
        cancellation.Cancel();

        await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
        await Assert.That(lease.IsReleased).IsTrue();
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(lease.IsReleased).IsFalse();
    }

    [Test]
    public async Task FailedQueuedReleasePreservesUncertainRenewalState()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), "-ERR release failed\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(1, 250);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        using var cancellation = new CancellationTokenSource();
        var renewal = lease.ResetExpiryAsync(TimeSpan.FromSeconds(1), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(5, timeout.Token);
        var release = lease.ReleaseAsync().AsTask();
        cancellation.Cancel();

        await Assert.That(async () => await renewal).Throws<OperationCanceledException>();
        await Assert.That(async () => await release).Throws<RespireServerException>();
        await Assert.That(lease.IsReleased).IsTrue();
        await Assert.That(lease.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(lease.IsReleased).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(4);
    }

    [Test]
    public async Task CancellationBeforeAcquireReplyAttemptsOwnerCheckedCleanup()
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(0, 250);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(5, timeout.Token);
        await Task.Delay(50, timeout.Token);
        cancellation.Cancel();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
        var owner = server.ReceivedArguments[0][5];
        await Assert.That(server.ReceivedArguments[1][5]).IsEquivalentTo(owner);
    }

    [Test]
    public async Task BestEffortAcquireCleanupIsBoundedWhenRedisDoesNotReply()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var pending = new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0) await Task.Delay(5, timeout.Token);
        cancellation.Cancel();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task EmptyHashKeyOrLeaseFieldIsRejectedBeforeConnecting()
    {
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        var coordination = new RespireCoordination(client);
        await Assert.That(async () => await coordination.TryAcquireLeaseAsync(
            RespireKey.Empty, "worker", TimeSpan.FromSeconds(1))).Throws<ArgumentException>();
        await Assert.That(async () => await coordination.TryAcquireLeaseAsync(
            "registry", RespireKey.Empty, TimeSpan.FromSeconds(1))).Throws<ArgumentException>();
    }
}
