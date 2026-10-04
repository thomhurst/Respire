using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Reply delivery is serial per connection; these cover handing it off when a continuation
/// blocks the runner while other replies wait.
/// </summary>
public class StalledDeliveryTests
{
    [Test]
    public async Task StalledRunnerHandsQueuedRepliesToANewRunner()
    {
        var first = new PendingResponsePool(1).Rent();
        var second = new PendingResponsePool(1).Rent();
        var firstAwaiter = first.Task.ConfigureAwait(false).GetAwaiter();
        var secondTask = second.Task.AsTask();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstAwaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var value = firstAwaiter.GetResult();
                entered.TrySetResult();
                // Blocks on the reply queued behind it, as sync-over-async on one connection does.
                if (!secondTask.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Second reply was never delivered.");
                using var secondValue = secondTask.Result;
                completed.TrySetResult(secondValue.AsInteger());
            }
            catch (Exception error) { completed.TrySetException(error); }
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(first, RespValue.Integer(1));
        scheduler.Add(second, RespValue.Integer(2));
        scheduler.Flush();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(scheduler.RescueStalledRunner(0, 500)).IsFalse();
        await Assert.That(scheduler.RescueStalledRunner(499, 500)).IsFalse();
        await Assert.That(secondTask.IsCompleted).IsFalse();
        await Assert.That(scheduler.RescueStalledRunner(500, 500)).IsTrue();
        await Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(2);
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SlowContinuationWithNothingQueuedIsNotHandedOff()
    {
        var source = new PendingResponsePool(1).Rent();
        var awaiter = source.Task.ConfigureAwait(false).GetAwaiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        awaiter.UnsafeOnCompleted(() =>
        {
            using var value = awaiter.GetResult();
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Integer(1));
        scheduler.Flush();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(scheduler.RescueStalledRunner(0, 500)).IsFalse();
            await Assert.That(scheduler.RescueStalledRunner(10_000, 500)).IsFalse();
        }
        finally
        {
            release.Set();
        }

        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplyContinuationCanBlockOnAnotherReplyFromItsConnection(bool commandTimeout)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { CommandTimeout = commandTimeout ? TimeSpan.FromSeconds(30) : null });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var first = awaiter.GetResult();
                // Sync-over-async on the same connection: this reply is delivered behind the
                // continuation that is waiting for it.
                using var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                    .AsTask().GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
