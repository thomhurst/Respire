using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Respire.Commands;
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
        byte[] unavailable = "-TRYAGAIN controlled replacement failure\r\n"u8.ToArray();
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            ReplyOverride = (connectionId, _) => connectionId == 0 ? null : unavailable,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromMilliseconds(50), MaxDelay = TimeSpan.FromMilliseconds(100), JitterRatio = 0, MaxAttempts = 2 })
            with { Database = 1 });
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhaustionMeasured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long exhaustionCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.connection.reconnect.exhausted")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && tag.Value is int port && port == server.Port)
                {
                    Interlocked.Add(ref exhaustionCount, value);
                    exhaustionMeasured.TrySetResult();
                }
        });
        listener.Start();
        client.ConnectionStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult();
        };
        var original = client.Core.Multiplexer.GetConnection();
        // Keep the listener bound: releasing this port lets another parallel fixture
        // accept a replacement. Each replacement instead fails its SELECT handshake.
        await original.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiters = Enumerable.Range(0, 8).Select(async _ =>
        {
            await Assert.That(async () => await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token))
                .ThrowsExactly<RespireReconnectLimitException>();
        }).ToArray();
        await Task.WhenAll(waiters);
        await exhausted.Task.WaitAsync(deadline.Token);
        await exhaustionMeasured.Task.WaitAsync(deadline.Token);
        await Assert.That(Interlocked.Read(ref exhaustionCount)).IsEqualTo(1L);
        for (var index = 0; index < 5; index++)
            await Assert.That(() => client.Core.Multiplexer.GetConnection()).ThrowsExactly<RespireReconnectLimitException>();
        var attempts = changes.Where(change => change.State == RespireConnectionState.Reconnecting).ToArray();
        await Assert.That(attempts.All(change => change.ReconnectSource == RespireReconnectSource.Command
            && change.SourceState == RespireConnectionState.Reconnecting)).IsTrue();
        await Assert.That(attempts.Select(change => change.ReconnectAttempt).ToArray()).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(attempts.Select(change => change.NextReconnectDelay).ToArray())
            .IsEquivalentTo(new TimeSpan?[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100) }, CollectionOrdering.Matching);
        await Assert.That(changes.Last().ReconnectExhausted).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "SELECT 1", "SELECT 1", "SELECT 1" }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedConnectionIds).IsEquivalentTo(new[] { 0, 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulReplacementResetsAttemptCountAndEmitsTelemetry(bool closeBeforeAccept)
    {
        var acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!closeBeforeAccept) acceptGate.SetResult();
        await using var server = new FakeRespServer(3, acceptGate.Task, FakeRespServer.PongReply);
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
            // On Windows, resetting a queued peer makes AcceptSocketAsync throw before
            // returning a socket. The fixture must keep accepting replacement connections.
            acceptGate.TrySetResult();
            await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
            await client.PingAsync(deadline.Token);
        }
        await measured.Task.WaitAsync(deadline.Token);
        await Assert.That(changes.Where(change => change.State == RespireConnectionState.Reconnecting)
            .Select(change => change.ReconnectAttempt).ToArray()).IsEquivalentTo(new[] { 1, 1 }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "PING", "PING" }, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdentitySetupPropagatesExhaustionWithOrWithoutCallerCancellation(bool cancellable)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 }));
        var original = client.Core.Multiplexer.GetConnection();
        await server.DisposeAsync();
        await original.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Initialize()
        {
            if (cancellable) await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync(deadline.Token);
            else await client.TryEnsureReliableCorrectionOrderingAsync();
        }
        await Assert.That(async () => await Initialize().WaitAsync(TimeSpan.FromSeconds(3)))
            .ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(client.Core.Multiplexer.HasReliableCorrectionOrdering).IsFalse();
    }

    [Test]
    public async Task IdentitySetupRequiresEachSlotEvenWhenAnotherSlotIsHealthy()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply, ":42\r\n"u8.ToArray(), FakeRespServer.PongReply);
        var handshakes = 0;
        server.SuppressReply = command =>
        {
            if (command != "CLIENT SETNAME policy-test" || Interlocked.Increment(ref handshakes) != 3) return false;
            server.SendRawAsync("-ERR rejected replacement\r\n"u8.ToArray(), 2).GetAwaiter().GetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 }) with
            { Connections = 2, ClientName = "policy-test" });
        await client.Core.Multiplexer.GetConnection(0).DisposeAsync();
        await Assert.That(async () => await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync()
            .AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(client.IsConnected).IsTrue();
        await client.PingAsync();
        await Assert.That(handshakes).IsEqualTo(3);
    }

    [Test]
    public async Task FailedFenceRetainsIdentityAfterCommandRecoveryExhaustion()
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 }));
        await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync();
        var original = client.Core.Multiplexer.GetConnection();
        // A command with an uncertain outcome keeps the server-side identity owed to the fence.
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = _ => { received.TrySetResult(); return true; };
        var pending = client.PingAsync().AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await server.DisposeAsync();
        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await original.DisposeAsync();
        await Assert.That(async () => await client.Core.Multiplexer.GetHealthyConnectionAsync(default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ThrowsExactly<RespireReconnectLimitException>();
        // Fences use a fresh control connection to the captured peer, independently of
        // ordinary slot recovery. The stopped peer refuses that connection.
        var failure = await Assert.That(async () => await client.Core.Multiplexer.FenceRetiredConnectionsAsync()
            .AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Throws<Exception>();
        // A closed listener may refuse immediately or reach ConnectTimeout, depending on the OS.
        await Assert.That(failure is SocketException or RespireTimeoutException).IsTrue();
        await Assert.That(client.Core.Multiplexer.HasPendingCorrectionFences).IsTrue();
    }

    [Test]
    public async Task FenceCanCompleteWithoutRevivingExhaustedCommandRecovery()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply, ":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        server.CloseConnectionAfterCommand = 4; // SETNAME, CLIENT ID, permission check, uncertain PING.
        var handshakes = 0;
        server.SuppressReply = command =>
        {
            if (command == "CLIENT SETNAME policy-test" && Interlocked.Increment(ref handshakes) == 2)
            {
                server.SendRawAsync("-ERR rejected replacement\r\n"u8.ToArray(), 1).GetAwaiter().GetResult();
                return true;
            }
            if (command != "CLIENT KILL ID 42") return false;
            server.SendRawAsync(":1\r\n"u8.ToArray(), 2).GetAwaiter().GetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 }) with
            { ClientName = "policy-test" });
        await client.Core.Multiplexer.EnsureReliableCorrectionOrderingAsync();
        await Assert.That(async () => await client.PingAsync()).Throws<RespireConnectionException>();
        await Assert.That(async () => await client.Core.Multiplexer.GetHealthyConnectionAsync(default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(3))).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(client.Core.Multiplexer.HasPendingCorrectionFences).IsTrue();

        await client.Core.Multiplexer.FenceRetiredConnectionsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.That(client.Core.Multiplexer.HasPendingCorrectionFences).IsFalse();
        await Assert.That(handshakes).IsEqualTo(3);
        await Assert.That(() => client.Core.Multiplexer.GetConnection()).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT KILL ID 42")).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyRecoveryStillReportsItsSourceSlot()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port, new()) with { ReconnectPolicy = null });
        var scheduled = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting) scheduled.TrySetResult(change);
        };
        await client.Core.Multiplexer.GetConnection().DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.Core.Multiplexer.GetHealthyConnectionAsync(deadline.Token);
        var change = await scheduled.Task.WaitAsync(deadline.Token);
        await Assert.That(change.ConnectionSlot).IsEqualTo((int?)0);
        await Assert.That(change.ReconnectAttempt).IsEqualTo(0);
        await Assert.That(change.NextReconnectDelay).IsNull();
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyBroadcastAwaitsDelayedRecoveryAndPropagatesExhaustion(bool unavailable)
    {
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server.Port,
            new() { InitialDelay = TimeSpan.FromMilliseconds(50), JitterRatio = 0, MaxAttempts = 1 }));
        var multiplexer = client.Core.Multiplexer;
        await multiplexer.EnsureReliableCorrectionOrderingAsync();
        var original = multiplexer.GetConnection();
        await original.RetireAsync(); // A successful drain leaves no owed CLIENT KILL fence.
        await Assert.That(original.DrainedSuccessfully).IsTrue();
        if (unavailable) await server.DisposeAsync();
        var broadcast = Task.Run(async () => await multiplexer.SendToAllConnectionsAsync(new Cmd(new Verb(-1, "PING"))));
        if (unavailable)
            await Assert.That(async () => await broadcast.WaitAsync(TimeSpan.FromSeconds(3)))
                .ThrowsExactly<RespireReconnectLimitException>();
        else
        {
            await broadcast.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.That(server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
        }
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsFalse();
    }

    private static RespireOptions Options(int port, RespireReconnectPolicy policy) => new()
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", port) }, Connections = 1,
        ConnectTimeout = TimeSpan.FromSeconds(1), ReconnectPolicy = policy,
    };
}
