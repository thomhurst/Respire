using Microsoft.Extensions.DependencyInjection;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ZiggyCreatures.Caching.Fusion;

namespace Respire.FusionCache.Tests;

public class IndependentLeaseLifetimeWireTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiryJoinsRenewalAndOwnerCheckedReleaseWithoutStoppingClient(int protocol)
    {
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer();
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("SET ", StringComparison.Ordinal)) renewed.TrySetResult();
            return Reply(command);
        };
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return false;
            releaseArrived.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, LeaseDuration = TimeSpan.FromSeconds(1),
            MaximumIndependentLeaseLifetime = TimeSpan.FromSeconds(2),
        });
        using var caller = new CancellationTokenSource();
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero, caller.Token))!;
        await caller.CancelAsync();
        try
        {
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await releaseArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(owner.LifetimeLimitExpired).IsTrue();
            await Assert.That(owner.OwnershipCancellationToken.IsCancellationRequested).IsTrue();
            await Assert.That(owner.OwnershipLost).IsFalse();
            await Assert.That(owner.RenewalFailure).IsNull();
            var releases = server.ReceivedArguments.Where(arguments => arguments[0].AsSpan().SequenceEqual("DELEX"u8)).ToArray();
            await Assert.That(releases.Length).IsEqualTo(1);
            var acquisition = server.ReceivedArguments.Single(arguments => arguments[0].AsSpan().SequenceEqual("EVALSHA"u8));
            await Assert.That(releases[0][1].SequenceEqual(acquisition[3])).IsTrue();
            await Assert.That(releases[0][3].SequenceEqual(acquisition[5])).IsTrue();
            var cleanup = owner.DisposeAsync().AsTask();
            await Assert.That(cleanup.IsCompleted).IsFalse();
            await Assert.That(owner.DisposeAsync().AsTask()).IsSameReferenceAs(cleanup);
        }
        finally { await server.SendRawAsync(":1\r\n"u8.ToArray()); }
        await Task.WhenAll(owner.DisposeAsync().AsTask(), locker.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await client.Strings.IncrementAsync("client-survives")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AcquisitionTimeDoesNotConsumeIndependentLifetime(int protocol)
    {
        var acquisitionArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer();
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
            acquisitionArrived.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(200),
        });
        var acquisition = DistributedLockerTests.AcquireAsync(locker, "cache", Timeout.InfiniteTimeSpan).AsTask();
        await acquisitionArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await Assert.That(acquisition.IsCompleted).IsFalse();
        await server.SendRawAsync("$1\r\n1\r\n"u8.ToArray());
        var owner = (RespireFusionCacheLock)(await acquisition.WaitAsync(TimeSpan.FromSeconds(5)))!;
        await Assert.That(owner.OwnershipCancellationToken.IsCancellationRequested).IsFalse();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForCancellationAsync(owner, timeout.Token);
        await owner.DisposeAsync();
        await Assert.That(owner.LifetimeLimitExpired).IsTrue();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task EarlyReleaseOrShutdownStopsLongLifetimeWorker(int protocol, bool shutdown)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.MaxValue,
        });
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        if (shutdown) await locker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(owner.LifetimeLimitExpired).IsFalse();
        await Assert.That(owner.OwnershipCancellationToken.IsCancellationRequested).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(await client.Strings.IncrementAsync("client-survives")).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExplicitReleaseBeforeExpiryRemainsSingleCleanupWhileReplyIsBlocked(int protocol)
    {
        var releaseArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer();
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("DELEX ", StringComparison.Ordinal)) return false;
            releaseArrived.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(200),
        });
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        var release = owner.DisposeAsync().AsTask();
        await releaseArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = locker.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(300);
            await Assert.That(release.IsCompleted).IsFalse();
            await Assert.That(shutdown.IsCompleted).IsFalse();
            await Assert.That(owner.LifetimeLimitExpired).IsFalse();
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
        }
        finally { await server.SendRawAsync(":1\r\n"u8.ToArray()); }
        await Task.WhenAll(release, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExpiryCleanupFailureRemainsObservableByRepeatedRelease(int protocol)
    {
        await using var server = CreateServer();
        server.ReplyOverride = (_, command) => command.StartsWith("DELEX ", StringComparison.Ordinal)
            ? "-NOPERM release denied\r\n"u8.ToArray() : Reply(command);
        await using var client = await ConnectAsync(server, protocol);
        await using var locker = new RespireFusionCacheDistributedLocker(client, new()
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(50),
        });
        var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(locker, "cache", TimeSpan.Zero))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WaitForCancellationAsync(owner, deadline.Token);
        var error = await Assert.That(async () => await owner.DisposeAsync().AsTask().WaitAsync(deadline.Token))
            .Throws<RespireServerException>();
        var repeated = await Assert.That(async () => await DistributedLockerTests.ReleaseAsync(locker, "cache", owner))
            .Throws<RespireServerException>();
        await Assert.That(owner.LifetimeLimitExpired).IsTrue();
        await Assert.That(repeated).IsSameReferenceAs(error);
        await Assert.That(error!.Message).IsEqualTo("NOPERM release denied");
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-2)]
    public async Task InvalidLifetimeFailsAcrossConstructionAndRegistration(int milliseconds)
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, 2);
        var options = new RespireFusionCacheDistributedLockerOptions
        {
            ReleaseOnCallerCancellation = false, MaximumIndependentLeaseLifetime = TimeSpan.FromMilliseconds(milliseconds),
        };
        await Assert.That(() => new RespireFusionCacheDistributedLocker(client, options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ServiceCollection().AddFusionCacheRespireDistributedLocker(options)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ServiceCollection().AddFusionCache().WithRespireDistributedLocker(options)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task LifetimeRequiresIndependentPolicyAndDefaultsToUnlimited()
    {
        await using var server = CreateServer();
        await using var client = await ConnectAsync(server, 2);
        await Assert.That(new RespireFusionCacheDistributedLockerOptions().MaximumIndependentLeaseLifetime).IsNull();
        var options = new RespireFusionCacheDistributedLockerOptions { MaximumIndependentLeaseLifetime = TimeSpan.FromSeconds(1) };
        await Assert.That(() => new RespireFusionCacheDistributedLocker(client, options)).Throws<ArgumentException>();
        await Assert.That(() => new ServiceCollection().AddFusionCacheRespireDistributedLocker(options)).Throws<ArgumentException>();
        await Assert.That(() => new ServiceCollection().AddFusionCache().WithRespireDistributedLocker(options)).Throws<ArgumentException>();
    }

    internal static async Task WaitForCancellationAsync(RespireFusionCacheLock owner, CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = owner.OwnershipCancellationToken.Register(() => cancelled.TrySetResult());
        await cancelled.Task.WaitAsync(token);
    }

    private static FakeRespServer CreateServer() => new(":1\r\n"u8.ToArray()) { ReplyOverride = (_, command) => Reply(command) };

    private static byte[]? Reply(string command) => command.Split(' ')[0] switch
    {
        "HELLO" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
        "EVALSHA" => "$1\r\n1\r\n"u8.ToArray(),
        "SET" => "+OK\r\n"u8.ToArray(),
        _ => null,
    };

    private static Task<RespireClient> ConnectAsync(FakeRespServer server, int protocol) => RespireClient.ConnectAsync(new RespireOptions
    {
        Protocol = (RespProtocol)protocol, Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
        MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
    }).AsTask();
}
