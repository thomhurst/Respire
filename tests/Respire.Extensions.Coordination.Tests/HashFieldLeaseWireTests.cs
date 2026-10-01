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
    public async Task LeaseEstimateStartsAfterCorrectionOrderingBootstrap()
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray());
        server.DelayReply(0, 250);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 2,
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

        await using var lease = await (new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromMilliseconds(100))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("CLIENT ID", StringComparison.Ordinal))).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lease.RemainingEstimate > TimeSpan.Zero).IsTrue();
    }

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
        var acquireIndex = commands.ToList().FindIndex(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal));
        var cleanupIndex = commands.ToList().FindIndex(command => command.StartsWith("EVAL ", StringComparison.Ordinal));
        await Assert.That(acquireIndex >= 0 && cleanupIndex > acquireIndex).IsTrue();
        var owner = server.ReceivedArguments[acquireIndex][5];
        await Assert.That(server.ReceivedArguments[cleanupIndex][5]).IsEquivalentTo(owner);
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
        server.DelayReply(3, 500);
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
    public async Task UncertainRenewalFailsClosedAndCannotBeRetried()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(3, 250);
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
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(lease.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    public async Task FailedQueuedReleasePreservesUncertainRenewalState()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            "-ERR release failed\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(3, 250);
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
        await Assert.That(await lease.ResetExpiryAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(lease.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(3);
    }

    [Test]
    [NotInParallel]
    public async Task QueuedReleaseDeadlineIncludesRenewalWait()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(3, 2500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");

        var renewal = lease.ResetExpiryAsync(TimeSpan.FromSeconds(20)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(5, timeout.Token);
        var release = lease.ReleaseAsync().AsTask();
        await Assert.That(async () => await release.WaitAsync(TimeSpan.FromSeconds(4)))
            .Throws<OperationCanceledException>();
        await Assert.That(renewal.IsCompleted).IsFalse();
        await Assert.That(await renewal.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(await lease.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(await lease.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.AlreadyReleased);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(3);
    }

    [Test]
    public async Task SharedReleaseIsBoundedWhenCommandTimeoutIsDisabled()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");
        server.SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal);

        var started = Stopwatch.GetTimestamp();
        await Assert.That(async () => await lease.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)))
            .Throws<OperationCanceledException>();

        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4)).IsTrue();
        await Assert.That(lease.IsReleased).IsTrue();
        server.SuppressReply = null;
    }

    [Test]
    [NotInParallel]
    public async Task QueuedReleaseDeadlineIncludesAnUnresponsiveRenewal()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        await using var lease = await new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("Expected lease acquisition.");
        server.SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal);

        var renewal = lease.ResetExpiryAsync(TimeSpan.FromSeconds(30)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)) < 2)
            await Task.Delay(5, timeout.Token);

        var started = Stopwatch.GetTimestamp();
        await Assert.That(async () => await lease.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)))
            .Throws<OperationCanceledException>();
        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4)).IsTrue();
        await Assert.That(lease.IsReleased).IsTrue();

        await client.DisposeAsync();
        try { await renewal.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
    }

    [Test]
    public async Task CancellationBeforeAcquireReplyAttemptsOwnerCheckedCleanup()
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        server.DelayReply(2, 250);
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
        var commands = server.ReceivedCommands;
        var acquireIndex = commands.ToList().FindIndex(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal));
        var cleanupIndex = commands.ToList().FindIndex(command => command.StartsWith("EVAL ", StringComparison.Ordinal));
        await Assert.That(acquireIndex >= 0 && cleanupIndex > acquireIndex).IsTrue();
        var owner = server.ReceivedArguments[acquireIndex][5];
        await Assert.That(server.ReceivedArguments[cleanupIndex][5]).IsEquivalentTo(owner);
    }

    [Test]
    [NotInParallel]
    public async Task UncertainAcquisitionCorrectionFollowsEveryPossibleConnectionCopy()
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 2,
        });
        using var cancellation = new CancellationTokenSource();
        var pending = new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            await Task.Delay(5, timeout.Token);
        var acquireIndex = server.ReceivedCommands.ToList().FindIndex(
            command => command.StartsWith("EVALSHA ", StringComparison.Ordinal));
        var acquisitionConnection = server.ReceivedConnectionIds[acquireIndex];
        cancellation.Cancel();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        var cleanupConnections = server.ReceivedCommands
            .Select((command, index) => (command, ConnectionId: server.ReceivedConnectionIds[index]))
            .Where(item => item.command.StartsWith("EVAL ", StringComparison.Ordinal))
            .Select(item => item.ConnectionId)
            .Distinct()
            .Order()
            .ToArray();
        await Assert.That(cleanupConnections).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(cleanupConnections.Contains(acquisitionConnection)).IsTrue();
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
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
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
