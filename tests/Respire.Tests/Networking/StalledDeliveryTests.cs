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

        await Assert.That(scheduler.RescueStalledRunner(0, 500, out var nextCheck)).IsFalse();
        // Replies are waiting, so the next check lands exactly on the threshold.
        await Assert.That(nextCheck).IsEqualTo(500);
        await Assert.That(scheduler.RescueStalledRunner(499, 500, out nextCheck)).IsFalse();
        await Assert.That(nextCheck).IsEqualTo(1);
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
                // Bounded so a regression fails the test instead of hanging connection teardown.
                using var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task TeardownKeepsRescuingRepliesParsedBeforeTheConnectionClosed()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        // Disable the running rescue so only teardown can deliver the queued reply.
        var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { StalledDeliveryThreshold = TimeSpan.FromHours(1) });
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var first = awaiter.GetResult();
                var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
                blocked.TrySetResult();
                using var second = pending.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });

        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Wait until the second PONG is parsed and queued behind the blocked continuation, then
        // close the connection before the stall threshold: teardown must not abandon that reply.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!connection.HasUndeliveredReplies && !completed.Task.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        await Assert.That(connection.HasUndeliveredReplies).IsTrue();
        var disposed = connection.DisposeAsync().AsTask();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposed.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
