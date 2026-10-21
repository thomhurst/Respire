using System.Collections.Concurrent;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SubscriptionCommandHookTests
{
    [Test]
    [Arguments(RespireSubscriptionKind.Channel)]
    [Arguments(RespireSubscriptionKind.Pattern)]
    [Arguments(RespireSubscriptionKind.Sharded)]
    public async Task RejectedSetupDoesNotConnect(RespireSubscriptionKind kind)
    {
        await using var server = Server();
        var rejected = new InvalidOperationException("Admission denied");
        var hook = new RecordingHook { Before = _ => throw rejected, ThrowOnCompleted = true };
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var error = await Failure(async () => _ = await Subscribe(client, kind));
        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.ConnectionAccepted.IsCompleted).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo(SubscribeName(kind));
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(completion.Error).IsSameReferenceAs(rejected);
        await Assert.That(completion.State).IsNull();
    }

    [Test]
    [Arguments(RespireSubscriptionKind.Channel)]
    [Arguments(RespireSubscriptionKind.Pattern)]
    [Arguments(RespireSubscriptionKind.Sharded)]
    public async Task SetupAndTeardownCompleteAtTheirResourceBoundaries(RespireSubscriptionKind kind)
    {
        await using var server = Server();
        var setupReceived = Signal();
        var teardownReceived = Signal();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        server.SuppressReply = command =>
        {
            if (command.StartsWith(SubscribeName(kind) + " ", StringComparison.Ordinal)) setupReceived.TrySetResult();
            else teardownReceived.TrySetResult();
            return true;
        };
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var setup = Subscribe(client, kind);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await setupReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hook.Completed.Count).IsEqualTo(0);
        await server.SendRawAsync(Confirmation(SubscribeName(kind)));
        var subscription = await setup;
        await Assert.That(subscription.IsDisposed).IsFalse();
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        var teardown = subscription.DisposeAsync();
        await teardownReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
        await server.SendRawAsync(Confirmation(UnsubscribeName(kind)));
        await teardown;
        await subscription.DisposeAsync();
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.Disposed);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Entered.Select(context => context.Command))
            .IsEquivalentTo([SubscribeName(kind), UnsubscribeName(kind)]);
        await Assert.That(hook.Completed.All(result => result.Outcome == RespireCommandOutcome.Succeeded
            && result.Error is null && ReferenceEquals(result.Context, result.State))).IsTrue();
    }

    [Test]
    public async Task RejectedTeardownKeepsDeliveryLiveAndCanBeRetried()
    {
        await using var server = Server();
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var subscription = await client.SubscribeAsync("ch");
        var rejected = new InvalidOperationException("Keep subscription");
        hook.Before = _ => throw rejected;
        var error = await Failure(async () => await subscription.DisposeAsync());
        await Assert.That(error).IsSameReferenceAs(rejected);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(subscription.IsDisposed).IsFalse();
        await Assert.That(subscription.Completion.IsCompleted).IsFalse();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await server.SendRawAsync("*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$5\r\nhello\r\n"u8.ToArray());
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("hello");
        hook.Before = null;
        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
        await Assert.That(hook.Completed.Select(result => result.Outcome))
            .IsEquivalentTo([RespireCommandOutcome.Succeeded, RespireCommandOutcome.Rejected, RespireCommandOutcome.Succeeded]);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SUBSCRIBE ch", "UNSUBSCRIBE ch"]);
    }

    [Test]
    public async Task ConcurrentAndReentrantTeardownShareOneAdmission()
    {
        await using var server = Server();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This reentrancy/concurrency scenario owns its client and shared callback state until all calls finish.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var subscription = await client.SubscribeAsync("ch");
        var received = Signal();
        server.SuppressReply = command =>
        {
            if (command != "UNSUBSCRIBE ch") return false;
            received.TrySetResult();
            return true;
        };
        hook.Before = context =>
        {
            if (context.Command == "UNSUBSCRIBE")
            {
                var duplicate = subscription.DisposeAsync();
                if (!duplicate.IsCompletedSuccessfully) throw new InvalidOperationException("Reentrant disposal did not finish synchronously.");
                _ = duplicate.AsTask();
            }
        };
        var teardown = subscription.DisposeAsync();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await subscription.DisposeAsync();
        await Assert.That(hook.Entered.Count).IsEqualTo(2);
        await server.SendRawAsync(Confirmation("UNSUBSCRIBE"));
        await teardown;
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(command => command == "UNSUBSCRIBE ch")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SetupCancellationCompletesOnce(bool pending)
    {
        await using var server = Server();
        var received = Signal();
        server.SuppressReply = _ => { received.TrySetResult(); return true; };
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This cancellation scenario owns its client, hook state and scripted in-flight reply boundaries.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        using var cancellation = new CancellationTokenSource();
        if (!pending) cancellation.Cancel();
        var setup = client.SubscribeAsync("ch", cancellationToken: cancellation.Token);
        if (pending)
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var error = await Failure(async () => _ = await setup);
        await Assert.That(error is OperationCanceledException).IsTrue();
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(completion.Context.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(server.CommandsSeen).IsEqualTo(pending ? 1 : 0);
    }

    [Test]
    public async Task SetupServerFailureCompletesOnceAfterCleanup()
    {
        await using var server = Server();
        server.ReplyOverride = (_, _) => "-ERR subscription denied\r\n"u8.ToArray();
        var hook = new RecordingHook { ThrowOnCompleted = true };
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var error = await Failure(async () => _ = await client.SubscribeAsync("ch"));
        var completion = hook.Completed.Single();
        await Assert.That(completion.Outcome).IsEqualTo(RespireCommandOutcome.Failed);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RecoveryAndClientDisposalDoNotInvokeApplicationHooks()
    {
        await using var server = Server();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var subscription = await client.SubscribeAsync("ch");
        hook.Before = _ => throw new InvalidOperationException("No application command expected");
        var recovered = Signal();
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        server.CloseConnection(server.ReceivedConnectionIds[0]);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands.Count(command => command == "SUBSCRIBE ch")).IsEqualTo(2);
        await client.DisposeAsync();
        await subscription.DisposeAsync();
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CallbackStartedApplicationCommandHasItsOwnScope()
    {
        await using var server = Server();
        var hook = new RecordingHook();
#pragma warning disable RESP010 // This subscription hook scenario owns its client and setup/teardown confirmation state.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        Task<string?>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == "SUBSCRIBE") nested = client.GetStringAsync("key").AsTask();
        };
        await using var subscription = await client.SubscribeAsync("ch");
        await Assert.That(await nested!).IsEqualTo("OK");
        await Assert.That(hook.Entered.Select(context => context.Command)).IsEquivalentTo(["SUBSCRIBE", "GET"]);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
    }

    [Test]
    public async Task InvalidOptionsRemainPreflightErrors()
    {
        await using var server = Server();
        var hook = new RecordingHook { Before = _ => throw new InvalidOperationException("Rejected") };
#pragma warning disable RESP010 // This preflight scenario owns isolated client options and hooks to verify rejection before I/O.
        await using var client = RespireClient.Create(Options(server, hook));
#pragma warning restore RESP010
        var error = await Failure(async () => _ = await client.SubscribeAsync("ch", new(BufferSize: 0)));
        await Assert.That(error is ArgumentOutOfRangeException).IsTrue();
        await Assert.That(hook.Entered.Count).IsEqualTo(0);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    private static ValueTask<RespireSubscription> Subscribe(RespireClient client, RespireSubscriptionKind kind)
        => client.SubscribeAsync(kind switch
        {
            RespireSubscriptionKind.Pattern => RespireChannel.Pattern("ch"),
            RespireSubscriptionKind.Sharded => RespireChannel.Sharded("ch"),
            _ => (RespireChannel)"ch",
        });

    private static string SubscribeName(RespireSubscriptionKind kind) => kind switch
    {
        RespireSubscriptionKind.Pattern => "PSUBSCRIBE", RespireSubscriptionKind.Sharded => "SSUBSCRIBE", _ => "SUBSCRIBE",
    };

    private static string UnsubscribeName(RespireSubscriptionKind kind) => kind switch
    {
        RespireSubscriptionKind.Pattern => "PUNSUBSCRIBE", RespireSubscriptionKind.Sharded => "SUNSUBSCRIBE", _ => "UNSUBSCRIBE",
    };

    private static byte[] Confirmation(string operation)
        => Encoding.UTF8.GetBytes($"*3\r\n${operation.Length}\r\n{operation.ToLowerInvariant()}\r\n$2\r\nch\r\n:1\r\n");

    private static FakeRespServer Server() => new(8, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command.Contains("SUBSCRIBE", StringComparison.Ordinal)
            ? Confirmation(command.Split(' ')[0]) : null,
    };

    private static RespireOptions Options(FakeRespServer server, RespireCommandHook hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5), CommandHook = hook,
        ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(20), JitterRatio = 0, MaxAttempts = 2 },
    };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    private sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal bool ThrowOnCompleted;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, state, outcome, error));
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failure");
        }
    }
}
