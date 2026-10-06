using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedHandshakeTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    [Arguments(false, 4)]
    [Arguments(true, 4)]
    [Arguments(false, 5)]
    [Arguments(true, 5)]
    public async Task CancellationAfterTcpConnectDoesNotStartHandshake(bool pooled, int cancellation)
    {
        // 0: owned deadline; 1: caller; 2/3: pool retirement/disposal;
        // 4: unrelated cancellation despite an expired deadline; 5: uncanceled owned token.
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        using var caller = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? connectTimeout = null;
        CancellationToken timeoutToken = default;
        System.Net.Sockets.NetworkStream? transport = null;
        var options = new RespireConnectionOptions
        {
            Database = 1,
            TestingConnectTimeoutFactory = (token, _) =>
            {
                connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutToken = connectTimeout.Token;
                return connectTimeout;
            },
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    transport = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    connected.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (cancellation == 4) throw new OperationCanceledException(unrelated.Token);
                    if (cancellation == 5) throw new OperationCanceledException(token);
                    // Deliberately return after cancellation. The production post-connect check
                    // must observe it before handing this real TCP stream to the handshake.
                    return transport;
                }
                catch
                {
                    transport?.Dispose();
                    socket.Dispose();
                    throw;
                }
            },
        };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port, options, null);
        var pending = pooled ? pool.RentAsync(caller.Token).AsTask()
            : RespireConnection.ConnectAsync("127.0.0.1", server.Port, options, cancellationToken: caller.Token);
        Task? cleanup = null;
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var registration = connectTimeout!.Token.Register(() => cancelled.TrySetResult());
            switch (cancellation)
            {
                case 0: connectTimeout.Cancel(); break;
                case 1: caller.Cancel(); break;
                case 2: cleanup = pool.RetireAsync().AsTask(); break;
                case 3: cleanup = pool.DisposeAsync().AsTask(); break;
                case 4: unrelated.Cancel(); connectTimeout.Cancel(); break;
            }
            if (cancellation != 5) await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
            release.TrySetResult();
            if (cancellation == 0)
            {
                var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Throws<RespireTimeoutException>();
                await Assert.That(error!.CommandName).IsEqualTo("CONNECT");
                await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
                await Assert.That(error.InnerException is OperationCanceledException).IsTrue();
                await Assert.That(((OperationCanceledException)error.InnerException!).CancellationToken)
                    .IsEqualTo(timeoutToken);
            }
            else
            {
                var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Throws<OperationCanceledException>();
                if (cancellation == 1) await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
                else if (cancellation == 4) await Assert.That(error!.CancellationToken).IsEqualTo(unrelated.Token);
                else await Assert.That(error!.CancellationToken == caller.Token).IsFalse();
            }
            await Assert.That(caller.IsCancellationRequested).IsEqualTo(cancellation == 1);
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
            await Assert.That(transport!.CanRead).IsFalse();
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                var connection = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                if (!pooled) await connection.DisposeAsync();
            }
            catch (Exception) when (pending.IsCompleted) { }
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TlsTimeoutIsDistinctFromCallerCancellation(bool pooled, bool connectTimeout)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var options = new RespireConnectionOptions
        {
            UseTls = true,
            ConnectTimeout = connectTimeout ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(10),
        };
        using var caller = new CancellationTokenSource();
        await using var pool = new DedicatedConnectionPool("127.0.0.1", port, options, null);
        var pending = pooled ? pool.RentAsync(caller.Token).AsTask()
            : RespireConnection.ConnectAsync("127.0.0.1", port, options, cancellationToken: caller.Token);
        using var accepted = await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!connectTimeout) caller.Cancel();
        OperationCanceledException cancellation;
        if (connectTimeout)
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("CONNECT");
            await Assert.That(error.Timeout).IsEqualTo(options.ConnectTimeout);
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", port));
            await Assert.That(error.InnerException is OperationCanceledException).IsTrue();
            cancellation = (OperationCanceledException)error.InnerException!;
        }
        else
        {
            cancellation = (await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>())!;
        }
        await Assert.That(cancellation.CancellationToken == caller.Token).IsEqualTo(!connectTimeout);
        await Assert.That(caller.IsCancellationRequested).IsEqualTo(!connectTimeout);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FencingHandshakePreservesNormalDeadlinesOnReuse(bool fencing)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING",
        };
        server.DelayReply(0, 250);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, CommandTimeout = TimeSpan.FromMilliseconds(50) }, null);
        if (!fencing)
        {
            await Assert.That(async () => await pool.RentAsync(CancellationToken.None))
                .Throws<RespireTimeoutException>();
            return;
        }

        var control = await pool.RentAsync(CancellationToken.None, armHandshakeDeadline: false);
        pool.Return(control);
        var ordinary = await pool.RentAsync(CancellationToken.None);
        await Assert.That(ordinary).IsSameReferenceAs(control);
        await Assert.That(async () => await ordinary.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireTimeoutException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingFencingHandshakeCanBeCancelled(bool disposePool)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1 }, null);
        using var caller = new CancellationTokenSource();
        var pending = pool.RentAsync(caller.Token, armHandshakeDeadline: false).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposePool)
            await pool.DisposeAsync();
        else
            caller.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
    }
}
