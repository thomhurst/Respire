using System.Net;
using System.Net.Sockets;
using System.Text;
using Respire.Commands;
using Respire.Networking;
using Respire.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TransportRetirementTests
{
    [Test]
    public async Task AcceptedReplyDrainsWhileNewAndCapacityWaitingCommandsAreRejected()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { MaxInflightCommands = 1 });
        var command = new RawCommand(FakeRespServer.PingFrame);
        var accepted = connection.SendAsync(command).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = connection.SendAsync(command).AsTask();
        var retirement = connection.RetireAsync();
        await Assert.That(connection.RetireAsync()).IsSameReferenceAs(retirement);
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(connection.IsAcceptingCommands).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(async () => await waiting).ThrowsExactly<RespireConnectionRetiredException>();
        await Assert.That(async () => await connection.SendAsync(command)).ThrowsExactly<RespireConnectionRetiredException>();
        await server.SendRawAsync(FakeRespServer.PongReply);
        using var reply = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task ExplicitDisposalAbortsAStalledDrainAndSharesCleanupCompletion()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var accepted = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = connection.RetireAsync();
        var firstDispose = connection.DisposeAsync().AsTask();
        var secondDispose = connection.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose, retirement).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await accepted).ThrowsExactly<RespireConnectionException>();
        await Assert.That(connection.DrainedSuccessfully).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CommandTimeoutKeepsAcceptedReplyOwnedUntilDrainOrAbort(bool abort)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { CommandTimeout = TimeSpan.FromMilliseconds(150) });
        var accepted = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = connection.RetireAsync();
        await Assert.That(async () => await accepted.WaitAsync(TimeSpan.FromSeconds(5))).ThrowsExactly<RespireTimeoutException>();
        // A timeout abandons the caller's wait, not the FIFO slot or server-side operation.
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(connection.IsConnected).IsTrue();
        if (abort) await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else await server.SendRawAsync(FakeRespServer.PongReply);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.DrainedSuccessfully).IsEqualTo(!abort);
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task MultiplexerStopsSelectionButDrainsAnAcceptedReply()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        var heldConnection = multiplexer.GetConnection();
        var accepted = multiplexer.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = multiplexer.RetireAsync();
        await Assert.That(multiplexer.RetireAsync()).IsSameReferenceAs(retirement);
        await Assert.That(multiplexer.IsConnected).IsFalse();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(() => multiplexer.GetConnection()).ThrowsExactly<RespireConnectionRetiredException>();
        await Assert.That(async () => await heldConnection.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            .ThrowsExactly<RespireConnectionRetiredException>();
        await server.SendRawAsync(FakeRespServer.PongReply);
        using var reply = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsFalse();
    }

    [Test]
    public async Task MultiplexerDisposalInterruptsRetirement()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        var accepted = multiplexer.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = multiplexer.RetireAsync();
        await multiplexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await accepted).ThrowsExactly<RespireConnectionException>();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RetirementCancelsUnpublishedHandshake(bool reconnect, bool dispose)
    {
        var blockedHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshakes = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("CLIENT SETNAME ", StringComparison.Ordinal)) return false;
                if (Interlocked.Increment(ref handshakes) == 1 && reconnect) return false;
                blockedHandshake.TrySetResult();
                return true;
            }
        };
        await using var multiplexer = RespireConnectionMultiplexer.Create("127.0.0.1", server.Port,
            options: new RespireConnectionOptions { ClientName = "retirement-test" });
        Task? initialization = null;
        if (reconnect)
        {
            await multiplexer.EnsureConnectedAsync();
            await multiplexer.GetConnection().DisposeAsync();
            await Assert.That(() => multiplexer.GetConnection()).ThrowsExactly<RespireConnectionException>();
        }
        else initialization = multiplexer.EnsureConnectedAsync().AsTask();
        await blockedHandshake.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = multiplexer.RetireAsync();
        if (dispose) await multiplexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        if (initialization is not null)
            await Assert.That(async () => await initialization).Throws<OperationCanceledException>();
        await Assert.That(multiplexer.IsConnected).IsFalse();
        if (dispose)
        {
            await Assert.That(() => multiplexer.GetConnection()).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(async () => await multiplexer.EnsureConnectedAsync()).ThrowsExactly<ObjectDisposedException>();
        }
        else
        {
            await Assert.That(() => multiplexer.GetConnection()).ThrowsExactly<RespireConnectionRetiredException>();
            await Assert.That(async () => await multiplexer.EnsureConnectedAsync()).ThrowsExactly<RespireConnectionRetiredException>();
        }
        await Assert.That(handshakes).IsEqualTo(reconnect ? 2 : 1);
    }

    [Test]
    public async Task RetirementFencesAnAlreadyFailedTrackedConnection()
    {
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        await multiplexer.EnsureReliableCorrectionOrderingAsync();
        await multiplexer.GetConnection().DisposeAsync();
        await multiplexer.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsFalse();
        await Assert.That(multiplexer.IsConnected).IsFalse();
    }

    [Test]
    public async Task FailedRetirementFenceRetainsItsIdForAnExplicitRetry()
    {
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray())
        {
            CloseConnectionAfterCommand = 2
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        var connection = multiplexer.GetConnection();
        // An identity obtained during partial bootstrap still needs a fence.
        await connection.EnsureServerClientIdAsync();
        await connection.DisposeAsync();
        await Assert.That(async () => await multiplexer.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsTrue();
        await multiplexer.FenceRetiredConnectionsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT KILL ID 42")).IsEqualTo(2);
    }

    [Test]
    public async Task DisposalCancelsAStalledRetirementFence()
    {
        var fenceSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                fenceSeen.TrySetResult();
                return true;
            }
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        var connection = multiplexer.GetConnection();
        await connection.EnsureServerClientIdAsync();
        await connection.DisposeAsync();
        var retirement = multiplexer.RetireAsync();
        await fenceSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await multiplexer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await retirement).Throws<OperationCanceledException>();
        await Assert.That(multiplexer.HasPendingCorrectionFences).IsTrue();
    }

    [Test]
    public async Task ReconnectNotificationCanRetireBeforeTheConnectStarts()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        Task? retirement = null;
        multiplexer.StateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting) retirement = multiplexer.RetireAsync();
        };
        await multiplexer.GetConnection().DisposeAsync();
        await Assert.That(() => multiplexer.GetConnection()).ThrowsExactly<RespireConnectionRetiredException>();
        await Assert.That(retirement).IsNotNull();
        await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(multiplexer.IsConnected).IsFalse();
    }

    [Test]
    public async Task CancelledCallerStillLeavesAnAcceptedReplyToDrain()
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ => { seen.TrySetResult(); return true; }
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var caller = new CancellationTokenSource();
        var abandoned = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), caller.Token).AsTask();
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.That(async () => await abandoned).ThrowsExactly<OperationCanceledException>();
        var retirement = connection.RetireAsync();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await server.SendRawAsync(FakeRespServer.PongReply);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
    }

    [Test]
    public async Task RetirementAfterDisposalSharesAlreadyCompletedCleanup()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port);
        await multiplexer.DisposeAsync();
        await multiplexer.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await multiplexer.DisposeAsync();
        await Assert.That(multiplexer.IsConnected).IsFalse();
    }

    [Test]
    public async Task RetirementFinishesTheEntireAcceptedFrameBeforeClosing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port);
        using var peer = await accept;
        const int payloadLength = 4 * 1024 * 1024;
        var header = Encoding.ASCII.GetBytes($"*2\r\n$4\r\nECHO\r\n${payloadLength}\r\n");
        var frame = new byte[header.Length + payloadLength + 2];
        header.CopyTo(frame, 0);
        frame.AsSpan(header.Length, payloadLength).Fill((byte)'x');
        frame[^2] = (byte)'\r';
        frame[^1] = (byte)'\n';
        var response = connection.SendAsync(new RawCommand(frame)).AsTask();
        var retirement = connection.RetireAsync();
        var received = new byte[frame.Length];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var count = 0;
        while (count < received.Length)
        {
            var read = await peer.ReceiveAsync(received.AsMemory(count), SocketFlags.None, deadline.Token);
            if (read == 0) throw new InvalidOperationException("Retirement truncated the accepted frame.");
            count += read;
        }
        await Assert.That(received.AsSpan().SequenceEqual(frame)).IsTrue();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await peer.SendAsync(FakeRespServer.OkReply, SocketFlags.None, deadline.Token);
        using var reply = await response.WaitAsync(deadline.Token);
        await retirement.WaitAsync(deadline.Token);
        await Assert.That(reply.AsString()).IsEqualTo("OK");
    }
}
