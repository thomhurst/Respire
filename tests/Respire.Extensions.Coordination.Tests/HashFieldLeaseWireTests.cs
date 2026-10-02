using System.Diagnostics;
using System.Text;
using Respire.Internal;
using Respire.Networking;
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
            "-ERR release failed\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
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
        await Assert.That(await lease.ReleaseAsync()).IsEqualTo(LockReleaseOutcome.Released);
        await Assert.That(lease.IsReleased).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            .IsEqualTo(4);
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
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ReceivedCommands.Count(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)) < 3)
            await Task.Delay(5, releaseDeadline.Token);
        var outcome = await lease.ReleaseAsync();
        await Assert.That(outcome is LockReleaseOutcome.Released or LockReleaseOutcome.AlreadyReleased).IsTrue();
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
        while (!server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            await Task.Delay(5, timeout.Token);
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
    public async Task CancellationDuringCorrectionOrderingBootstrapSkipsLeaseCleanup()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("CLIENT ID", StringComparison.Ordinal),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource();
        var pending = new RespireCoordination(client)
            .TryAcquireLeaseAsync("registry", "worker", TimeSpan.FromSeconds(30), cancellation.Token).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Any(command => command.StartsWith("CLIENT ID", StringComparison.Ordinal)))
            await Task.Delay(5, timeout.Token);
        cancellation.Cancel();

        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsFalse();
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
    [NotInParallel]
    public async Task UncertainAcquisitionCleanupAlsoTargetsPromotedSentinelPrimary()
    {
        await using var oldPrimary = new FakeRespServer(8, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()
                : null,
        };
        await using var promotedPrimary = new FakeRespServer(8, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()
                : null,
        };
        var primaryPort = oldPrimary.Port;
        await using var sentinel = new FakeRespServer(8, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primaryPort.ToString().Length}\r\n{primaryPort}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            CommandTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["acquire"], [], default, requireReliableCorrectionOrdering: true);
        using (var response = await execution.Response) await Assert.That(response.AsInteger()).IsEqualTo(1);

        primaryPort = promotedPrimary.Port;
        oldPrimary.CloseConnections();
        using var promotionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Endpoint.Port != promotedPrimary.Port)
        {
            try
            {
                await client.PingAsync().AsTask().WaitAsync(promotionTimeout.Token);
            }
            catch (Exception error) when (
                error is RespireConnectionException or RespireConnectionRetiredException
                && !promotionTimeout.IsCancellationRequested)
            {
                await Task.Delay(10, promotionTimeout.Token);
            }
        }
        await Assert.That(client.Endpoint.Port).IsEqualTo(promotedPrimary.Port);
        await new RespireCoordination(client).BestEffortReleaseHashFieldLeaseAsync(
            "registry", "worker", RespireLock.NewToken(), client, execution.ConnectionIdentity);

        await Assert.That(promotedPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task UncertainAcquisitionCleanupAlsoTargetsCurrentClusterOwner()
    {
        await using var currentOwner = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? "*0\r\n"u8.ToArray()
                : command.StartsWith("EVAL ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray()
                : command == "GET registry" ? "$-1\r\n"u8.ToArray()
                : null,
        };
        var slot = ClusterHash.GetSlot("registry");
        await using var oldOwner = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? "*0\r\n"u8.ToArray()
                : command.StartsWith("GET registry", StringComparison.Ordinal)
                    || command.StartsWith("EVAL ", StringComparison.Ordinal)
                    ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{currentOwner.Port}\r\n")
                    : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = [new("127.0.0.1", oldOwner.Port)],
            CommandTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["registry"], [], default, requireReliableCorrectionOrdering: true);
        using (var response = await execution.Response) await Assert.That(response.AsInteger()).IsEqualTo(1);
        await client.GetStringAsync("registry");
        oldOwner.SuppressReply = command => command.StartsWith("EVAL ", StringComparison.Ordinal);
        await new RespireCoordination(client).BestEffortReleaseHashFieldLeaseAsync(
            "registry", "worker", RespireLock.NewToken(), client, execution.ConnectionIdentity);

        await Assert.That(oldOwner.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(currentOwner.ReceivedCommands.Any(command => command.StartsWith("EVAL", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [NotInParallel]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UncertainCleanupFollowsNewClusterOwnerWhileCurrentOwnerReleaseIsPending(bool originalCorrectionStalls)
    {
        static bool IsEval(string command) => command.StartsWith("EVAL", StringComparison.Ordinal);
        await using var replacementOwner = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var stalledOwner = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? "*0\r\n"u8.ToArray()
                : command == "GET registry" ? "$-1\r\n"u8.ToArray()
                : null,
            SuppressReply = IsEval,
        };
        var slot = ClusterHash.GetSlot("registry");
        await using var oldOwner = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
                ? "*0\r\n"u8.ToArray()
                : command.StartsWith("GET registry", StringComparison.Ordinal)
                    || command.StartsWith("EVAL ", StringComparison.Ordinal)
                    ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{stalledOwner.Port}\r\n")
                    : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            Endpoints = [new("127.0.0.1", oldOwner.Port)],
            CommandTimeout = null,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["registry"], [], default, requireReliableCorrectionOrdering: true);
        using (var response = await execution.Response) await Assert.That(response.AsInteger()).IsEqualTo(1);
        await client.GetStringAsync("registry");
        // When the original correction is answered, monitoring must still continue for the stalled owner.
        if (originalCorrectionStalls)
            oldOwner.SuppressReply = command => command.StartsWith("EVAL ", StringComparison.Ordinal);

        var cleanup = new RespireCoordination(client).BestEffortReleaseHashFieldLeaseAsync(
            "registry", "worker", RespireLock.NewToken(), client, execution.ConnectionIdentity).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!stalledOwner.ReceivedCommands.Any(IsEval))
            await Task.Delay(5, timeout.Token);

        var router = client.Core.Cluster!;
        router.SetSlotOwner(slot, router.GetMultiplexer(new RespireEndpoint("127.0.0.1", replacementOwner.Port)));
        while (!replacementOwner.ReceivedCommands.Any(IsEval))
            await Task.Delay(5, timeout.Token);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(replacementOwner.ReceivedCommands.Any(IsEval)).IsTrue();
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UncertainCleanupRechecksSentinelWhileOldPrimaryCorrectionIsPending(bool promotedReleaseStalls)
    {
        await using var oldPrimary = new FakeRespServer(8, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()
                : null,
        };
        await using var promotedPrimary = new FakeRespServer(8, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()
                : null,
        };
        await using var secondPromotedPrimary = new FakeRespServer(8, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()
                : null,
        };
        var primaryPort = oldPrimary.Port;
        await using var sentinel = new FakeRespServer(8, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${Volatile.Read(ref primaryPort).ToString().Length}\r\n{Volatile.Read(ref primaryPort)}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            // A stalled release to one promoted primary must not hide a later promotion.
            CommandTimeout = promotedReleaseStalls ? null : TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });

        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["acquire"], [], default, requireReliableCorrectionOrdering: true);
        using (var response = await execution.Response) await Assert.That(response.AsInteger()).IsEqualTo(1);
        oldPrimary.SuppressReply = command => command.StartsWith("EVAL ", StringComparison.Ordinal);
        if (promotedReleaseStalls)
            promotedPrimary.SuppressReply = command => command.StartsWith("EVAL ", StringComparison.Ordinal);

        var cleanup = new RespireCoordination(client).BestEffortReleaseHashFieldLeaseAsync(
            "registry", "worker", RespireLock.NewToken(), client, execution.ConnectionIdentity).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!oldPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal)))
            await Task.Delay(5, timeout.Token);

        Volatile.Write(ref primaryPort, promotedPrimary.Port);
        var generation = client.Core.Sentinel!.Current!;
        using var rejection = Respire.Protocol.RespValue.Error("READONLY replica");
        generation.ObserveResponse(generation.Multiplexer.GetConnection(), "SET", in rejection);
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var promotedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!promotedPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal)))
            await Task.Delay(5, promotedTimeout.Token);

        Volatile.Write(ref primaryPort, secondPromotedPrimary.Port);
        var secondGeneration = client.Core.Sentinel!.Current!;
        using var secondRejection = Respire.Protocol.RespValue.Error("READONLY replica");
        secondGeneration.ObserveResponse(secondGeneration.Multiplexer.GetConnection(), "SET", in secondRejection);
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        while (!secondPromotedPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal)))
            await Task.Delay(5, promotedTimeout.Token);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(promotedPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(secondPromotedPrimary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(cleanup.IsCompleted).IsTrue();
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
        while (!server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)))
            await Task.Delay(5, timeout.Token);
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
