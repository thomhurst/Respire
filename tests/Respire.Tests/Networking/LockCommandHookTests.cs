using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class CommandHookTests
{
    [Test]
    [Arguments("acquire")]
    [Arguments("wait")]
    [Arguments("throw")]
    [Arguments("keepalive")]
    [Arguments("take")]
    [Arguments("release")]
    [Arguments("renew")]
    [Arguments("verify")]
    public async Task LockRejectionPrecedesEverySend(string route)
    {
        await using var server = LockHookServer();
        var rejection = new InvalidOperationException("Lock policy");
        var hook = new RecordingHook { Before = _ => throw rejection };
        await using var client = RespireClient.Create(Options(server, hook));
        var error = await Failure(async () =>
        {
            switch (route)
            {
                case "acquire": await client.Locks.AcquireAsync("lock", TimeSpan.FromSeconds(30)); break;
                case "wait": await client.Locks.AcquireAsync("lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)); break;
                case "throw": await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromSeconds(30)); break;
                case "keepalive": await client.Locks.AcquireAsync("lock", TimeSpan.FromSeconds(30), keepAlive: true); break;
                case "take": await client.Locks.TryTakeAsync("lock", "owner", TimeSpan.FromSeconds(30)); break;
                case "release": await client.Locks.ReleaseAsync("lock", "owner"); break;
                case "renew": await client.Locks.ResetExpiryAsync("lock", "owner", TimeSpan.FromSeconds(30)); break;
                case "verify": await client.Locks.GetOwnerTokenAsync("lock"); break;
            }
        });
        await Assert.That(error).IsSameReferenceAs(rejection);
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
    }

    [Test]
    [Arguments("release")]
    [Arguments("dispose")]
    [Arguments("renew")]
    [Arguments("verify")]
    [Arguments("keepalive")]
    public async Task ManagedLockRejectionPreservesOwnershipAndCanRetry(string route)
    {
        await using var server = LockHookServer();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        var mutex = await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromMinutes(1));
        var baseline = server.CommandsSeen;
        hook.Before = _ => throw new InvalidOperationException("Lock policy");
        await Failure(async () =>
        {
            switch (route)
            {
                case "release": await mutex.ReleaseAsync(); break;
                case "dispose": await mutex.DisposeAsync(); break;
                case "renew": await mutex.ResetExpiryAsync(TimeSpan.FromMinutes(2)); break;
                case "verify": await mutex.VerifyStillHeldAsync(); break;
                case "keepalive": await mutex.KeepAliveAsync(); break;
            }
        });
        await Assert.That(mutex.IsReleased).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        hook.Before = null;
        await Assert.That(await mutex.ReleaseAsync()).IsEqualTo(RespireLockReleaseOutcome.Released);
        await Assert.That(mutex.IsReleased).IsTrue();
        await Assert.That(hook.Completed.Select(c => c.Outcome).ToArray()).IsEquivalentTo(new[]
        {
            RespireCommandOutcome.Succeeded, RespireCommandOutcome.Rejected, RespireCommandOutcome.Succeeded,
        });
    }

    [Test]
    public async Task LockWaitAndFinalThrowUseOneScope()
    {
        var attempts = 0;
        await using var server = LockHookServer();
        server.ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref attempts) < 3 ? "$-1\r\n"u8.ToArray() : FakeRespServer.OkReply
            : ":1\r\n"u8.ToArray();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        var mutex = await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1));
        await Assert.That(attempts).IsEqualTo(3);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Single().Context.Command).IsEqualTo("LOCK ACQUIRE");
        await mutex.DisposeAsync();
        server.ReplyOverride = (_, _) => "$-1\r\n"u8.ToArray();
        var error = await Failure(async () => await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromSeconds(30)));
        await Assert.That(error).IsTypeOf<RespireLockNotAcquiredException>();
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
    }

    [Test]
    public async Task LockFallbackAndReentrantApplicationCallHaveIndependentScopes()
    {
        await using var server = LockHookServer();
        server.ReplyOverride = (_, command) => command.StartsWith("DELEX ", StringComparison.Ordinal) ? NativeLockCommandTests.UnknownDelex
            : command.StartsWith("DELIFEQ ", StringComparison.Ordinal) ? NativeLockCommandTests.UnknownDelifeq
            : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? "-NOSCRIPT missing\r\n"u8.ToArray()
            : command.StartsWith("GET ", StringComparison.Ordinal) ? "$2\r\n42\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        Task<string?>? reentrant = null;
        hook.Before = context =>
        {
            if (context.Command == "LOCK RELEASE") reentrant = client.Strings.GetStringAsync("other").AsTask();
        };
        await Assert.That(await client.Locks.ReleaseAsync("lock", "owner")).IsTrue();
        await Assert.That(await reentrant!).IsEqualTo("42");
        await Assert.That(hook.Entered.Select(c => c.Command).ToArray()).IsEquivalentTo(new[] { "LOCK RELEASE", "GET" });
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Any(c => c.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ManagedLockCancellationCompletesOnlyAfterForegroundFence()
    {
        var renewalSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fenceSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LockHookServer();
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("SET ", StringComparison.Ordinal) || !command.Contains(" IFEQ ", StringComparison.Ordinal)) return false;
            renewalSent.TrySetResult();
            return true;
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        await client.EnsureReliableCorrectionOrderingAsync();
        server.GateCommand("CLIENT KILL ID 41", finishFence.Task, () => fenceSent.TrySetResult());
        var mutex = await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var pending = mutex.ResetExpiryAsync(TimeSpan.FromMinutes(2), cancellation.Token).AsTask();
        try
        {
            await renewalSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await fenceSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(mutex.IsReleased).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(hook.Completed.Count).IsEqualTo(1);
        }
        finally { finishFence.TrySetResult(); }
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.Last().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await mutex.DisposeAsync();
    }

    [Test]
    public async Task LockPreflightAndCanceledAdmissionDoNotSend()
    {
        await using var server = LockHookServer();
        var hook = new RecordingHook();
        await using var client = RespireClient.Create(Options(server, hook));
        await Assert.That(async () => await client.Locks.AcquireAsync("lock", TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(hook.Entered).IsEmpty();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Locks.AcquireAsync("lock", TimeSpan.FromSeconds(30), cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task LockBackgroundRenewalDoesNotReadmitAndDisposalOwnsStopAndRelease()
    {
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LockHookServer();
        var replies = server.ReplyOverride!;
        server.ReplyOverride = (id, command) =>
        {
            if (command.StartsWith("SET ", StringComparison.Ordinal) && command.Contains(" IFEQ ", StringComparison.Ordinal))
                renewed.TrySetResult();
            return replies(id, command);
        };
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        var attempt = await client.Locks.AcquireAsync("lock", TimeSpan.FromSeconds(2), keepAlive: true);
        var lifetime = attempt.Lock.KeepAliveCancellationToken;
        try
        {
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(hook.Completed.Count).IsEqualTo(1);
        }
        finally { await attempt.DisposeAsync(); }
        await Assert.That(hook.Entered.Select(c => c.Command).ToArray()).IsEquivalentTo(new[] { "LOCK ACQUIRE", "LOCK DISPOSE" });
        await Assert.That(attempt.Lock.IsReleased).IsTrue();
        await Assert.That(lifetime.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ConcurrentManagedReleaseCallersEachCompleteAfterSharedReply()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = LockHookServer();
        server.GateCommand("DELEX ", finish.Task, () => sent.TrySetResult());
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        var mutex = await client.Locks.AcquireOrThrowAsync("lock", TimeSpan.FromMinutes(1));
        var first = mutex.ReleaseAsync().AsTask();
        Task<RespireLockReleaseOutcome>? second = null;
        try
        {
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = mutex.ReleaseAsync().AsTask();
            await Assert.That(hook.Entered.Count).IsEqualTo(3);
            await Assert.That(hook.Completed.Count).IsEqualTo(1);
        }
        finally { finish.TrySetResult(); }
        await Assert.That(await first).IsEqualTo(RespireLockReleaseOutcome.Released);
        await Assert.That(await second!).IsEqualTo(RespireLockReleaseOutcome.Released);
        await Assert.That(hook.Completed.Count).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    private static FakeRespServer LockHookServer() => new(8, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command == "CLIENT ID" ? ":41\r\n"u8.ToArray()
            : command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) ? ":0\r\n"u8.ToArray()
            : command.StartsWith("DELEX ", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : null,
    };
}
