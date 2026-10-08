using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.SignalR.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

public class SubscriptionLifetimeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledInvocationRetainsTokenAndRejectsLateCompletion(bool alreadyCancelled)
    {
        var results = new ClientResultsManager();
        using var cancellation = new CancellationTokenSource();
        if (alreadyCancelled) cancellation.Cancel();
        var pending = results.AddInvocation<int>("connection", "id", cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(pending.IsCanceled).IsTrue();
        await Assert.That(results.TryGetType("id", out _)).IsFalse();
        await Assert.That(results.RemoveInvocation("id").HasValue).IsFalse();
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 1));
        var next = results.AddInvocation<int>("connection", "next", CancellationToken.None);
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("next", 2));
        await Assert.That(await next).IsEqualTo(2);
    }

    [Test]
    public async Task CompletionBeforeRegistrationCannotCancelReusedInvocation()
    {
        var results = new ClientResultsManager();
        using var cancellation = new CancellationTokenSource();
        var first = new ClientResultsManager.TaskCompletionSourceWithCancellation<int>(
            results, "connection", "id", cancellation.Token);
        results.AddInvocation("id", (typeof(int), "connection", first, static (owner, message) =>
        {
            ((ClientResultsManager.TaskCompletionSourceWithCancellation<int>)owner).SetResult((int)message.Result!);
            return Task.CompletedTask;
        }));
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 1));
        await Assert.That(await first.Task).IsEqualTo(1);
        first.RegisterCancellation();
        var next = results.AddInvocation<int>("connection", "id", CancellationToken.None);
        cancellation.Cancel();
        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(results.TryGetType("id", out _)).IsTrue();
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 2));
        await Assert.That(await next).IsEqualTo(2);
    }

    [Test]
    public async Task ForwardedCompletionIsAwaitedAndOwnedOnce()
    {
        var results = new ClientResultsManager();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        results.AddInvocation("id", (typeof(int), "connection", new object(), async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
        }));
        await Assert.That(() => results.TryCompleteResult("wrong", CompletionMessage.WithResult("id", 1)))
            .ThrowsExactly<InvalidOperationException>();
        var completion = results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(completion.IsCompleted).IsFalse();
            await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 2));
        }
        finally { release.TrySetResult(); }
        await completion;
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(results.RemoveInvocation("id").HasValue).IsFalse();
    }

    [Test]
    public async Task RemovedInvocationHasNoCompletionOwnerOnSecondRemoval()
    {
        var results = new ClientResultsManager();
        results.AddInvocation("id", (typeof(int), "connection", new object(), static (_, _) => Task.CompletedTask));
        await Assert.That(results.RemoveInvocation("id").HasValue).IsTrue();
        await Assert.That(results.RemoveInvocation("id").HasValue).IsFalse();
    }

    [Test]
    public async Task FailedFirstSubscriberCannotHideSubscriptionForNextConnection()
    {
        var manager = new RedisSubscriptionManager();
        var first = new HubConnectionContext(new DefaultConnectionContext("first"), new(), NullLoggerFactory.Instance);
        var second = new HubConnectionContext(new DefaultConnectionContext("second"), new(), NullLoggerFactory.Instance);
        await Assert.That(async () => await manager.AddSubscriptionAsync("group", first,
            (_, _) => throw new IOException("subscription rejected"))).ThrowsExactly<IOException>();
        var subscribed = false;
        await manager.AddSubscriptionAsync("group", second, (_, connections) =>
        {
            subscribed = true;
            if (connections.Count != 1) throw new InvalidOperationException("Failed registration retained a connection.");
            return Task.CompletedTask;
        });
        await Assert.That(subscribed).IsTrue();
        var removed = false;
        await manager.RemoveSubscriptionAsync("group", second, manager, (_, _) => { removed = true; return Task.CompletedTask; });
        await Assert.That(removed).IsTrue();
    }

    [Test]
    public async Task AcknowledgementDisposalSettlesPendingWaiters()
    {
        var acknowledgements = new AckHandler();
        var pending = acknowledgements.CreateAck(1);
        acknowledgements.Dispose();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(() => acknowledgements.CreateAck(2)).ThrowsExactly<ObjectDisposedException>();
        acknowledgements.TriggerAck(1);
    }
}
