using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CompletionSchedulerTests
{
    [Test]
    public async Task ParsedReplyWinsOverCancellationAndAnAlreadyCapturedDeadlineSweep()
    {
        var source = new PendingResponsePool(1).Rent();
        var pending = source.Task.AsTask();
        var observedState = source.State;
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        // Hold the parsed reply in the scheduler, exactly as a busy earlier continuation does.
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        RespireTimeoutDiagnostics? diagnostics = null;
        await Assert.That(source.TrySetTimedOut(observedState, TimeSpan.FromSeconds(1), ref diagnostics, null)).IsFalse();
        scheduler.Flush();
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsInteger()).IsEqualTo(42);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MultiReplyReservesOnlyAfterItsFinalReplyAndPreservesPrefixErrors(bool cancelBeforeFinal)
    {
        var source = MultiReplyPendingResponseSource.Rent(2, 0, "MULTI/EXEC");
        var pending = source.Task.AsTask();
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Error("ERR queue rejected"u8.ToArray()));
        if (cancelBeforeFinal) await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsTrue();
        scheduler.Add(source, RespValue.Integer(42));
        if (!cancelBeforeFinal) await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        scheduler.Flush();
        if (cancelBeforeFinal)
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        else
        {
            using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.IsError).IsTrue();
            await Assert.That(System.Text.Encoding.UTF8.GetString(reply.AsSpan())).IsEqualTo("ERR queue rejected");
        }
    }

    [Test]
    public async Task ParsedStreamingErrorPreservesTheAskingFailure()
    {
        var source = new BulkStreamPendingResponseSource("GET", true, null);
        var pending = source.Task.AsTask();
        var scheduler = new CompletionScheduler();
        var prefix = RespValue.Error("ERR asking rejected"u8.ToArray());
        source.ObservePrefix(prefix);
        scheduler.Add(source, prefix);
        scheduler.Add(source, RespValue.Error("ERR get rejected"u8.ToArray()));
        await Assert.That(source.TrySetCanceled(new CancellationToken(true))).IsFalse();
        scheduler.Flush();
        var error = await Assert.That(async () => await pending).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("asking rejected");
    }

    [Test]
    public async Task RunWhileAwaitingDeliversRepliesOnTheThreadTheSuspendedLoopFrees()
    {
        var source = new PendingResponsePool(1).Rent();
        var awaiter = source.Task.ConfigureAwait(false).GetAwaiter();
        var deliveredOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiter.UnsafeOnCompleted(() =>
        {
            using var value = awaiter.GetResult();
            deliveredOn.TrySetResult(Environment.CurrentManagedThreadId);
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        await Assert.That(scheduler.FlushDeferred()).IsTrue();
        var receive = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var suspendedOn = 0;
        var loop = Task.Run(async () =>
        {
            suspendedOn = Environment.CurrentManagedThreadId;
            return await new CompletionScheduler.RunWhileAwaiting<int>(new ValueTask<int>(receive.Task), scheduler);
        });

        // Delivered before the awaited operation completes, on the thread the loop suspended on.
        var deliveredThread = await deliveredOn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(deliveredThread).IsEqualTo(suspendedOn);
        await Assert.That(loop.IsCompleted).IsFalse();
        receive.SetResult(7);
        await Assert.That(await loop.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(7);
    }

    [Test]
    public async Task RunWhileAwaitingResumesTheLoopWhileADeliveredContinuationBlocks()
    {
        var source = new PendingResponsePool(1).Rent();
        var awaiter = source.Task.ConfigureAwait(false).GetAwaiter();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiter.UnsafeOnCompleted(() =>
        {
            using var value = awaiter.GetResult();
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        await Assert.That(scheduler.FlushDeferred()).IsTrue();
        var receive = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Observe the resumption directly: Task.Run cannot link its proxy to the loop until
        // the delegate returns, and the delegate's thread is the one held by the continuation.
        var resumed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () => resumed.TrySetResult(
            await new CompletionScheduler.RunWhileAwaiting<int>(new ValueTask<int>(receive.Task), scheduler)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            receive.SetResult(7);
            // The receive loop must not wait for the caller code it handed its thread to.
            await Assert.That(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(7);
        }
        finally
        {
            release.Set();
        }

        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
