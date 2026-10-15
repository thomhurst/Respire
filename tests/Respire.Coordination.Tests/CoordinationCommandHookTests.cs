using System.Collections.Concurrent;
using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class CoordinationCommandHookTests
{
    [Test]
    [Arguments("semaphore")]
    [Arguments("lease")]
    [Arguments("fenced")]
    [Arguments("read")]
    [Arguments("write")]
    [Arguments("lease-wait")]
    [Arguments("fenced-wait")]
    public async Task RejectedAcquireDoesNotPerformSetupOrSend(string kind)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("Denied");
        var hook = new RecordingHook { Before = _ => throw rejected };
        await using var client = await ConnectAsync(server, hook);
        var before = server.CommandsSeen;

        var error = await Failure(() => AcquireAsync(client, kind));

        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
    }

    [Test]
    [Arguments("semaphore")]
    [Arguments("lease")]
    [Arguments("fenced")]
    [Arguments("read")]
    [Arguments("write")]
    public async Task AcquireAndDisposeEachCompleteExactlyOnce(string kind)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        var handle = await AcquireAsync(client, kind);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await handle.DisposeAsync();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(hook.Completed.All(c => !c.Command.StartsWith("EVAL", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("lease-wait")]
    [Arguments("fenced-wait")]
    public async Task NotificationWaitHasOneScopeIncludingItsTrackedRead(string kind)
    {
        await using var server = Server();
        var originalReply = server.ReplyOverride;
        server.ReplyOverride = (connection, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            var value when value.StartsWith("GET ", StringComparison.Ordinal)
                || value.StartsWith("HGET ", StringComparison.Ordinal) => "_\r\n"u8.ToArray(),
            var value when value.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal)
                || value.StartsWith("CLIENT CACHING ", StringComparison.Ordinal) => FakeRespServer.OkReply,
            _ => originalReply!(connection, command),
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp3,
            ClientSideCache = new(), CommandHook = hook, ThreadPoolMonitoring = false,
        });

        var handle = await AcquireAsync(client, kind).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("CLIENT CACHING ", StringComparison.Ordinal))).IsTrue();
        await handle.DisposeAsync();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("semaphore")]
    [Arguments("lease")]
    [Arguments("read")]
    public async Task RejectedRenewalAndDisposalPreserveOwnership(string kind)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        var handle = await AcquireAsync(client, kind);
        var before = server.CommandsSeen;
        var rejected = new InvalidOperationException("Denied");
        hook.Before = _ => throw rejected;

        await Assert.That(await Failure(async () =>
        {
            _ = await RenewAsync(handle);
            return handle;
        })).IsSameReferenceAs(rejected);
        await Assert.That(await Failure(async () =>
        {
            await handle.DisposeAsync();
            return handle;
        })).IsSameReferenceAs(rejected);
        await Assert.That(IsReleased(handle)).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(before);

        hook.Before = null;
        await handle.DisposeAsync();
        await Assert.That(IsReleased(handle)).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments("semaphore")]
    [Arguments("read")]
    public async Task FailedRenewalCompletesAfterUnhookedOwnerCleanup(string kind)
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        var handle = await AcquireAsync(client, kind);
        var evalCount = 0;
        server.ReplyOverride = (_, command) => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref evalCount) == 1 ? "-ERR renewal failed\r\n"u8.ToArray() : ":1\r\n"u8.ToArray()
            : null;
        var cleanupWasComplete = false;
        hook.After = (_, outcome) => cleanupWasComplete = outcome == RespireCommandOutcome.Failed && Volatile.Read(ref evalCount) == 2;
        var error = await Failure(async () => { _ = await RenewAsync(handle); return handle; });

        await Assert.That(error).IsTypeOf<RespireServerException>();
        await Assert.That(cleanupWasComplete).IsTrue();
        await Assert.That(IsReleased(handle)).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await handle.DisposeAsync();
    }

    [Test]
    [Arguments("semaphore")]
    [Arguments("read")]
    public async Task CancellationAfterAcquireReplyCompletesAfterOwnerCleanup(string kind)
    {
        await using var server = Server();
        using var cancellation = new CancellationTokenSource();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
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
        var cleanupWasComplete = false;
        hook.After = (_, _) => cleanupWasComplete = SemaphoreWireTests.EvalCommands(server).Length == 2;
        var coordination = new RespireCoordination(client);
        var error = await Failure(async () => kind == "semaphore"
            ? (await coordination.CreateSemaphore("{cancel}:semaphore", 1)
                .TryAcquireAsync(TimeSpan.FromMinutes(1), cancellation.Token)).Permit
            : (await coordination.TryAcquireReadLockAsync("{cancel}:read", TimeSpan.FromMinutes(1), cancellation.Token)).Lock);

        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(cleanupWasComplete).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
    }

    [Test]
    public async Task FencedConversionFailureCompletesAfterUnhookedDisposal()
    {
        await using var server = Server();
        var originalReply = server.ReplyOverride;
        server.ReplyOverride = (connection, command) =>
            command.StartsWith($"EVALSHA {RespireCoordination.AcquireFencedLock.Sha1} ", StringComparison.Ordinal)
                ? "$3\r\nbad\r\n"u8.ToArray() : originalReply!(connection, command);
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        var error = await Failure(() => AcquireAsync(client, "fenced"));

        await Assert.That(error).IsTypeOf<RespireProtocolException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("DELEX ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
    }

    [Test]
    public async Task HookApplicationReentrancyHasIndependentScope()
    {
        await using var server = Server();
        var hook = new RecordingHook();
        await using var client = await ConnectAsync(server, hook);
        Task? independent = null;
        hook.Before = context =>
        {
            if (context.Command == "SEMAPHORE ACQUIRE")
                independent = client.Strings.GetStringAsync("other").AsTask();
        };
        var handle = await AcquireAsync(client, "semaphore");
        await independent!;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Any(c => c.Command == "GET")).IsTrue();
        await handle.DisposeAsync();
    }

    [Test]
    public async Task RedlockFailedQuorumCleanupBypassesReleaseRejection()
    {
        await using var first = Server();
        await using var second = Server();
        await using var third = Server();
        foreach (var server in new[] { first, second, third })
            server.ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
                ? "$-1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        var hook = new RecordingHook
        {
            Before = context =>
            {
                if (context.Command == "LOCK RELEASE") throw new InvalidOperationException("Release denied");
            },
        };
        await using var firstClient = await ConnectAsync(first, hook);
        await using var secondClient = await ConnectAsync(second, hook);
        await using var thirdClient = await ConnectAsync(third, hook);
        var group = new RespireRedlockGroup([firstClient, secondClient, thirdClient]);
        var attempt = await group.TryAcquireAsync("redlock:hook", TimeSpan.FromMinutes(1));

        await Assert.That(attempt.Acquired).IsFalse();
        await Assert.That(hook.Completed.Count).IsEqualTo(3);
        await Assert.That(new[] { first, second, third }.All(server => server.ReceivedCommands.Any(
            command => command.StartsWith("DELEX ", StringComparison.Ordinal)))).IsTrue();
    }

    private static ValueTask<bool> RenewAsync(IAsyncDisposable handle) => handle switch
    {
        RespireSemaphorePermit permit => permit.ResetExpiryAsync(TimeSpan.FromMinutes(1)),
        RespireReadWriteLock lease => lease.ResetExpiryAsync(TimeSpan.FromMinutes(1)),
        RespireCoordinationLease lease => lease.ResetExpiryAsync(TimeSpan.FromMinutes(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(handle)),
    };

    private static bool IsReleased(IAsyncDisposable handle) => handle switch
    {
        RespireSemaphorePermit permit => permit.IsReleased,
        RespireReadWriteLock lease => lease.IsReleased,
        RespireCoordinationLease lease => lease.IsReleased,
        _ => throw new ArgumentOutOfRangeException(nameof(handle)),
    };

    private static async Task<IAsyncDisposable> AcquireAsync(RespireClient client, string kind)
    {
        var coordination = new RespireCoordination(client);
        var duration = TimeSpan.FromMinutes(1);
        return kind switch
        {
            "semaphore" => (await coordination.CreateSemaphore("{hook}:semaphore", 1).TryAcquireAsync(duration)).Permit,
            "lease" => (await coordination.TryAcquireLeaseAsync("{hook}:lease", "owner", duration))!,
            "fenced" => (await coordination.TryAcquireFencedLockAsync("{hook}:lock", "{hook}:counter", duration)).Lock,
            "read" => (await coordination.TryAcquireReadLockAsync("{hook}:read", duration)).Lock,
            "write" => (await coordination.TryAcquireWriteLockAsync("{hook}:write", duration)).Lock,
            "lease-wait" => await coordination.AcquireLeaseAsync("{hook}:lease", "owner", duration),
            "fenced-wait" => await coordination.AcquireFencedLockAsync("{hook}:lock", "{hook}:counter", duration),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static FakeRespServer Server() => new(":1\r\n"u8.ToArray())
    {
        ReplyOverride = (_, command) => command switch
        {
            "CLIENT ID" => SemaphoreWireTests.ClientIdReply,
            var kill when kill.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => SemaphoreWireTests.ClientKillReply,
            var fenced when fenced.StartsWith($"EVALSHA {RespireCoordination.AcquireFencedLock.Sha1} ", StringComparison.Ordinal)
                => "$1\r\n1\r\n"u8.ToArray(),
            var get when get.StartsWith("GET ", StringComparison.Ordinal) => "$1\r\nx\r\n"u8.ToArray(),
            _ => ":1\r\n"u8.ToArray(),
        },
    };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, RecordingHook hook)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            CommandHook = hook, ThreadPoolMonitoring = false,
        });

    private static async Task<Exception?> Failure(Func<Task<IAsyncDisposable>> action)
    {
        try { _ = await action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal Action<string, RespireCommandOutcome>? After;
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context) { Before?.Invoke(context); return null; }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context.Command, outcome));
            After?.Invoke(context.Command, outcome);
        }
    }
}
