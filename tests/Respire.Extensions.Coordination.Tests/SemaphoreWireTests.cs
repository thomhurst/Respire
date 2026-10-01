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
    public async Task RenewalReplyOfZeroMarksPermitReleasedWithoutCleanup()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            ":1\r\n"u8.ToArray(),
            ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "{renew}:lost", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();

        await Assert.That(await attempt.Permit.ResetExpiryAsync(null)).IsFalse();

        // Redis proved the permit is gone, so no release is needed to mark it lost.
        await Assert.That(attempt.Permit.IsReleased).IsTrue();
        await Assert.That(EvalCommands(server).Length).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    public async Task CancellableRenewalDoesNotRequireClientFencePermissions()
    {
        await using var server = new FakeRespServer(20);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" => "-NOPERM client identity denied\r\n"u8.ToArray(),
            _ when command.StartsWith("CLIENT KILL", StringComparison.Ordinal) => "-NOPERM client kill denied\r\n"u8.ToArray(),
            _ when command.StartsWith("EVALSHA ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            CommandTimeout = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var semaphore = new RespireSemaphore(client, "{renew}:acl", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();
        var clientIdCount = server.ReceivedCommands.Count(command => command == "CLIENT ID");
        using var cancellation = new CancellationTokenSource();

        await Assert.That(await attempt.Permit.ResetExpiryAsync(TimeSpan.FromSeconds(30), cancellation.Token)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT ID")).IsEqualTo(clientIdCount);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("CLIENT KILL", StringComparison.Ordinal))).IsFalse();
        await Assert.That(EvalCommands(server).Length).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    public async Task UncertainRenewalRefusesLaterRenewals()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            ":1\r\n"u8.ToArray(),
            "-ERR cleanup failed\r\n"u8.ToArray());
        var evals = 0;
        // Leave the first renewal unanswered so its outcome is uncertain.
        server.SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
            && Interlocked.Increment(ref evals) == 2;
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var semaphore = new RespireSemaphore(client, "{renew}:uncertain", capacity: 1);
        await using var attempt = await semaphore.TryAcquireAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await attempt.Permit.ResetExpiryAsync(null, cancellation.Token))
            .Throws<OperationCanceledException>();
        var sent = EvalCommands(server).Length;

        // The unanswered renewal may still run later and overwrite any newer score.
        await Assert.That(await attempt.Permit.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsFalse();
        await Assert.That(EvalCommands(server).Length).IsEqualTo(sent);
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
    public async Task NoScriptFallbackStartsAcquisitionExpiryAtFallbackSend()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            "-NOSCRIPT missing script\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(2, 2500);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await using var attempt = await new RespireSemaphore(client, "{retry}:noscript", capacity: 1)
            .TryAcquireAsync(TimeSpan.FromSeconds(2));

        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("EVAL ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task UnansweredFenceDoesNotDelayCanceledAcquisition()
    {
        static bool IsFence(string command)
            => command.StartsWith("CLIENT KILL ", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("SKIPME", StringComparison.OrdinalIgnoreCase);

        var evalCount = 0;
        var fenceCount = 0;
        await using var server = new FakeRespServer(5, ":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    && Interlocked.Increment(ref evalCount) == 1) return true;
                return IsFence(command) && Interlocked.Increment(ref fenceCount) == 1;
            },
            ReplyOverride = (_, command) => IsFence(command) ? ":1\r\n"u8.ToArray() : null,
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

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref evalCount) < 2 || Volatile.Read(ref fenceCount) < 2)
            await Task.Delay(10, deadline.Token);
        var commands = server.ReceivedCommands;
        var fences = commands.Select((command, index) => (command, index))
            .Where(entry => IsFence(entry.command)).Select(entry => entry.index).ToArray();
        var release = commands.Select((command, index) => (command, index))
            .Where(entry => entry.command.StartsWith("EVALSHA ", StringComparison.Ordinal))
            .Select(entry => entry.index).Last();
        await Assert.That(fences.Length).IsEqualTo(2);
        await Assert.That(release).IsGreaterThan(fences[^1]);
    }

    [Test]
    [NotInParallel]
    public async Task FenceRejectionIsRetriedBeforeCleanup()
    {
        static bool IsFence(string command)
            => command.StartsWith("CLIENT KILL ", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("SKIPME", StringComparison.OrdinalIgnoreCase);

        var evalCount = 0;
        var fenceCount = 0;
        using var cancellation = new CancellationTokenSource();
        await using var server = new FakeRespServer(4, ":1\r\n"u8.ToArray())
        {
            // Parks the acquire and cancels it once the server has it; every later script (the
            // cleanup release) is answered.
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    || Interlocked.Increment(ref evalCount) != 1) return false;
                _ = Task.Run(cancellation.Cancel);
                return true;
            },
            ReplyOverride = (_, command) => IsFence(command) && Interlocked.Increment(ref fenceCount) == 1
                ? "-NOPERM ACL changed before cleanup\r\n"u8.ToArray()
                : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        var semaphore = new RespireSemaphore(client, "{retry}:transient-fence", capacity: 1);

        await Assert.That(async () => await semaphore.TryAcquireAsync(cancellationToken: cancellation.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<OperationCanceledException>();

        var waitStarted = Stopwatch.GetTimestamp();
        while (EvalCommands(server).Length < 2 && Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        var commands = server.ReceivedCommands.ToList();
        await Assert.That(EvalCommands(server).Length).IsEqualTo(2)
            .Because(string.Join(" | ", server.ReceivedConnectionIds.Zip(commands,
                (id, command) => $"{id}:{command[..Math.Min(command.Length, 24)]}")));
        var fences = commands.Select((command, index) => (command, index))
            .Where(entry => IsFence(entry.command)).Select(entry => entry.index).ToList();
        var release = commands.FindLastIndex(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal));
        // A refusal does not prove the target connection is gone, so cleanup waits for an acknowledged fence.
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
        await Assert.That(EvalCommands(server).Length).IsGreaterThanOrEqualTo(2);

        // The permit has no expiry, so its release keeps retrying in the background after the
        // caller has seen the cancellation.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (EvalCommands(server).Length < 3) await Task.Delay(10, deadline.Token);
    }

    [Test]
    [NotInParallel]
    public async Task CapacityMismatchSkipsCleanupAndThrowsTypedException()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            "-ERR semaphore capacity cannot change while permits are active\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var error = await Assert.That(async () => await new RespireSemaphore(client, "{capacity}:mismatch", capacity: 2)
                .TryAcquireAsync())
            .Throws<RespireSemaphoreCapacityMismatchException>();

        await Assert.That(error!.RequestedCapacity).IsEqualTo(2);
        await Assert.That(error.InnerException).IsTypeOf<RespireServerException>();
        // The error reply is definite: the script refused before adding a permit.
        await Assert.That(EvalCommands(server).Length).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task OtherAcquireErrorRepliesPropagateWithoutCleanup()
    {
        await using var server = new FakeRespServer(
            ClientIdReply,
            ClientKillReply,
            "-OOM command not allowed when used memory > 'maxmemory'\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var error = await Assert.That(async () => await new RespireSemaphore(client, "{capacity}:oom", capacity: 1)
                .TryAcquireAsync())
            .ThrowsExactly<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo("OOM");
        await Assert.That(EvalCommands(server).Length).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task DisposalCleanupOutlivesTheOldExpiryWhileARenewalIsPending()
    {
        var evalCount = 0;
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            // Answer only the acquire. The renewal and every release stay unanswered.
            SuppressReply = command => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                && Interlocked.Increment(ref evalCount) >= 2,
        };
        var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            CommandTimeout = null,
        });
        try
        {
            var permit = (await new RespireSemaphore(client, "{dispose}:pending-renewal", capacity: 1)
                .TryAcquireAsync(TimeSpan.FromMilliseconds(200))).Permit;
            var renewal = permit.ResetExpiryAsync(null).AsTask();
            using (var sent = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                while (Volatile.Read(ref evalCount) < 2) await Task.Delay(10, sent.Token);

            // Disposal stops waiting for the renewal after one second, when the old 200 ms estimate
            // has run out. Redis may already have made the permit non-expiring, so release is still sent.
            await permit.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            using (var released = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (Volatile.Read(ref evalCount) < 3) await Task.Delay(10, released.Token);
            await Assert.That(renewal.IsCompleted).IsFalse();

            await client.DisposeAsync();
            await Assert.That(async () => await renewal.WaitAsync(TimeSpan.FromSeconds(5))).ThrowsException();
        }
        finally
        {
            await client.DisposeAsync();
        }
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
