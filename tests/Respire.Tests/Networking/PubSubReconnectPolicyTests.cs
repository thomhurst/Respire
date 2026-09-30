using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PubSubReconnectPolicyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly byte[] Confirmation = "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray();
    private static readonly byte[] Rejection = "-ERR subscription denied\r\n"u8.ToArray();
    private static RespireReconnectPolicy Policy(int milliseconds = 25, int attempts = 2) => new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(milliseconds), MaxDelay = TimeSpan.FromMilliseconds(milliseconds * 2),
        JitterRatio = 0, MaxAttempts = attempts,
    };
    private static RespireOptions Options(int port, RespireReconnectPolicy? policy, int database = 0) => new()
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", port) }, Connections = 1, Database = database,
        ReconnectPolicy = policy,
    };
    private static async Task WaitForCommandsAsync(FakeRespServer server, int count, CancellationToken token)
    {
        while (server.CommandsSeen < count) await Task.Delay(5, token);
    }

    [Test]
    public async Task FailedResubscriptionsShareOneBudgetAndExhaustionEndsLiveSubscriptions()
    {
        await using var server = new FakeRespServer(3, Confirmation) { CloseConnectionAfterCommand = 2 };
        await using var client = RespireClient.Create(Options(server.Port, Policy()));
        await using var subscription = await client.SubscribeAsync("ch");
        using var deadline = new CancellationTokenSource(Deadline);
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        var pendingRead = reader.MoveNextAsync().AsTask();
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.ReconnectExhausted) terminal.TrySetResult();
        };
        long exhaustions = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.connection.reconnect.exhausted")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var endpoint = false;
            var pubsub = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.port" && Equals(tag.Value, server.Port)) endpoint = true;
                if (tag.Key == "respire.connection.source" && Equals(tag.Value, "pubsub")) pubsub = true;
            }
            if (endpoint && pubsub) Interlocked.Add(ref exhaustions, value);
        });
        listener.Start();
        server.SuppressReply = _ => true;
        await Assert.That(async () => await client.SubscribeAsync("lost", deadline.Token))
            .Throws<RespireConnectionException>();
        for (var count = 3; count <= 4; count++)
        {
            await WaitForCommandsAsync(server, count, deadline.Token);
            await server.SendRawAsync(Rejection, server.ReceivedConnectionIds[^1]);
        }
        await terminal.Task.WaitAsync(deadline.Token);
        await Assert.That(await subscription.Completion.WaitAsync(deadline.Token)).IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(await pendingRead.WaitAsync(deadline.Token)).IsFalse();
        await Assert.That(subscription.IsDisposed).IsTrue();
        await Assert.That(async () => await client.SubscribeAsync("later", deadline.Token))
            .ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(4);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(3);
        var states = changes.ToArray();
        await Assert.That(states.Length).IsEqualTo(3);
        await Assert.That(states[0].ReconnectAttempt).IsEqualTo(1);
        await Assert.That(states[0].NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(25));
        await Assert.That(states[1].ReconnectAttempt).IsEqualTo(2);
        await Assert.That(states[1].NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(50));
        await Assert.That(states[2].State).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(states[2].Error).IsTypeOf<RespireServerException>();
        await Assert.That(Interlocked.Read(ref exhaustions)).IsEqualTo(1L);
    }

    [Test]
    public async Task SuccessfulResubscriptionResetsBudgetAndKeepsGapBeforeMessages()
    {
        var reply = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Confirmation)
            + "*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$5\r\nhello\r\n");
        await using var server = new FakeRespServer(3, reply) { CloseConnectionAfterCommand = 2 };
        await using var client = RespireClient.Create(Options(server.Port, Policy(attempts: 1)));
        await using var subscription = await client.SubscribeAsync("ch");
        using var deadline = new CancellationTokenSource(Deadline);
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        var attempts = new ConcurrentQueue<int>();
        var recovered = new SemaphoreSlim(0);
        client.ConnectionStateChanged += change =>
        {
            if (change.NextReconnectDelay is not null) attempts.Enqueue(change.ReconnectAttempt);
            if (change.State == RespireConnectionState.Connected) recovered.Release();
        };
        for (var episode = 0; episode < 2; episode++)
        {
            server.CloseConnectionAfterCommand = 2 + episode * 2;
            await Assert.That(async () => await client.SubscribeAsync("lost", deadline.Token))
                .Throws<RespireConnectionException>();
            await recovered.WaitAsync(deadline.Token);
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
            await Assert.That(reader.Current.Gap!.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.Text).IsEqualTo("hello");
        }
        await Assert.That(attempts.ToArray()).IsEquivalentTo(new[] { 1, 1 });
        await Assert.That(server.CommandsSeen).IsEqualTo(5);
        // Dispose the client before the subscription: the scripted server does not send
        // unsubscribe confirmations, and this test concerns recovery rather than unsubscribe.
        await client.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentChangesCannotBypassBackoffOrRetainRemovedRoutes()
    {
        await using var server = new FakeRespServer(2, Confirmation) { CloseConnectionAfterCommand = 3 };
        await using var client = RespireClient.Create(Options(server.Port, Policy(milliseconds: 500)));
        await using var retained = await client.SubscribeAsync("ch");
        await using var removed = await client.SubscribeAsync("ch");
        using var deadline = new CancellationTokenSource(Deadline);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.NextReconnectDelay is not null) waiting.TrySetResult();
            if (change.State == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        await Assert.That(async () => await client.SubscribeAsync("lost", deadline.Token)).Throws<RespireConnectionException>();
        await waiting.Task.WaitAsync(deadline.Token);
        await removed.DisposeAsync();
        await Assert.That(async () => await client.SubscribeAsync("new", deadline.Token)).ThrowsExactly<RespireConnectionException>();
        await recovered.Task.WaitAsync(deadline.Token);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SUBSCRIBE ch")).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands.Contains("SUBSCRIBE new")).IsFalse();
        await Assert.That(await removed.Completion).IsEqualTo(RespireSubscriptionEndReason.Disposed);
        await Assert.That(retained.IsDisposed).IsFalse();
        await client.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalCancelsBackoffOrReplacementHandshake(bool handshake)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply, Confirmation)
            { CloseConnectionAfterCommand = 3 };
        await using var client = RespireClient.Create(Options(server.Port, Policy(milliseconds: handshake ? 1 : 30_000), database: 1));
        await using var subscription = await client.SubscribeAsync("ch");
        using var deadline = new CancellationTokenSource(Deadline);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change => { if (change.NextReconnectDelay is not null) waiting.TrySetResult(); };
        server.SuppressReply = command => command == "SELECT 1";
        await Assert.That(async () => await client.SubscribeAsync("lost", deadline.Token)).Throws<RespireConnectionException>();
        await waiting.Task.WaitAsync(deadline.Token);
        if (handshake) await WaitForCommandsAsync(server, 4, deadline.Token);
        await client.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
        await Assert.That(server.CommandsSeen).IsEqualTo(handshake ? 4 : 3);
    }

    [Test]
    public async Task CancelledActivationPreservesCallerTokenAndRecoveryUsesJitterBounds()
    {
        await using var server = new FakeRespServer(2, Confirmation)
        {
            SuppressReply = command => command == "SUBSCRIBE stalled",
        };
        await using var client = RespireClient.Create(Options(server.Port, Policy() with { JitterRatio = 0.25 }));
        await using var subscription = await client.SubscribeAsync("ch");
        using var deadline = new CancellationTokenSource(Deadline);
        using var caller = new CancellationTokenSource();
        var scheduled = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.NextReconnectDelay is { } delay) scheduled.TrySetResult(delay);
            if (change.State == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        var pending = client.SubscribeAsync("stalled", caller.Token).AsTask();
        await WaitForCommandsAsync(server, 2, deadline.Token);
        caller.Cancel();
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await recovered.Task.WaitAsync(deadline.Token);
        var actualDelay = await scheduled.Task.WaitAsync(deadline.Token);
        await Assert.That(actualDelay >= TimeSpan.FromMilliseconds(18.75)
            && actualDelay <= TimeSpan.FromMilliseconds(31.25)).IsTrue();
        await Assert.That(subscription.IsDisposed).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
        await client.DisposeAsync();
    }

    [Test]
    public async Task RecoveryObserverCanDisposeSynchronously()
    {
        await using var server = new FakeRespServer(Confirmation) { CloseConnectionAfterCommand = 2 };
        await using var client = RespireClient.Create(Options(server.Port, Policy(milliseconds: 30_000)));
        await using var subscription = await client.SubscribeAsync("ch");
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.NextReconnectDelay is null) return;
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };
        await Assert.That(async () => await client.SubscribeAsync("lost")).Throws<RespireConnectionException>();
        await disposed.Task.WaitAsync(Deadline);
        await Assert.That(await subscription.Completion).IsEqualTo(RespireSubscriptionEndReason.ClientDisposed);
    }
}
