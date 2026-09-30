using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedPoolRetirementTests
{
    [Test]
    public async Task RetirementClosesIdleButPreservesBorrowedReply()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var pool = CreatePool(server);
        var borrowed = await pool.RentAsync(CancellationToken.None);
        var idle = await pool.RentAsync(CancellationToken.None);
        pool.Return(idle);
        var response = borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retirement = pool.RetireAsync().AsTask();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await Assert.That(idle.IsConnected).IsFalse();
        await Assert.That(borrowed.IsConnected).IsTrue();
        await Assert.That(async () => await pool.RentAsync(CancellationToken.None)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(ReferenceEquals(pool.RetireAsync().AsTask(), retirement)).IsTrue();

        await server.SendRawAsync(FakeRespServer.PongReply);
        using var reply = await response.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(retirement.IsCompleted).IsFalse();
        pool.Return(borrowed);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(borrowed.IsConnected).IsFalse();
        await Assert.That(idle.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementCancelsPendingHandshake(bool fencing)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1 }, null);
        var acquisition = pool.RentAsync(CancellationToken.None, armHandshakeDeadline: !fencing).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pool.RetireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await acquisition).Throws<OperationCanceledException>();
        await Assert.That(async () => await pool.RentAsync(CancellationToken.None)).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task DisposalAbortsBorrowedOperationDuringRetirement()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var pool = CreatePool(server);
        var connection = await pool.RentAsync(CancellationToken.None);
        var response = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = pool.RetireAsync().AsTask();
        await Assert.That(retirement.IsCompleted).IsFalse();
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await response).Throws<RespireConnectionException>();
        pool.Return(connection); // A finally block may return a lease after client disposal.
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task DiscardCompletesRetirementAfterClosingBorrowedConnection()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server);
        var connection = await pool.RentAsync(CancellationToken.None);
        var retirement = pool.RetireAsync().AsTask();
        await pool.DiscardAsync(connection);
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.IsConnected).IsFalse();
        await pool.DiscardAsync(connection); // Idempotent after cleanup has completed.
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentReturnsAndStopLeaveNoBorrowedOrIdleConnections(bool dispose)
    {
        const int count = 12;
        await using var server = new FakeRespServer(count, FakeRespServer.PongReply);
        await using var pool = CreatePool(server);
        var connections = new RespireConnection[count];
        for (var i = 0; i < count; i++) connections[i] = await pool.RentAsync(CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returns = connections.Select(connection => Task.Run(async () =>
        {
            await start.Task;
            pool.Return(connection);
        })).ToArray();
        var stop = Task.Run(async () =>
        {
            await start.Task;
            if (dispose) await pool.DisposeAsync();
            else await pool.RetireAsync();
        });
        start.SetResult();
        await Task.WhenAll(returns.Append(stop)).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var connection in connections) await Assert.That(connection.IsConnected).IsFalse();
        await pool.RetireAsync();
        await pool.DisposeAsync();
        await Assert.That(async () => await pool.RentAsync(CancellationToken.None)).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task ActivePoolReusesReturnedConnectionBeforeRetirement()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server);
        var first = await pool.RentAsync(CancellationToken.None);
        pool.Return(first);
        var second = await pool.RentAsync(CancellationToken.None);
        await Assert.That(second).IsSameReferenceAs(first);
        using var reply = await second.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        pool.Return(second);
        await pool.RetireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(second.IsConnected).IsFalse();
    }

    [Test]
    public async Task CompletionWaitsForReceiveCleanupAfterSocketCloses()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var server = new FakeRespServer();
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                PushHandler = (in Respire.Protocol.RespValue _) =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release receive callback.");
                },
            }, null);
        var connection = await pool.RentAsync(CancellationToken.None);
        try
        {
            await server.SendRawAsync(">2\r\n+notice\r\n+value\r\n"u8.ToArray());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pool.Return(connection);
            var retirement = pool.RetireAsync().AsTask();
            var disposal = pool.DisposeAsync().AsTask();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(retirement.IsCompleted).IsFalse();
            await Assert.That(disposal.IsCompleted).IsFalse();
            release.Set();
            await Task.WhenAll(retirement, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    private static DedicatedConnectionPool CreatePool(FakeRespServer server)
        => new("127.0.0.1", server.Port, RespireConnectionOptions.Default, null);
}
