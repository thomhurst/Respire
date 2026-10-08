using Microsoft.AspNetCore.SignalR.Protocol;
using Respire.SignalR.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

public class RemoteInvocationLifetimeTests
{
    [Test]
    public async Task ExpiryRemovesOwnerAndForwardsOnlyOnce()
    {
        var results = new ClientResultsManager();
        var completion = new TaskCompletionSource<CompletionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var lifetime = new RemoteInvocationLifetime(results, "id", default, TimeSpan.FromMilliseconds(50), message =>
        {
            Interlocked.Increment(ref calls);
            completion.TrySetResult(message);
            return Task.CompletedTask;
        });
        Register(results, lifetime);
        lifetime.Start();
        var error = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(error.Error).IsEqualTo("Remote client result timed out.");
        await Assert.That(results.TryGetType("id", out _)).IsFalse();
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 1));
        await lifetime.CompleteAsync(CompletionMessage.WithResult("id", 2));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AlreadyDisconnectedRegistrationCompletesSynchronouslyAndReleasesOwner()
    {
        var results = new ClientResultsManager();
        using var disconnected = new CancellationTokenSource();
        disconnected.Cancel();
        var completion = new TaskCompletionSource<CompletionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new RemoteInvocationLifetime(results, "id", disconnected.Token, TimeSpan.FromMinutes(1), message =>
        {
            completion.TrySetResult(message);
            return Task.CompletedTask;
        });
        Register(results, lifetime);
        lifetime.Start();
        await Assert.That((await completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Error).IsEqualTo("Connection disconnected.");
        await Assert.That(results.TryGetType("id", out _)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResultCompletionReleasesDisconnectRegistration(bool completeBeforeStart)
    {
        var results = new ClientResultsManager();
        using var disconnected = new CancellationTokenSource();
        var calls = 0;
        var lifetime = new RemoteInvocationLifetime(results, "id", disconnected.Token, TimeSpan.FromMinutes(1), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        Register(results, lifetime);
        if (!completeBeforeStart) lifetime.Start();
        await results.TryCompleteResult("connection", CompletionMessage.WithResult("id", 1));
        if (completeBeforeStart) lifetime.Start();
        // Reusing the ID makes a leaked callback observable: it must not remove this new owner.
        results.AddInvocation("id", (typeof(int), "connection", new object(), static (_, _) => Task.CompletedTask));
        disconnected.Cancel();
        await Assert.That(results.TryGetType("id", out _)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await results.CompleteAllAsync();
    }

    private static void Register(ClientResultsManager results, RemoteInvocationLifetime lifetime)
        => results.AddInvocation("id", (typeof(int), "connection", lifetime,
            static (state, message) => ((RemoteInvocationLifetime)state).CompleteAsync(message)));
}
