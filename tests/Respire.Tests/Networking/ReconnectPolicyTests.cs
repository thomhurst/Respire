using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReconnectPolicyTests
{
    [Test]
    public async Task BackoffHasExactBoundsAndCannotOverflow()
    {
        var policy = new RespireReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1), JitterRatio = 0.25 };
        policy.Validate();
        await Assert.That(policy.GetDelay(1, 0)).IsEqualTo(TimeSpan.FromMilliseconds(75));
        await Assert.That(policy.GetDelay(1, 1)).IsEqualTo(TimeSpan.FromMilliseconds(125));
        await Assert.That(policy.GetDelay(3, 0.5)).IsEqualTo(TimeSpan.FromMilliseconds(400));
        await Assert.That(policy.GetDelay(int.MaxValue, 1)).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That((policy with { InitialDelay = TimeSpan.Zero }).GetDelay(int.MaxValue, 1)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task InvalidPoliciesFailBeforeConnecting()
    {
        RespireReconnectPolicy[] invalid = [new() { InitialDelay = TimeSpan.FromTicks(-1) },
            new() { MaxDelay = TimeSpan.Zero }, new() { MaxDelay = TimeSpan.FromDays(2) },
            new() { BackoffMultiplier = double.NaN }, new() { BackoffMultiplier = double.PositiveInfinity },
            new() { BackoffMultiplier = 0.5 }, new() { JitterRatio = -0.1 }, new() { JitterRatio = 1.1 },
            new() { JitterRatio = double.NaN }, new() { MaxAttempts = 0 }];
        foreach (var policy in invalid)
            await Assert.That(() => RespireClient.Create(new RespireOptions { ReconnectPolicy = policy }))
                .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task ConcurrentWaitersShareBoundedAttemptsAndExhaustionPersists()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromMilliseconds(50), MaxDelay = TimeSpan.FromMilliseconds(100), JitterRatio = 0, MaxAttempts = 2 }));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult();
        };
        var original = client.Core.Multiplexer.GetConnection();
        await server.DisposeAsync();
        await original.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiters = Enumerable.Range(0, 8).Select(async _ =>
        {
            await Assert.That(async () => await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token))
                .ThrowsExactly<RespireReconnectLimitException>();
        }).ToArray();
        await Task.WhenAll(waiters);
        await exhausted.Task.WaitAsync(deadline.Token);
        for (var index = 0; index < 5; index++)
            await Assert.That(() => client.Core.Multiplexer.GetConnection()).ThrowsExactly<RespireReconnectLimitException>();
        var attempts = changes.Where(change => change.State == RespireConnectionState.Reconnecting).ToArray();
        await Assert.That(attempts.Select(change => change.ReconnectAttempt).ToArray()).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(attempts.Select(change => change.NextReconnectDelay).ToArray())
            .IsEquivalentTo(new TimeSpan?[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }, CollectionOrdering.Matching);
        await Assert.That(changes.Last().ReconnectExhausted).IsTrue();
    }

    [Test]
    public async Task SuccessfulReplacementResetsAttemptCountAndEmitsTelemetry()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0, MaxAttempts = 1 }));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += changes.Enqueue;
        var measured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.connection.reconnect.attempt")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && tag.Value is int port && port == server.Port && value == 1)
                    measured.TrySetResult();
        });
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 2; index++)
        {
            await client.Core.Multiplexer.GetConnection().DisposeAsync();
            await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
            await client.PingAsync(deadline.Token);
        }
        await measured.Task.WaitAsync(deadline.Token);
        await Assert.That(changes.Where(change => change.State == RespireConnectionState.Reconnecting)
            .Select(change => change.ReconnectAttempt).ToArray()).IsEquivalentTo(new[] { 1, 1 }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "PING", "PING" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RecoveryDoesNotReplayAnAcceptedCommand()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply) { CloseConnectionAfterCommand = 1 };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 }));
        await Assert.That(async () => await client.PingAsync()).Throws<RespireConnectionException>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task CallerCancellationLeavesSharedRecoveryUntilClientDisposalCancelsIt()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromHours(1), MaxDelay = TimeSpan.FromHours(1), JitterRatio = 0 }));
        await client.Core.Multiplexer.GetConnection().DisposeAsync();
        using var cancelled = new CancellationTokenSource();
        var waiting = client.Core.Multiplexer.GetHealthyConnectionAsync(cancelled.Token).AsTask();
        cancelled.Cancel();
        await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
        await Assert.That(client.Core.Multiplexer.IsReconnecting).IsTrue();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(client.Core.Multiplexer.IsReconnecting).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ReconnectingHandlerCanDisposeSynchronously()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromHours(1), MaxDelay = TimeSpan.FromHours(1), JitterRatio = 0 }));
        await client.Core.Multiplexer.GetConnection().DisposeAsync();
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting)
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
        await Task.Run(async () => await Assert.That(() => client.Core.Multiplexer.GetConnection())
            .Throws<ObjectDisposedException>()).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static RespireOptions Options(int port, RespireReconnectPolicy policy) => new()
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", port) }, Connections = 1,
        ConnectTimeout = TimeSpan.FromSeconds(1), ReconnectPolicy = policy,
    };
}
