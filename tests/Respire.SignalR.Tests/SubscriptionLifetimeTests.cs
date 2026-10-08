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
