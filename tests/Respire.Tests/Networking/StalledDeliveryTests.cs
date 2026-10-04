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
    public async Task HandedOffRepliesKeepWireOrder()
    {
        var first = new PendingResponsePool(1).Rent();
        var sources = Enumerable.Range(0, 4).Select(_ => new PendingResponsePool(1).Rent()).ToArray();
        var order = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var tasks = sources.Select(source => ConsumeAsync(source.Task)).ToArray();
        var firstAwaiter = first.Task.ConfigureAwait(false).GetAwaiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        firstAwaiter.UnsafeOnCompleted(() =>
        {
            using var value = firstAwaiter.GetResult();
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        var scheduler = new CompletionScheduler();
        scheduler.Add(first, RespValue.Integer(0));
        scheduler.Add(sources[0], RespValue.Integer(1));
        scheduler.Add(sources[1], RespValue.Integer(2));
        scheduler.Flush();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A later drain queues behind the blocked runner as well.
        scheduler.Add(sources[2], RespValue.Integer(3));
        scheduler.Add(sources[3], RespValue.Integer(4));
        scheduler.Flush();
        try
        {
            await Assert.That(scheduler.RescueStalledRunner(0, 500)).IsFalse();
            await Assert.That(scheduler.RescueStalledRunner(500, 500)).IsTrue();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(order.ToArray()).IsEquivalentTo(
                new long[] { 1, 2, 3, 4 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
        }

        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        async Task ConsumeAsync(ValueTask<RespValue> pending)
        {
            using var value = await pending;
            order.Enqueue(value.AsInteger());
        }
    }

    [Test]
    public async Task StallWatcherWakesOnlyWhenRepliesQueueBehindARunner()
    {
        var scheduler = new CompletionScheduler();
        var watch = scheduler.WaitForPossibleStallAsync().AsTask();

        // One reply on an idle connection cannot wait behind anything.
        var lone = new PendingResponsePool(1).Rent();
        var loneTask = lone.Task.AsTask();
        scheduler.Add(lone, RespValue.Integer(1));
        scheduler.Flush();
        using (await loneTask.WaitAsync(TimeSpan.FromSeconds(5))) { }
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(watch.IsCompleted).IsFalse();

        // A reply queued behind a busy runner wakes the watcher.
        var first = new PendingResponsePool(1).Rent();
        var awaiter = first.Task.ConfigureAwait(false).GetAwaiter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        awaiter.UnsafeOnCompleted(() =>
        {
            using var value = awaiter.GetResult();
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        scheduler.Add(first, RespValue.Integer(2));
        scheduler.Flush();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(watch.IsCompleted).IsFalse();
            var behind = new PendingResponsePool(1).Rent();
            scheduler.Add(behind, RespValue.Integer(3));
            scheduler.Flush();
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(scheduler.RescueStalledRunner(0, 500, out var nextCheck)).IsFalse();
            await Assert.That(nextCheck).IsEqualTo(500);
        }
        finally
        {
            release.Set();
        }

        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(scheduler.RescueStalledRunner(1_000, 500, out var idleCheck)).IsFalse();
        await Assert.That(idleCheck).IsEqualTo(-1);
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
    [Arguments(1)]
    [Arguments(200)]
    public async Task TeardownKeepsRescuingRepliesParsedBeforeTheConnectionClosed(int drainBudgetMilliseconds)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        // The stall threshold is out of reach, so only teardown's drain budget can hand off the
        // queued reply; a budget shorter than the teardown interval must still hand it off.
        var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                StalledDeliveryThreshold = TimeSpan.FromHours(1),
                RetirementDrainFallbackTimeout = TimeSpan.FromMilliseconds(drainBudgetMilliseconds),
            });
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

    [Test]
    public async Task TeardownDrainsAChainOfBlockingContinuations()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                StalledDeliveryThreshold = TimeSpan.FromHours(1),
                RetirementDrainFallbackTimeout = TimeSpan.FromMilliseconds(1),
            });
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var first = awaiter.GetResult();
                var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
                var third = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
                // The second reply's continuation blocks on the third, so each rescue only
                // unblocks one link of the chain.
                second.UnsafeOnCompleted(() =>
                {
                    try
                    {
                        using var value = second.GetResult();
                        using var last = third.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    }
                    catch (RespireConnectionException)
                    {
                        // A reply not yet parsed when the socket closed fails instead; the chain still ends.
                    }
                    finally
                    {
                        secondDone.TrySetResult();
                    }
                });
                queued.TrySetResult();
                secondDone.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                firstDone.TrySetResult();
            }
            catch (Exception error) { firstDone.TrySetException(error); }
        });

        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!connection.HasUndeliveredReplies && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        var disposed = connection.DisposeAsync().AsTask();
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposed.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
