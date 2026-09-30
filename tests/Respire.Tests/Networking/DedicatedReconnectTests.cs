using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedReconnectTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static RespireReconnectPolicy Policy(int attempts = 2, int milliseconds = 25) => new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(milliseconds),
        MaxDelay = TimeSpan.FromMilliseconds(milliseconds * 2),
        JitterRatio = 0,
        MaxAttempts = attempts,
    };

    [Test]
    public async Task FailedHandshakesBackOffAndConcurrentRentsHaveIndependentBudgets()
    {
        await using var server = new FakeRespServer(9, "-LOADING dataset\r\n"u8.ToArray());
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhaustedCount = 0;
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy() }, null, change =>
            {
                changes.Enqueue(change);
                if (change.ReconnectExhausted && Interlocked.Increment(ref exhaustedCount) == 3) exhausted.TrySetResult();
            });
        using var deadline = new CancellationTokenSource(Deadline);
        var start = Stopwatch.GetTimestamp();
        async Task Rent()
        {
            var error = await Assert.That(async () => await pool.RentAsync(deadline.Token))
                .ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(error!.InnerException).IsTypeOf<RespireConnectionException>();
        }
        await Task.WhenAll(Rent(), Rent());
        await Assert.That(Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(70)).IsTrue();
        await Rent(); // A fresh rent can retry even after other rents exhaust.
        await exhausted.Task.WaitAsync(deadline.Token);
        await Assert.That(server.CommandsSeen).IsEqualTo(9);
        await Assert.That(changes.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(3);
        foreach (var episode in changes.GroupBy(change => change.ReconnectEpisodeId))
        {
            await Assert.That(episode.Key > 0).IsTrue();
            var ordered = episode.ToArray();
            await Assert.That(ordered.Length).IsEqualTo(3);
            await Assert.That(ordered[0].ReconnectAttempt).IsEqualTo(1);
            await Assert.That(ordered[0].NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(25));
            await Assert.That(ordered[1].ReconnectAttempt).IsEqualTo(2);
            await Assert.That(ordered[1].NextReconnectDelay).IsEqualTo(TimeSpan.FromMilliseconds(50));
            await Assert.That(ordered[2].ReconnectExhausted).IsTrue();
        }
    }

    [Test]
    [Arguments("WRONGPASS")]
    [Arguments("NOAUTH")]
    [Arguments("NOPERM")]
    [Arguments("ERR")]
    public async Task PermanentHandshakeRejectionFailsWithoutRetrying(string code)
    {
        await using var server = new FakeRespServer(System.Text.Encoding.ASCII.GetBytes($"-{code} denied\r\n"));
        var events = 0;
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy() with { MaxAttempts = null } }, null,
            _ => Interlocked.Increment(ref events));
        using var deadline = new CancellationTokenSource(Deadline);
        var error = await Assert.That(async () => await pool.RentAsync(deadline.Token)).ThrowsExactly<RespireConnectionException>();
        var serverError = await Assert.That(error!.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(serverError!.Code).IsEqualTo(code);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref events)).IsEqualTo(0);
    }

    [Test]
    [Arguments("+OK\r\n", false)]
    [Arguments("%1\r\n$5\r\nproto\r\n:2\r\n", false)]
    [Arguments("%0\r\n", false)]
    [Arguments("+OK\r\n", true)]
    [Arguments("%1\r\n$5\r\nproto\r\n:2\r\n", true)]
    [Arguments("%0\r\n", true)]
    public async Task InvalidHelloResponseFailsWithoutRetrying(string reply, bool afterTransportFailure)
    {
        await using var server = new FakeRespServer(2, System.Text.Encoding.ASCII.GetBytes(reply))
        {
            CloseConnectionAfterCommand = afterTransportFailure ? 1 : null,
        };
        var terminal = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { UseResp3 = true, ReconnectPolicy = Policy() with { MaxAttempts = null } }, null,
            change => { if (change.State == RespireConnectionState.Disconnected) terminal.TrySetResult(change); });
        using var deadline = new CancellationTokenSource(Deadline);
        var error = await Assert.That(async () => await pool.RentAsync(deadline.Token)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException).IsTypeOf<RespireProtocolException>();
        await Assert.That(error.Message).Contains("server did not confirm RESP3");
        await Assert.That(server.CommandsSeen).IsEqualTo(afterTransportFailure ? 2 : 1);
        if (afterTransportFailure)
        {
            var stopped = await terminal.Task.WaitAsync(deadline.Token);
            await Assert.That(stopped.ReconnectExhausted).IsFalse();
            await Assert.That(stopped.ReconnectAttempt).IsEqualTo(1);
        }
    }

    [Test]
    public async Task PermanentRejectionAfterTransportFailureEndsEpisodeWithoutExhaustion()
    {
        await using var server = new FakeRespServer(2, "-WRONGPASS denied\r\n"u8.ToArray()) { CloseConnectionAfterCommand = 1 };
        var terminal = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy() with { MaxAttempts = null } }, null,
            change => { if (change.State == RespireConnectionState.Disconnected) terminal.TrySetResult(change); });
        using var deadline = new CancellationTokenSource(Deadline);
        await Assert.That(async () => await pool.RentAsync(deadline.Token)).ThrowsExactly<RespireConnectionException>();
        var stopped = await terminal.Task.WaitAsync(deadline.Token);
        await Assert.That(stopped.ReconnectExhausted).IsFalse();
        await Assert.That(stopped.ReconnectAttempt).IsEqualTo(1);
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task RecoveryPublishesDedicatedTelemetryAndIdleReuseDoesNotStartAnotherEpisode()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply) { CloseConnectionAfterCommand = 1 };
        var recovered = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var measured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.connection.reconnect.delay")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var matchingPort = false;
            var dedicated = false;
            foreach (var tag in tags)
            {
                if (tag.Key == "server.port" && tag.Value is int port && port == server.Port) matchingPort = true;
                if (tag.Key == "respire.connection.source" && Equals(tag.Value, "dedicated")) dedicated = true;
            }
            if (matchingPort && dedicated && value == 0.025) measured.TrySetResult();
        });
        listener.Start();
        var eventCount = 0;
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy() }, null, change =>
            {
                Interlocked.Increment(ref eventCount);
                if (change.State == RespireConnectionState.Connected) recovered.TrySetResult(change);
            });
        using var deadline = new CancellationTokenSource(Deadline);
        var connection = await pool.RentAsync(deadline.Token);
        var success = await recovered.Task.WaitAsync(deadline.Token);
        await measured.Task.WaitAsync(deadline.Token);
        await Assert.That(success.ReconnectSource).IsEqualTo(RespireReconnectSource.Dedicated);
        await Assert.That(success.ReconnectAttempt).IsEqualTo(1);
        await Assert.That(success.ConnectionSlot).IsNull();
        pool.Return(connection);
        var reused = await pool.RentAsync(deadline.Token);
        await Assert.That(reused).IsSameReferenceAs(connection);
        await Assert.That(Volatile.Read(ref eventCount)).IsEqualTo(2);
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task BackoffCancelsForCallerRetirementAndDisposal(int stop)
    {
        await using var server = new FakeRespServer("-LOADING dataset\r\n"u8.ToArray());
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy(milliseconds: 30_000) }, null,
            change => { if (change.NextReconnectDelay is not null) waiting.TrySetResult(); });
        using var caller = new CancellationTokenSource();
        var pending = pool.RentAsync(caller.Token).AsTask();
        await waiting.Task.WaitAsync(Deadline);
        if (stop == 0) caller.Cancel();
        else if (stop == 1) await pool.RetireAsync().AsTask().WaitAsync(Deadline);
        else await pool.DisposeAsync().AsTask().WaitAsync(Deadline);
        var error = await Assert.That(async () => await pending.WaitAsync(Deadline)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken == caller.Token).IsEqualTo(stop == 0);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NullPolicyAndCorrectiveFencesDoNotRetry(bool fencing)
    {
        await using var server = new FakeRespServer("-ERR denied\r\n"u8.ToArray());
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = fencing ? Policy() : null }, null);
        await Assert.That(async () => await pool.RentAsync(CancellationToken.None, armHandshakeDeadline: !fencing))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task AcceptedCommandIsNotReplayed()
    {
        await using var server = new FakeRespServer { CloseConnectionAfterCommand = 1 };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { ReconnectPolicy = Policy() }, null);
        var connection = await pool.RentAsync(CancellationToken.None);
        await Assert.That(async () => await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
            .AsTask().WaitAsync(Deadline)).Throws<RespireConnectionException>();
        await pool.DiscardAsync(connection);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementCancelsReplacementHandshakeButWaitsForBorrowedConnection()
    {
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selects = 0;
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = 2,
            SuppressReply = _ =>
            {
                if (Interlocked.Increment(ref selects) == 1) return false;
                handshake.TrySetResult();
                return true;
            },
        };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy() }, null);
        using var deadline = new CancellationTokenSource(Deadline);
        var borrowed = await pool.RentAsync(deadline.Token);
        var pending = pool.RentAsync(deadline.Token).AsTask();
        await handshake.Task.WaitAsync(deadline.Token);
        var retirement = pool.RetireAsync().AsTask();
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(borrowed.IsConnected).IsTrue();
        pool.Return(borrowed);
        await retirement.WaitAsync(deadline.Token);
        await Assert.That(borrowed.IsConnected).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EachReplacementHandshakeRetainsItsCommandDeadline(bool holdServerReceipt)
    {
        var acceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, holdServerReceipt ? acceptGate.Task : Task.CompletedTask)
            { SuppressReply = _ => true };
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromMilliseconds(50);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                Database = 1, CommandTimeout = timeout, ReconnectPolicy = Policy(attempts: 1),
            }, null, change =>
            {
                changes.Enqueue(change);
                if (change.ReconnectExhausted) exhausted.TrySetResult();
            });
        using var deadline = new CancellationTokenSource(Deadline);
        var error = await Assert.That(async () => await pool.RentAsync(deadline.Token))
            .ThrowsExactly<RespireReconnectLimitException>();
        await exhausted.Task.WaitAsync(deadline.Token);
        var attempts = changes.ToArray();
        await Assert.That(attempts.Length).IsEqualTo(2);
        await Assert.That(attempts[0].State).IsEqualTo(RespireConnectionState.Reconnecting);
        await Assert.That(attempts[0].ReconnectAttempt).IsEqualTo(1);
        await Assert.That(attempts[0].ReconnectExhausted).IsFalse();
        await Assert.That(attempts[1].State).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(attempts[1].ReconnectAttempt).IsEqualTo(1);
        await Assert.That(attempts[1].ReconnectExhausted).IsTrue();
        await Assert.That(attempts[1].ReconnectEpisodeId).IsEqualTo(attempts[0].ReconnectEpisodeId);
        foreach (var attempt in attempts)
        {
            var failure = await Assert.That(attempt.Error).IsTypeOf<RespireTimeoutException>();
            await Assert.That(failure!.CommandName).IsEqualTo("SELECT");
            await Assert.That(failure.Timeout).IsEqualTo(timeout);
            await Assert.That(failure.Diagnostics.ConnectionId).IsNotNull();
        }
        var initial = (RespireTimeoutException)attempts[0].Error!;
        var replacement = (RespireTimeoutException)attempts[1].Error!;
        await Assert.That(replacement.Diagnostics.ConnectionId).IsNotEqualTo(initial.Diagnostics.ConnectionId);
        await Assert.That(error!.InnerException).IsSameReferenceAs(replacement);
        // Command deadlines start at enqueue. Neither timeout proves server receipt.
        if (holdServerReceipt) await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await Assert.That(deadline.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task CallerCancellationAfterHandshakeReceiptDoesNotConsumeReplacementBudget()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (command == "SELECT 1") received.TrySetResult();
                return true;
            },
        };
        var recoveryEvents = 0;
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, ReconnectPolicy = Policy(attempts: 1) }, null,
            _ => Interlocked.Increment(ref recoveryEvents));
        using var caller = new CancellationTokenSource();
        var pending = pool.RentAsync(caller.Token).AsTask();
        try
        {
            await received.Task.WaitAsync(Deadline);
            caller.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(Deadline)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await server.PeerClosed.WaitAsync(Deadline);
            await Assert.That(server.CommandsSeen).IsEqualTo(1);
            await Assert.That(Volatile.Read(ref recoveryEvents)).IsEqualTo(0);
        }
        finally
        {
            caller.Cancel();
            await pool.DisposeAsync();
            try { await pending.WaitAsync(Deadline); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    public async Task PublicEventsPreserveCommandHealthAndAllowSynchronousDisposal()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply) { CloseConnectionAfterCommand = 2 };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) }, Connections = 1,
            Database = 1, ReconnectPolicy = Policy(milliseconds: 30_000),
        });
        var disposed = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.Dedicated) return;
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposed.TrySetResult(change);
        };
        var pending = client.Core.DedicatedPool.RentAsync(CancellationToken.None).AsTask();
        var change = await disposed.Task.WaitAsync(Deadline);
        await Assert.That(change.State).IsEqualTo(RespireConnectionState.Connected);
        await Assert.That(change.SourceState).IsEqualTo(RespireConnectionState.Reconnecting);
        await Assert.That(async () => await pending.WaitAsync(Deadline)).Throws<OperationCanceledException>();
    }
}
