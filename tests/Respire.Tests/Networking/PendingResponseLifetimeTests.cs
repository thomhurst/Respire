using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PendingResponseLifetimeTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 2)]
    public async Task SingleReplyRecyclesOnlyAfterBothOwnersRelease(bool callerFirst, int outcome)
    {
        var pool = new PendingResponsePool(1);
        var source = pool.Rent();
        source.Deadline = CommandDeadline.FromRawValue(1);
        var observedState = source.State;
        var pending = source.Task;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Signal readiness without consuming: both release orders are under test control.
        pending.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => ready.TrySetResult());
        if (outcome == 0) source.TrySetResult(RespValue.Integer(42));
        else if (outcome == 1) source.TrySetException(new InvalidOperationException("failure"));
        else source.TrySetCanceled(new CancellationToken(true));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (callerFirst) await ConsumeAsync(pending, outcome);
        else source.ReleaseRef();
        var other = pool.Rent();
        await Assert.That(ReferenceEquals(source, other)).IsFalse();
        // Keep the other source checked out, so the only available pool entry is the source
        // returned by the remaining owner. No shared/global pool ordering is assumed.
        if (callerFirst) source.ReleaseRef();
        else await ConsumeAsync(pending, outcome);
        var recycled = pool.Rent();
        await Assert.That(ReferenceEquals(recycled, source)).IsTrue();
        await Assert.That(recycled.State).IsNotEqualTo(observedState);
        await Assert.That(recycled.Deadline).IsEqualTo(CommandDeadline.None);
        RespireTimeoutDiagnostics? diagnostics = null;
        await Assert.That(recycled.TrySetTimedOut(observedState, TimeSpan.FromSeconds(1), ref diagnostics, null)).IsFalse();
        Finish(recycled);
        Finish(other);
    }

    [Test]
    [NotInParallel]
    public async Task InlineConsumerCannotRecycleAReservedSourceWhileItsRunnerIsBlocked()
    {
        var pool = new PendingResponsePool(1);
        var source = pool.Rent();
        var awaiter = source.Task.ConfigureAwait(false).GetAwaiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var value = awaiter.GetResult();
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Runner was not released.");
                finished.TrySetResult();
            }
            catch (Exception error) { finished.TrySetException(error); }
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        scheduler.Flush();
        PendingResponseSource? other = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            other = pool.Rent();
            await Assert.That(ReferenceEquals(source, other)).IsFalse();
        }
        finally { release.Set(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var recycled = pool.Rent();
        await Assert.That(ReferenceEquals(recycled, source)).IsTrue();
        Finish(recycled);
        if (other is not null) Finish(other);
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentCallerAndReceiveReleaseRecycleExactlyOnce()
    {
        var pool = new PendingResponsePool(2);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var source = pool.Rent();
            var pending = source.Task;
            source.TrySetResult(RespValue.Integer(42));
            using var barrier = new Barrier(2);
            var caller = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Receive owner did not arrive.");
                using var reply = pending.GetAwaiter().GetResult();
            });
            var receiver = Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Caller did not arrive.");
                source.ReleaseRef();
            });
            await Task.WhenAll(caller, receiver).WaitAsync(TimeSpan.FromSeconds(10));
            var first = pool.Rent();
            var second = pool.Rent();
            await Assert.That(ReferenceEquals(first, second)).IsFalse();
            Finish(first);
            Finish(second);
        }
    }

    private static async Task ConsumeAsync(ValueTask<RespValue> pending, int outcome)
    {
        if (outcome == 0)
        {
            using var value = pending.GetAwaiter().GetResult();
            await Assert.That(value.AsInteger()).IsEqualTo(42);
        }
        else if (outcome == 1)
            await Assert.That(() => pending.GetAwaiter().GetResult()).ThrowsExactly<InvalidOperationException>();
        else
            await Assert.That(() => pending.GetAwaiter().GetResult()).ThrowsExactly<OperationCanceledException>();
    }

    private static void Finish(PendingResponseSource source)
    {
        var pending = source.Task;
        source.TrySetResult(default);
        using var value = pending.GetAwaiter().GetResult();
        source.ReleaseRef();
    }
}
