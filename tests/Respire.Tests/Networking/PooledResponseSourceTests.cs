using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class PooledResponseSourceTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionPublishesOnTheCompletionOwnerWithoutAnotherDispatch(bool converterFails)
    {
        var source = new PendingResponsePool(1).Rent();
        var expectedError = new InvalidOperationException("converter failure");
        var converted = PooledResponseSource<Exception?, long>.Create(source.Task,
            converterFails ? expectedError : null,
            static (Exception? error, in RespValue value) => error is null ? value.AsInteger() : throw error);
        var awaiter = converted.ConfigureAwait(false).GetAwaiter();
        var observed = new TaskCompletionSource<(int Thread, long Value, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiter.UnsafeOnCompleted(() =>
        {
            try { observed.TrySetResult((Environment.CurrentManagedThreadId, awaiter.GetResult(), null)); }
            catch (Exception error) { observed.TrySetResult((Environment.CurrentManagedThreadId, 0, error)); }
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        await Assert.That(scheduler.FlushDeferred()).IsTrue();
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A dedicated owner makes an extra pool dispatch distinguishable without timing assertions.
        new Thread(() =>
        {
            try { scheduler.Execute(); finished.TrySetResult(Environment.CurrentManagedThreadId); }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true }.Start();
        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var owner = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Thread).IsEqualTo(owner);
        if (converterFails) await Assert.That(ReferenceEquals(result.Error, expectedError)).IsTrue();
        else
        {
            await Assert.That(result.Error).IsNull();
            await Assert.That(result.Value).IsEqualTo(42L);
        }
    }

    [Test]
    public async Task BlockingConvertedContinuationDoesNotHoldTheSuspendedReceiveLoop()
    {
        var source = new PendingResponsePool(1).Rent();
        var converted = PooledResponseSource<int, long>.Create(source.Task, 0,
            static (int _, in RespValue value) => value.AsInteger());
        var awaiter = converted.ConfigureAwait(false).GetAwaiter();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                _ = awaiter.GetResult();
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Caller was not released.");
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(42));
        await Assert.That(scheduler.FlushDeferred()).IsTrue();
        var receive = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () => resumed.TrySetResult(
            await new CompletionScheduler.RunWhileAwaiting<int>(new ValueTask<int>(receive.Task), scheduler)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            receive.SetResult(7);
            await Assert.That(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(7);
            await Assert.That(completed.Task.IsCompleted).IsFalse();
        }
        finally { release.Set(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task NestedConversionCanRentAfterItsCallerConsumesTheFirstResult()
    {
        var pool = new PendingResponsePool(2);
        var first = pool.Rent();
        var second = pool.Rent();
        var converted = PooledResponseSource<int, long>.Create(first.Task, 10,
            static (int state, in RespValue value) => state + value.AsInteger());
        var firstAwaiter = converted.ConfigureAwait(false).GetAwaiter();
        var nested = new TaskCompletionSource<Task<long>>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstAwaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                var firstValue = firstAwaiter.GetResult(); // Returns this conversion source to its pool.
                nested.TrySetResult(PooledResponseSource<int, long>.Create(second.Task, checked((int)firstValue),
                    static (int state, in RespValue value) => state + value.AsInteger()).AsTask());
            }
            catch (Exception error) { nested.TrySetException(error); }
        });
        first.TrySetResult(RespValue.Integer(1));
        first.ReleaseRef();
        var secondResult = await nested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.TrySetResult(RespValue.Integer(2));
        second.ReleaseRef();
        await Assert.That(await secondResult.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(13L);
    }

    [Test]
    public async Task CancellationKeepsItsTokenAndDoesNotCallTheConverter()
    {
        var source = new PendingResponsePool(1).Rent();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var converted = PooledResponseSource<int, long>.Create(source.Task, 0,
            static (int _, in RespValue value) => throw new InvalidOperationException("Converter must not run."));
        source.TrySetCanceled(cancellation.Token);
        source.ReleaseRef();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await converted);
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
    }
}
