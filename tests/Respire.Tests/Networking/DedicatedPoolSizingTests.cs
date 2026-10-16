using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedPoolSizingTests
{
    [Test]
    public async Task MaximumSizeIncludesConnectingReservations()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        server.GateCommand("SELECT", release.Task, () => entered.TrySetResult());
        await using var pool = CreatePool(server, new() { MaximumSize = 1 }, database: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = pool.RentAsync(deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var second = pool.RentAsync(deadline.Token).AsTask();
        try
        {
            await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(1);
            await Assert.That(second.IsCompleted).IsFalse();
            release.TrySetResult();
            var connection = await first.WaitAsync(deadline.Token);
            await Assert.That(second.IsCompleted).IsFalse();
            pool.Return(connection);
            var reused = await second.WaitAsync(deadline.Token);
            await Assert.That(reused).IsSameReferenceAs(connection);
            await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("SELECT", StringComparison.Ordinal))).IsEqualTo(1);
            pool.Return(reused);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task CanceledWaiterDoesNotConsumeReturnedCapacity()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { MaximumSize = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = await pool.RentAsync(deadline.Token);
        using var cancellation = new CancellationTokenSource();
        var canceled = pool.RentAsync(cancellation.Token).AsTask();
        var next = pool.RentAsync(deadline.Token).AsTask();
        await Assert.That(canceled.IsCompleted).IsFalse();
        cancellation.Cancel();
        var error = await Assert.That(async () => await canceled.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(next.IsCompleted).IsFalse();
        pool.Return(connection);
        var rented = await next.WaitAsync(deadline.Token);
        await Assert.That(rented).IsSameReferenceAs(connection);
        pool.Return(rented);
    }

    [Test]
    public async Task DiscardReleasesCapacityForReplacement()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { MaximumSize = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = await pool.RentAsync(deadline.Token);
        var pending = pool.RentAsync(deadline.Token).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        await pool.DiscardAsync(connection);
        var replacement = await pending.WaitAsync(deadline.Token);
        await Assert.That(ReferenceEquals(connection, replacement)).IsFalse();
        await Assert.That(connection.IsConnected).IsFalse();
        pool.Return(replacement);
    }

    [Test]
    public async Task ClosingConnectionKeepsCapacityUntilReceiveCleanupCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port, new RespireConnectionOptions
        {
            Protocol = RespireProtocol.Resp2,
            DedicatedPool = new() { MaximumSize = 1 },
            PushHandler = (in Respire.Protocol.RespValue _) =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Receive callback was not released.");
            },
        }, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = await pool.RentAsync(deadline.Token);
        try
        {
            await server.SendRawAsync(">2\r\n+notice\r\n+value\r\n"u8.ToArray());
            await entered.Task.WaitAsync(deadline.Token);
            var discard = pool.DiscardAsync(connection).AsTask();
            await Assert.That(connection.IsConnected).IsFalse();
            await Assert.That(discard.IsCompleted).IsFalse();
            var pending = pool.RentAsync(deadline.Token).AsTask();
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(0);
            release.Set();
            await discard.WaitAsync(deadline.Token);
            var replacement = await pending.WaitAsync(deadline.Token);
            pool.Return(replacement);
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task FailedHandshakeReleasesCapacityForWaitingRenter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (connectionId, _) => connectionId == 0 ? "-ERR rejected database\r\n"u8.ToArray() : null,
        };
        server.GateCommand("SELECT", release.Task, () => entered.TrySetResult());
        await using var pool = CreatePool(server, new() { MaximumSize = 1 }, database: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = pool.RentAsync(deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        var next = pool.RentAsync(deadline.Token).AsTask();
        try
        {
            await Assert.That(next.IsCompleted).IsFalse();
            release.TrySetResult();
            var failure = await Assert.That(async () => await first.WaitAsync(deadline.Token)).Throws<RespireConnectionException>();
            await Assert.That(failure!.InnerException).IsTypeOf<RespireServerException>();
            var replacement = await next.WaitAsync(deadline.Token);
            await Assert.That(replacement.IsConnected).IsTrue();
            pool.Return(replacement);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StoppingPoolRejectsCapacityWaiters(bool dispose)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { MaximumSize = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = await pool.RentAsync(deadline.Token);
        var pending = pool.RentAsync(deadline.Token).AsTask();
        var stop = dispose ? pool.DisposeAsync().AsTask() : pool.RetireAsync().AsTask();
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).ThrowsExactly<ObjectDisposedException>();
        if (!dispose)
        {
            await Assert.That(connection.IsConnected).IsTrue();
            await Assert.That(stop.IsCompleted).IsFalse();
        }
        pool.Return(connection);
        await stop.WaitAsync(deadline.Token);
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FullPoolReplacesIdleConnectionForIncompatibleAcquisition(bool streaming)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var pool = CreatePool(server, new() { MaximumSize = 1 },
            maintenance: RespireMaintenanceNotificationMode.Auto);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ordinary = await pool.RentAsync(deadline.Token);
        pool.Return(ordinary);
        var replacement = await pool.RentAsync(deadline.Token, reuseIdle: streaming,
            kind: streaming ? DedicatedLeaseKind.Streaming : DedicatedLeaseKind.Ordinary);
        await Assert.That(ReferenceEquals(ordinary, replacement)).IsFalse();
        await Assert.That(ordinary.IsConnected).IsFalse();
        await Assert.That(replacement.IsConnected).IsTrue();
        pool.Return(replacement);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(6)]
    public async Task MaximumIdleBoundsRetainedConnections(int maximumIdle)
    {
        await using var server = new FakeRespServer(7, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { MaximumIdle = maximumIdle });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connections = new List<RespireConnection>();
        for (var index = 0; index < 7; index++) connections.Add(await pool.RentAsync(deadline.Token));
        foreach (var connection in connections) pool.Return(connection);
        await Assert.That(connections.Count(connection => connection.IsConnected)).IsEqualTo(maximumIdle);
    }

    [Test]
    public async Task IdleTimeoutPreservesMinimumAndNeverClosesBorrowedConnection()
    {
        var clock = new PoolClock();
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new()
        {
            MinimumSize = 2, MaximumIdle = 3, IdleTimeout = TimeSpan.FromSeconds(10), TimeProvider = clock,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(0);
        await Assert.That(server.ConnectionAccepted.IsCompleted).IsFalse();
        var borrowed = await pool.RentAsync(deadline.Token);
        var first = await pool.RentAsync(deadline.Token);
        var second = await pool.RentAsync(deadline.Token);
        pool.Return(first);
        pool.Return(second);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(first.IsConnected && second.IsConnected).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(new[] { first, second }.Count(connection => connection.IsConnected)).IsEqualTo(1);
        await Assert.That(borrowed.IsConnected).IsTrue();
        using var response = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(response.AsString()).IsEqualTo("PONG");
        pool.Return(borrowed);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(new[] { borrowed, first, second }.Count(connection => connection.IsConnected)).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectingReservationDoesNotReplaceMinimumRetainedConnection()
    {
        var clock = new PoolClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
        await using var pool = CreatePool(server, new()
        {
            MinimumSize = 2, MaximumIdle = 2, IdleTimeout = TimeSpan.FromSeconds(1), TimeProvider = clock,
        }, database: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await pool.RentAsync(deadline.Token);
        var second = await pool.RentAsync(deadline.Token);
        pool.Return(first);
        pool.Return(second);
        server.GateCommand("SELECT", release.Task, () => entered.TrySetResult());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pending = pool.RentAsync(cancellation.Token, reuseIdle: false).AsTask();
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.That(first.IsConnected).IsTrue();
            await Assert.That(second.IsConnected).IsTrue();
            cancellation.Cancel();
            await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.That(first.IsConnected).IsTrue();
            await Assert.That(second.IsConnected).IsTrue();
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task IdleTimerPausesWhileBorrowedAndAfterExpiryAndRestartsOnReturn()
    {
        var clock = new PoolClock();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { IdleTimeout = TimeSpan.FromSeconds(1), TimeProvider = clock });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connection = await pool.RentAsync(deadline.Token);
        pool.Return(connection);
        await Assert.That(clock.ActiveTimers).IsEqualTo(1);
        var reused = await pool.RentAsync(deadline.Token);
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(reused.IsConnected).IsTrue();
        pool.Return(reused);
        await Assert.That(clock.ActiveTimers).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(reused.IsConnected).IsFalse();
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);
    }

    [Test]
    public async Task ReturningReusedConnectionRestartsItsIdleAge()
    {
        var clock = new PoolClock();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { IdleTimeout = TimeSpan.FromSeconds(10), TimeProvider = clock });
        var connection = await pool.RentAsync(CancellationToken.None);
        pool.Return(connection);
        clock.Advance(TimeSpan.FromSeconds(6));
        var reused = await pool.RentAsync(CancellationToken.None);
        await Assert.That(reused).IsSameReferenceAs(connection);
        pool.Return(reused);
        clock.Advance(TimeSpan.FromSeconds(4));
        await Assert.That(connection.IsConnected).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdleExpiryRacingReturnAndStopDrainsEveryConnection(bool dispose)
    {
        const int rounds = 8;
        await using var server = new FakeRespServer(rounds * 2, FakeRespServer.PongReply);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var round = 0; round < rounds; round++)
        {
            var clock = new PoolClock();
            await using var pool = CreatePool(server, new()
            {
                MaximumSize = 2, IdleTimeout = TimeSpan.FromSeconds(1), TimeProvider = clock,
            });
            var idle = await pool.RentAsync(deadline.Token);
            var borrowed = await pool.RentAsync(deadline.Token);
            pool.Return(idle);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expiry = Task.Run(async () =>
            {
                await start.Task;
                clock.Advance(TimeSpan.FromSeconds(1));
            });
            var returning = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    // Retirement and expiry must preserve the renter's ownership until return.
                    // Explicit disposal is allowed to abort a borrowed connection.
                    if (!dispose) await Assert.That(borrowed.IsConnected).IsTrue();
                }
                finally { pool.Return(borrowed); }
            });
            var stopping = Task.Run(async () =>
            {
                await start.Task;
                if (dispose) await pool.DisposeAsync();
                else await pool.RetireAsync();
            });
            start.TrySetResult();
            await Task.WhenAll(expiry, returning, stopping).WaitAsync(deadline.Token);
            // Further timer activity must not revive ownership after the pool has drained.
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.That(idle.IsConnected).IsFalse();
            await Assert.That(borrowed.IsConnected).IsFalse();
            await Assert.That(pool.CaptureSize()).IsEqualTo(0);
            await Assert.That(pool.CaptureRetirementState().Borrowed).IsEqualTo(0);
            await Assert.That(pool.CaptureRetirementState().Connecting).IsEqualTo(0);
            await Assert.That(async () => await pool.RentAsync(deadline.Token)).ThrowsExactly<ObjectDisposedException>();
        }
    }

    [Test]
    public async Task ConcurrentBorrowersNeverExceedMaximumSize()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, new() { MaximumSize = 2 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var arrivals = 0;
        var gate = new object();
        var borrowers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var connection = await pool.RentAsync(deadline.Token);
            try
            {
                lock (gate)
                {
                    active++;
                    peak = Math.Max(peak, active);
                    if (++arrivals == 2) pairEntered.TrySetResult();
                }
                await releaseFirstPair.Task.WaitAsync(deadline.Token);
                using var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
                await Assert.That(response.AsString()).IsEqualTo("PONG");
            }
            finally
            {
                lock (gate) active--;
                pool.Return(connection);
            }
        })).ToArray();
        start.TrySetResult();
        try
        {
            await pairEntered.Task.WaitAsync(deadline.Token);
            await Assert.That(pool.CaptureRetirementState().Borrowed).IsEqualTo(2);
        }
        finally { releaseFirstPair.TrySetResult(); }
        await Task.WhenAll(borrowers).WaitAsync(deadline.Token);
        await Assert.That(peak).IsEqualTo(2);
    }

    private static DedicatedConnectionPool CreatePool(FakeRespServer server, RespireDedicatedPoolOptions sizing,
        int database = 0, RespireMaintenanceNotificationMode maintenance = RespireMaintenanceNotificationMode.Disabled)
        => new("127.0.0.1", server.Port, new RespireConnectionOptions
        {
            Protocol = RespireProtocol.Resp2, DedicatedPool = sizing, Database = database,
            MaintenanceNotifications = maintenance,
        }, null);

    private sealed class PoolClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        internal int ActiveTimers { get { lock (_gate) return _timers.Count; } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due)
                {
                    if (timer.Period > TimeSpan.Zero) timer.Due = _ticks + timer.Period.Ticks;
                    else _timers.Remove(timer);
                }
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(PoolClock clock, TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            internal long Due;
            internal TimeSpan Period;
            internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (Volatile.Read(ref _disposed) != 0) return false;
                    clock._timers.Remove(this);
                    Period = period;
                    if (dueTime == Timeout.InfiniteTimeSpan) return true;
                    Due = clock._ticks + dueTime.Ticks;
                    clock._timers.Add(this);
                    return true;
                }
            }
            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                lock (clock._gate) clock._timers.Remove(this);
            }
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
