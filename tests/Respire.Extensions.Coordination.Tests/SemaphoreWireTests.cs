using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Coordination.Tests;

public class SemaphoreWireTests
{
    // The default command timeout makes acquisition capture CLIENT ID and validate CLIENT KILL
    // permission before its first tracked script.
    internal static readonly byte[] ClientIdReply = ":7\r\n"u8.ToArray();
    internal static readonly byte[] ClientKillReply = ":0\r\n"u8.ToArray();

    internal static string[] EvalCommands(FakeRespServer server)
        => server.ReceivedCommands.Where(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)).ToArray();

    [Test]
    [NotInParallel]
    public async Task FailedReleaseRemainsRetryableForNonExpiringPermit()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
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
        await Assert.That(EvalCommands(server).Length).IsEqualTo(4);
    }

    [Test]
    [NotInParallel]
    public async Task FailedRenewalCleanupLeavesPermitRetryable()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
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
        await Assert.That(EvalCommands(server).Length).IsEqualTo(4);
    }

    [Test]
    [NotInParallel]
    public async Task ElapsedConfirmedRenewalUpdatesLocalExpiryWhenCleanupFails()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            ":1\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            "-ERR cleanup failed\r\n"u8.ToArray());
        server.DelayReply(3, 50);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "{renew}:elapsed", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();
        await Assert.That(attempt.Permit.Expiry).IsNull();

        await Assert.That(await attempt.Permit.ResetExpiryAsync(TimeSpan.FromMilliseconds(1))).IsFalse();

        await Assert.That(attempt.Permit.Expiry).IsEqualTo(TimeSpan.FromMilliseconds(1));
        await Assert.That(attempt.Permit.RemainingEstimate).IsEqualTo(TimeSpan.Zero);
        await Assert.That(attempt.Permit.IsReleased).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task UnansweredFenceDoesNotDelayCanceledAcquisition()
    {
        // Suppresses the acquire and the correction's CLIENT KILL barrier (but not the SKIPME
        // permission probe), so the ordered cleanup can never complete.
        await using var server = new FakeRespServer(3, ":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                || (command.StartsWith("CLIENT KILL ", StringComparison.OrdinalIgnoreCase)
                    && !command.Contains("SKIPME", StringComparison.OrdinalIgnoreCase)),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var semaphore = new RespireSemaphore(client, "{retry}:fence", capacity: 1);

        var started = Stopwatch.GetTimestamp();
        await Assert.That(async () => await semaphore.TryAcquireAsync(cancellationToken: cancellation.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();
        await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3)).IsTrue();
        // The release must never overtake the unacknowledged barrier.
        await Assert.That(EvalCommands(server).Length).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task TransientFenceFailureIsRetriedBeforeCleanup()
    {
        static bool IsFence(string command)
            => command.StartsWith("CLIENT KILL ", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("SKIPME", StringComparison.OrdinalIgnoreCase);

        var evalCount = 0;
        var fenceCount = 0;
        await using var server = new FakeRespServer(4, ":1\r\n"u8.ToArray())
        {
            // Parks the acquire; every later script (the cleanup release) is answered.
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && Interlocked.Increment(ref evalCount) == 1,
            ReplyOverride = (_, command) => IsFence(command) && Interlocked.Increment(ref fenceCount) == 1
                ? "-BUSY Redis is busy running a script\r\n"u8.ToArray()
                : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var semaphore = new RespireSemaphore(client, "{retry}:transient-fence", capacity: 1);

        await Assert.That(async () => await semaphore.TryAcquireAsync(cancellationToken: cancellation.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (EvalCommands(server).Length < 2) await Task.Delay(10, deadline.Token);
        var commands = server.ReceivedCommands.ToList();
        var fences = commands.Select((command, index) => (command, index))
            .Where(entry => IsFence(entry.command)).Select(entry => entry.index).ToList();
        var release = commands.FindLastIndex(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal));
        // The refused barrier is retried, and the release is sent only after it is acknowledged.
        await Assert.That(fences.Count).IsEqualTo(2);
        await Assert.That(release).IsGreaterThan(fences[1]);
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

    [Test]
    [NotInParallel]
    public async Task DisposalReleasesPermitWithoutWaitingForBusyRenewal()
    {
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evalCount = 0;
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                var call = Interlocked.Increment(ref evalCount);
                if (call == 2) renewalStarted.TrySetResult();
                return call == 2;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var permit = (await new RespireSemaphore(client, "{dispose}:semaphore", capacity: 1).TryAcquireAsync()).Permit;
        var renewal = permit.ResetExpiryAsync(TimeSpan.FromSeconds(10)).AsTask();
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await permit.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var renewalCommand = server.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && command.Contains("semaphore", StringComparison.Ordinal));
        await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds.ToList()[renewalCommand]);
        await Assert.That(await renewal.WaitAsync(TimeSpan.FromSeconds(2))).IsFalse();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (Volatile.Read(ref evalCount) < 3) await Task.Delay(10, deadline.Token);
        await Assert.That(server.ReceivedCommands.Count).IsGreaterThanOrEqualTo(3);
        await server.SendRawAsync(":1\r\n"u8.ToArray(), server.ReceivedConnectionIds.ToList()[^1]);
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!permit.IsReleased) await Task.Delay(10, releaseDeadline.Token);
    }
}
