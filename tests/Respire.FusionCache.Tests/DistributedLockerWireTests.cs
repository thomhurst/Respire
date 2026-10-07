using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.FusionCache.Tests;

public class DistributedLockerWireTests
{
    [Test]
    [Arguments(0)]
    [Arguments(30)]
    public async Task ImmediateAttemptAndFiniteBudgetDoNotWaitForLongPollInterval(int timeoutMs)
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { PollInterval = TimeSpan.FromSeconds(1) });
        var pending = DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.FromMilliseconds(timeoutMs)).AsTask();
        // The smallest uncapped jittered poll is 900 ms. A finite budget must end before it.
        await Assert.That(await pending.WaitAsync(TimeSpan.FromMilliseconds(500))).IsNull();
        if (timeoutMs == 0)
        {
            await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
            await Assert.That(server.ReceivedCommands[0].StartsWith("EVALSHA ", StringComparison.Ordinal)).IsTrue();
        }
        // Finite cancellation can win before receipt, or after the capped delay permits a
        // further attempt. The command count is not a contract for a positive wait budget.
    }

    [Test]
    public async Task FiniteBudgetCanExpireBeforePeerReadsCommand()
    {
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray()) { ReadGate = readGate.Task };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { PollInterval = TimeSpan.FromSeconds(1) });
        var pending = DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.FromMilliseconds(30)).AsTask();
        await Assert.That(await pending.WaitAsync(TimeSpan.FromMilliseconds(500))).IsNull();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InfiniteContentionEndsOnCancellationOrDisposal(bool disposeLocker)
    {
        var attemptArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal)) attemptArrived.TrySetResult();
                return null;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { PollInterval = TimeSpan.FromSeconds(1) });
        using var cancellation = new CancellationTokenSource();
        var pending = DistributedLockerTests.AcquireAsync(locker, "cache", Timeout.InfiniteTimeSpan, cancellation.Token).AsTask();
        await attemptArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeLocker)
        {
            await locker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<ObjectDisposedException>();
        }
        else
        {
            await cancellation.CancelAsync();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulReleaseIgnoresCancellationBeforeAndDuringCleanup(bool cancelDuringCleanup)
    {
        var releaseArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return false;
                releaseArrived.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        using var cancellation = new CancellationTokenSource();
        if (!cancelDuringCleanup) cancellation.Cancel();
        var release = DistributedLockerTests.ReleaseAsync(locker, "cache", owner, cancellation.Token).AsTask();
        await releaseArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (cancelDuringCleanup) cancellation.Cancel();
            await Assert.That(release.IsCompleted).IsFalse();
        }
        finally { await server.SendRawAsync(":1\r\n"u8.ToArray()); }
        await release.WaitAsync(TimeSpan.FromSeconds(5));
        await DistributedLockerTests.ReleaseAsync(locker, "cache", owner, cancellation.Token);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task LockerDisposalJoinsUnreturnedLeaseCleanup()
    {
        var releaseArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return false;
                releaseArrived.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        Task? disposal = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (disposal is null && activity.GetTagItem("db.operation.name") is "EVALSHA"
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    disposal = locker.DisposeAsync().AsTask();
            },
        };
        ActivitySource.AddActivityListener(listener);
        var pending = DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero).AsTask();
        try { await releaseArrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException(string.Join(" | ", server.ReceivedCommands), error);
        }
        try
        {
            await Assert.That(disposal).IsNotNull();
            await Assert.That(disposal!.IsCompleted).IsFalse();
            await Assert.That(locker.DisposeAsync().AsTask()).IsSameReferenceAs(disposal);
        }
        finally
        {
            await server.SendRawAsync(":1\r\n"u8.ToArray());
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<ObjectDisposedException>();
            await disposal!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await Assert.That(await client.Strings.IncrementAsync("client-survives")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitReleasePreservesServerFailureForConcurrentCallers(bool cancelAfterHandoff)
    {
        var releaseCalls = 0;
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return null;
                Interlocked.Increment(ref releaseCalls);
                return "-NOPERM release denied\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var locker = new RespireFusionCacheDistributedLocker(client);
        using var cancellation = new CancellationTokenSource();
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero, cancellation.Token))!;
        if (cancelAfterHandoff) await cancellation.CancelAsync();
        var first = DistributedLockerTests.ReleaseAsync(locker, "cache", owner, cancellation.Token).AsTask();
        var second = owner.DisposeAsync().AsTask();
        var error = await Assert.That(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireServerException>();
        var repeated = await Assert.That(async () => await second.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo("NOPERM release denied");
        await Assert.That(repeated).IsSameReferenceAs(error);
        await Assert.That(releaseCalls).IsEqualTo(1);
    }

    [Test]
    public async Task TeardownJoinsEveryHandleWhenOneReleaseFails()
    {
        var releasesArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = 0;
        await using var server = new FakeRespServer("$1\r\n1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                ? "$1\r\n1\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return false;
                if (Interlocked.Increment(ref releaseCount) == 2) releasesArrived.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var locker = new RespireFusionCacheDistributedLocker(client);
        var rejected = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "rejected", TimeSpan.Zero))!;
        var blocked = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "blocked", TimeSpan.Zero))!;
        var disposal = locker.DisposeAsync().AsTask();
        await releasesArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replies = server.ReceivedCommands.Where(command => command.StartsWith("DELEX ", StringComparison.Ordinal))
            .Select(command => command.Contains(blocked.LeaseKey.ToString(), StringComparison.Ordinal)
                ? ":1\r\n" : "-NOPERM release denied\r\n").ToArray();
        try { await Assert.That(disposal.IsCompleted).IsFalse(); }
        finally { await server.SendRawAsync(System.Text.Encoding.UTF8.GetBytes(string.Concat(replies))); }
        var error = await Assert.That(async () => await disposal.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireServerException>();
        var repeated = await Assert.That(async () => await locker.DisposeAsync())
            .Throws<RespireServerException>();
        await Assert.That(repeated).IsSameReferenceAs(error);
        await blocked.DisposeAsync();
        await Assert.That(async () => await rejected.DisposeAsync()).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    [Test]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    public async Task RenewalErrorStopsCleanupAndReportsUncertainOwnership(int protocol, bool releaseOnCancellation)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.Split(' ')[0] switch
            {
                "HELLO" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "EVALSHA" => "$1\r\n1\r\n"u8.ToArray(),
                "SET" => "-NOPERM renewal denied\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { LeaseDuration = TimeSpan.FromSeconds(1), ReleaseOnCallerCancellation = releaseOnCancellation });
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!owner.OwnershipCancellationToken.IsCancellationRequested) await Task.Delay(10, timeout.Token);
        await owner.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await Assert.That(owner.OwnershipLost).IsTrue();
        await Assert.That(owner.RenewalFailure).IsTypeOf<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(0);
        // After loss, cleanup never assumes ownership; server expiry is the fallback.
    }

    [Test]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    public async Task CancellationAfterSuccessfulReplyReleasesUnreturnedLeaseWithoutStartingRenewal(int protocol, bool releaseOnCancellation)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.Split(' ')[0] switch
            {
                "HELLO" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "EVALSHA" => "$1\r\n1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        await using var locker = new RespireFusionCacheDistributedLocker(client,
            new() { ReleaseOnCallerCancellation = releaseOnCancellation });
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
        var error = await Assert.That(async () => await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero, cancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        var setupCount = protocol == 3 ? 1 : 0;
        var commands = server.ReceivedCommands.Skip(setupCount).ToArray();
        await Assert.That(commands.Length).IsEqualTo(4);
        var arguments = server.ReceivedArguments.Skip(setupCount).ToArray();
        // Managed release fences uncertainty before deleting only the acquired owner.
        await Assert.That(commands[1]).IsEqualTo("CLIENT ID");
        await Assert.That(commands[2]).IsEqualTo("CLIENT KILL ID 1 SKIPME yes");
        await Assert.That(arguments[3][1].SequenceEqual(arguments[0][3])).IsTrue();
        await Assert.That(arguments[3][3].SequenceEqual(arguments[0][5])).IsTrue();
    }
}
