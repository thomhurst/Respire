using System.Collections.Concurrent;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public partial class RedlockWireTests
{
    private static RedlockHook[] CreateHooks() => [new(), new(), new()];

    [Test]
    public async Task GroupHooksAdmitAllNodesBeforeDispatchAndCompleteOnceInOrder()
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        var events = new List<string>();
        var prematureDispatch = false;
        for (var i = 0; i < hooks.Length; i++)
        {
            var node = i;
            hooks[i].Before = context =>
            {
                events.Add($"enter {node}");
                prematureDispatch |= nodes.CommandsSeen != 0;
            };
            hooks[i].After = _ => events.Add($"exit {node}");
        }
        var attempt = await new RespireRedlockGroup(nodes.Clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        await Assert.That(prematureDispatch).IsFalse();
        await Assert.That(string.Join(",", events)).IsEqualTo("enter 0,enter 1,enter 2,exit 0,exit 1,exit 2");
        foreach (var hook in hooks) { hook.Before = null; hook.After = null; }
        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsTrue();
        await attempt.DisposeAsync();
        foreach (var hook in hooks)
        {
            await Assert.That(string.Join(",", hook.Completed.Select(static e => e.Context.Command)))
                .IsEqualTo("REDLOCK ACQUIRE,REDLOCK RENEW,REDLOCK RELEASE,REDLOCK DISPOSE");
            await Assert.That(hook.Completed.All(static e => e.Outcome == RespireCommandOutcome.Succeeded
                && e.Error is null && ReferenceEquals(e.State, e.Context))).IsTrue();
        }
        await Assert.That(nodes.CommandsSeen).IsEqualTo(9);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GroupRejectionStopsAdmissionAndEveryNode(bool cancelException)
    {
        var hooks = CreateHooks();
        Exception rejected = cancelException ? new OperationCanceledException() : new InvalidOperationException("policy");
        hooks[1].Before = _ => throw rejected;
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        Exception? failure = null;
        try { _ = await new RespireRedlockGroup(nodes.Clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30)); }
        catch (Exception error) { failure = error; }
        await Assert.That(ReferenceEquals(failure, rejected)).IsTrue();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(0);
        await Assert.That(hooks[0].Completed.Single().Outcome)
            .IsEqualTo(cancelException ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed);
        await Assert.That(hooks[1].Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hooks[1].Completed.Single().State).IsNull();
        await Assert.That(ReferenceEquals(hooks[0].Completed.Single().Error, rejected)).IsTrue();
        await Assert.That(hooks[2].Completed.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("REDLOCK RENEW")]
    [Arguments("REDLOCK RELEASE")]
    [Arguments("REDLOCK DISPOSE")]
    public async Task RejectedHandleOperationPreservesOwnershipAndCanBeRetried(string operation)
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        var attempt = await new RespireRedlockGroup(nodes.Clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        hooks[1].Before = context => { if (context.Command == operation) throw new InvalidOperationException("policy"); };
        var before = nodes.CommandsSeen;
        await Assert.That(async () =>
        {
            if (operation == "REDLOCK RENEW") _ = await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(1));
            else if (operation == "REDLOCK RELEASE") _ = await attempt.Lock.ReleaseAsync();
            else await attempt.DisposeAsync();
        }).Throws<InvalidOperationException>();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(before);
        await Assert.That(attempt.Lock.IsReleased).IsFalse();
        await Assert.That(attempt.Lock.Duration).IsEqualTo(TimeSpan.FromSeconds(30));
        hooks[1].Before = null;
        await Assert.That(await attempt.Lock.ReleaseAsync()).IsTrue();
        await Assert.That(nodes.CommandsSeen).IsEqualTo(before + 3);
    }

    [Test]
    public async Task PreCanceledGroupCompletesAllAdmissionsWithoutDispatch()
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await new RespireRedlockGroup(nodes.Clients)
            .TryAcquireAsync("hook", TimeSpan.FromSeconds(30), cancellation.Token)).Throws<OperationCanceledException>();
        foreach (var hook in hooks)
        {
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            await Assert.That(hook.Completed.Single().Context.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(nodes.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedQuorumCompletesAfterUnrejectableCorrection(bool renewal)
    {
        var hooks = CreateHooks();
        var rejectNodes = !renewal;
        await using var nodes = await Nodes.StartAsync((node, command) =>
            rejectNodes && node < 2 && command.StartsWith("SET ", StringComparison.Ordinal) ? Nil : null, hooks: hooks);
        var prematureCompletion = false;
        var group = new RespireRedlockGroup(nodes.Clients);
        RespireRedlockAttempt attempt = default;
        if (renewal) attempt = await group.TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        rejectNodes = true;
        foreach (var hook in hooks)
        {
            hook.Completed.Clear();
            hook.Before = context => { if (!context.Command.StartsWith("REDLOCK", StringComparison.Ordinal)) throw new InvalidOperationException("internal admission"); };
            hook.After = _ => prematureCompletion |= nodes.Servers.Any(static s =>
                !s.ReceivedCommands.Any(static c => c.StartsWith("DELEX ", StringComparison.Ordinal)));
        }
        if (renewal)
        {
            await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsFalse();
            await Assert.That(attempt.Lock.IsReleased).IsTrue();
        }
        else await Assert.That((await group.TryAcquireAsync("hook", TimeSpan.FromSeconds(30))).Acquired).IsFalse();
        await Assert.That(prematureCompletion).IsFalse();
        foreach (var hook in hooks)
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        foreach (var server in nodes.Servers)
            await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task SharedHookStillHasOneEntryPerNodeAndObserverErrorsDoNotChangeResult()
    {
        var hook = new RedlockHook { After = _ => throw new InvalidOperationException("observer") };
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: [hook, hook, hook]);
        var attempt = await new RespireRedlockGroup(nodes.Clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(hook.Completed.Count).IsEqualTo(3);
        await Assert.That(hook.Completed.Select(static e => e.Context).Distinct().Count()).IsEqualTo(3);
        await attempt.DisposeAsync();
        await Assert.That(hook.Completed.Count).IsEqualTo(6);
    }

    [Test]
    public async Task HookReentrancyHasIndependentAdmissionAndCompletionRunsOutsideLeaseGate()
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        hooks[0].Before = context =>
        {
            if (context.Command == "REDLOCK ACQUIRE")
                _ = nodes.Clients[0].Locks.TryTakeAsync("nested", new RespireLockToken(new byte[] { 1 }),
                    TimeSpan.FromSeconds(30)).AsTask().GetAwaiter().GetResult();
        };
        var attempt = await new RespireRedlockGroup(nodes.Clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        var released = false;
        hooks[0].After = entry =>
        {
            if (entry.Context.Command == "REDLOCK RENEW")
                released = attempt.Lock.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(released).IsTrue();
        await Assert.That(hooks[0].Completed.Count(static e => e.Context.Command == "LOCK ACQUIRE")).IsEqualTo(1);
        foreach (var hook in hooks)
            await Assert.That(hook.Completed.Count(static e => e.Context.Command == "REDLOCK RELEASE")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinalLeaseConversionFailureCleansUpBeforeFailedCompletion(bool renewal)
    {
        var hooks = CreateHooks();
        var clock = new FailingConversionClock();
        var fail = !renewal;
        await using var nodes = await Nodes.StartAsync((_, command) =>
        {
            if (fail && command.StartsWith("SET ", StringComparison.Ordinal)) clock.FailNextRead();
            return null;
        }, hooks: hooks);
        var group = new RespireRedlockGroup(nodes.Clients, timeProvider: clock);
        RespireRedlockAttempt attempt = default;
        if (renewal) attempt = await group.TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        fail = true;
        var earlyCompletion = false;
        foreach (var hook in hooks)
        {
            hook.Completed.Clear();
            hook.After = _ => earlyCompletion |= nodes.Servers.Any(static s =>
                !s.ReceivedCommands.Any(static c => c.StartsWith("DELEX ", StringComparison.Ordinal)));
        }
        await Assert.That(async () =>
        {
            if (renewal) _ = await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30));
            else _ = await group.TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        }).Throws<InvalidOperationException>();
        await Assert.That(earlyCompletion).IsFalse();
        foreach (var hook in hooks)
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        if (renewal) await Assert.That(attempt.Lock.IsReleased).IsTrue();
    }

    [Test]
    public async Task DriftExhaustedLeaseCompletesSuccessfullyAfterCleanup()
    {
        var hooks = CreateHooks();
        var clock = new ManualClock();
        await using var nodes = await Nodes.StartAsync((_, command) =>
        {
            if (command.StartsWith("SET ", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromSeconds(1));
            return null;
        }, hooks: hooks);
        var attempt = await new RespireRedlockGroup(nodes.Clients, timeProvider: clock)
            .TryAcquireAsync("hook", TimeSpan.FromSeconds(1));
        await Assert.That(attempt.Acquired).IsFalse();
        foreach (var hook in hooks)
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        foreach (var server in nodes.Servers)
            await Assert.That(server.ReceivedCommands.Count(static c => c.StartsWith("DELEX ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task GroupRetainsTheSameNativeFacetForAdmissionAndDispatch()
    {
        var hooks = CreateHooks();
        await using var nodes = await Nodes.StartAsync(static (_, _) => null, hooks: hooks);
        var clients = nodes.Clients.Select(client =>
        {
            var proxy = DispatchProxy.Create<IRespireClient, OnceOnlyLockFacetClient>();
            ((OnceOnlyLockFacetClient)proxy).Locks = client.Locks;
            return proxy;
        }).ToArray();
        var attempt = await new RespireRedlockGroup(clients).TryAcquireAsync("hook", TimeSpan.FromSeconds(30));
        await Assert.That(attempt.Acquired).IsTrue();
        await Assert.That(await attempt.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await attempt.DisposeAsync();
        foreach (var hook in hooks)
            await Assert.That(hook.Completed.Count).IsEqualTo(3);
        foreach (var client in clients)
            await Assert.That(((OnceOnlyLockFacetClient)client).Reads).IsEqualTo(1);
    }

    public class OnceOnlyLockFacetClient : DispatchProxy
    {
        internal IRespireLockCommands Locks = null!;
        internal int Reads;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Locks" && Interlocked.Increment(ref Reads) == 1
                ? Locks : throw new InvalidOperationException("Facet was resolved again after policy capture.");
    }

    private sealed class FailingConversionClock : TimeProvider
    {
        private int _fail;
        internal void FailNextRead() => Volatile.Write(ref _fail, 1);
        public override long GetTimestamp()
            => Interlocked.Exchange(ref _fail, 0) != 0
                ? throw new InvalidOperationException("lease conversion") : base.GetTimestamp();
    }

    private sealed record HookCompletion(RespireCommandContext Context, object? State,
        RespireCommandOutcome Outcome, Exception? Error);

    private sealed class RedlockHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal Action<HookCompletion>? After;
        internal ConcurrentQueue<HookCompletion> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error)
        {
            var entry = new HookCompletion(context, state, outcome, error);
            Completed.Enqueue(entry);
            After?.Invoke(entry);
        }
    }
}
