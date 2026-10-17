using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConnectionLifetimeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DefaultsDisableLifetimeAndLeavePublishedConnectionUnchanged()
    {
        var clock = new LifetimeClock();
        var options = new RespireOptions { Endpoints = [new("localhost")] }.ValidateAndSnapshot();
        await Assert.That(options.ConnectionMaxLifetime).IsNull();
        await Assert.That(options.ConnectionMaxLifetimeJitter).IsEqualTo(TimeSpan.Zero);
        await Assert.That(RespireConnectionOptions.Default.ConnectionMaxLifetime).IsNull();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var multiplexer = await CreateMultiplexer(server, clock, lifetime: null);
        var original = multiplexer.GetConnection();
        await Assert.That(original.LifetimeExpiresAt).IsNull();
        clock.Advance(TimeSpan.FromDays(2));
        await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(original);
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);
        using var response = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(response.AsString()).IsEqualTo("PONG");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    public async Task NonpositiveLifetimeIsRejected(int milliseconds)
    {
        var error = await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")], ConnectionMaxLifetime = TimeSpan.FromMilliseconds(milliseconds),
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains(nameof(RespireOptions.ConnectionMaxLifetime));
    }

    [Test]
    public async Task NegativeJitterAndJitterWithoutLifetimeAreRejected()
    {
        foreach (var options in new[]
        {
            new RespireOptions { ConnectionMaxLifetime = Lifetime, ConnectionMaxLifetimeJitter = TimeSpan.FromTicks(-1) },
            new RespireOptions { ConnectionMaxLifetimeJitter = TimeSpan.FromSeconds(1) },
        })
        {
            var error = await Assert.That(() => (options with { Endpoints = [new("localhost")] }).ValidateAndSnapshot())
                .ThrowsExactly<RespireConfigurationException>();
            await Assert.That(error!.Message).Contains(nameof(RespireOptions.ConnectionMaxLifetimeJitter));
        }
        await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")], ConnectionMaxLifetime = TimeSpan.MaxValue,
            ConnectionMaxLifetimeJitter = TimeSpan.FromTicks(1),
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task SnapshotPropagatesLifetimeToDataConnectionsOnly()
    {
        var clock = new LifetimeClock();
        var snapshot = new RespireOptions
        {
            Endpoints = [new("localhost")], ConnectionMaxLifetime = Lifetime,
            ConnectionMaxLifetimeJitter = TimeSpan.FromSeconds(3), ConnectionLifetimeTimeProvider = clock,
        }.ValidateAndSnapshot();
        var transport = snapshot.ToConnectionOptions();
        await Assert.That(transport.ConnectionMaxLifetime).IsEqualTo(Lifetime);
        await Assert.That(transport.ConnectionMaxLifetimeJitter).IsEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(transport.ConnectionLifetimeTimeProvider).IsSameReferenceAs(clock);
        await Assert.That(snapshot.ToControlConnectionOptions().ConnectionMaxLifetime).IsNull();
        await Assert.That(snapshot.ToControlConnectionOptions().ConnectionMaxLifetimeJitter).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task LifetimeStartsAfterSuccessfulHandshakeAndUsesMonotonicTime()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        server.GateCommand("SELECT", release.Task, () => entered.TrySetResult());
        var connecting = RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            ConnectionOptions(clock) with { Database = 1 });
        try
        {
            await entered.Task.WaitAsync(Limit);
            clock.Advance(TimeSpan.FromMinutes(1));
            release.TrySetResult();
            await using var connection = await connecting.WaitAsync(Limit);
            await Assert.That(connection.LifetimeExpiresAt).IsEqualTo(clock.GetTimestamp() + Lifetime.Ticks);
            clock.ShiftUtc(TimeSpan.FromDays(30));
            await Assert.That(connection.IsConnectionLifetimeExpired).IsFalse();
            clock.Advance(Lifetime - TimeSpan.FromTicks(1));
            await Assert.That(connection.IsConnectionLifetimeExpired).IsFalse();
            clock.Advance(TimeSpan.FromTicks(1));
            await Assert.That(connection.IsConnectionLifetimeExpired).IsTrue();
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task JitterStaysWithinConfiguredRangeAndSpreadsPhysicalDeadlines()
    {
        const int count = 24;
        var clock = new LifetimeClock();
        var jitter = TimeSpan.FromSeconds(5);
        await using var server = new FakeRespServer(count, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, clock, jitter: jitter);
        var connections = new List<RespireConnection>();
        var expires = new HashSet<long>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var connection = await pool.RentAsync(default);
                connections.Add(connection);
                var expiry = connection.LifetimeExpiresAt!.Value;
                await Assert.That(expiry).IsGreaterThanOrEqualTo(clock.GetTimestamp() + Lifetime.Ticks);
                await Assert.That(expiry).IsLessThanOrEqualTo(clock.GetTimestamp() + Lifetime.Ticks + jitter.Ticks);
                expires.Add(expiry);
            }
            await Assert.That(expires.Count).IsGreaterThan(1);
        }
        finally { foreach (var connection in connections) pool.Return(connection); }
    }

    [Test]
    public async Task ExpiredIdleConnectionCannotBeRentedBeforeItsTimerRuns()
    {
        var clock = new LifetimeClock();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = CreatePool(server, clock);
        var original = await pool.RentAsync(default);
        pool.Return(original);
        // A delayed timer must not permit a new borrower to receive an expired socket.
        clock.Advance(Lifetime, fireTimers: false);
        var replacement = await pool.RentAsync(default);
        try
        {
            await Assert.That(ReferenceEquals(original, replacement)).IsFalse();
            await Assert.That(original.IsConnected).IsFalse();
            using var response = await replacement.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(response.AsString()).IsEqualTo("PONG");
        }
        finally { pool.Return(replacement); }
    }

    [Test]
    public async Task IdleLifetimeSweepRunsWithoutIdleTimeoutAndOverridesMinimumSize()
    {
        var clock = new LifetimeClock();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var pool = CreatePool(server, clock,
            sizing: new() { MinimumSize = 1, MaximumIdle = 1, IdleTimeout = null });
        var original = await pool.RentAsync(default);
        pool.Return(original);
        await UntilAsync(() => clock.ActiveTimers != 0);
        clock.Advance(Lifetime);
        await UntilAsync(() => !original.IsConnected);
        await UntilAsync(() => pool.CaptureSize() == 0);
        await Assert.That(pool.CaptureSize()).IsEqualTo(0);
    }

    [Test]
    public async Task BorrowedConnectionFinishesCommandAfterExpiryAndClosesOnReturn()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        await using var pool = CreatePool(server, clock);
        var borrowed = await pool.RentAsync(default);
        var command = borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            clock.Advance(Lifetime + TimeSpan.FromHours(1));
            await Assert.That(borrowed.IsConnected).IsTrue();
            await Assert.That(command.IsCompleted).IsFalse();
            release.TrySetResult();
            using var response = await command.WaitAsync(Limit);
            await Assert.That(response.AsString()).IsEqualTo("PONG");
        }
        finally { release.TrySetResult(); pool.Return(borrowed); }
        await UntilAsync(() => !borrowed.IsConnected);
        var replacement = await pool.RentAsync(default);
        await Assert.That(ReferenceEquals(borrowed, replacement)).IsFalse();
        pool.Return(replacement);
    }

    [Test]
    public async Task ReplacementServesNewCommandsWhileExpiredSocketDrainsAcceptedCommand()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = EchoServer(2);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        await using var multiplexer = await CreateMultiplexer(server, clock);
        var original = multiplexer.GetConnection();
        var command = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
            await UntilAsync(() => !original.IsAcceptingCommands);
            await Assert.That(original.IsConnected).IsTrue();
            await Assert.That(original.IsAcceptingCommands).IsFalse();
            await Assert.That(command.IsCompleted).IsFalse();
            using var echo = await multiplexer.GetConnection().SendAsync(new Cmd1(new Verb("ECHO"), "replacement"));
            await Assert.That(echo.AsString()).IsEqualTo("replacement");
            await Assert.That(server.ReceivedConnectionIds.Last()).IsEqualTo(1);
            release.TrySetResult();
            using var response = await command.WaitAsync(Limit);
            await Assert.That(response.AsString()).IsEqualTo("PONG");
            await UntilAsync(() => !original.IsConnected);
            await Assert.That(server.ReceivedCommands.Count(text => text == "PING")).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task DisposalAbortsAndAwaitsExpiredSocketStillDraining()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = EchoServer(2);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        await using var multiplexer = await CreateMultiplexer(server, clock);
        var original = multiplexer.GetConnection();
        var command = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
            var replacement = multiplexer.GetConnection();
            await multiplexer.DisposeAsync().AsTask().WaitAsync(Limit);
            await Assert.That(original.IsConnected).IsFalse();
            await Assert.That(replacement.IsConnected).IsFalse();
            await Assert.That(async () => { using var response = await command.WaitAsync(Limit); })
                .Throws<RespireConnectionException>();
            await Assert.That(clock.ActiveTimers).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RetirementBoundsLifetimeDrainOnlyAfterOwnerStops(bool commandTimeout, bool replyBeforeDeadline)
    {
        var clock = new LifetimeClock();
        var maintenanceClock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        var timeout = TimeSpan.FromMinutes(commandTimeout ? 1 : 2);
        await using var server = EchoServer(2);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with
            {
                CommandTimeout = commandTimeout ? timeout : null,
                RetirementDrainFallbackTimeout = TimeSpan.FromMinutes(2),
                MaintenanceDrainTimeProvider = maintenanceClock,
            });
        var original = multiplexer.GetConnection();
        var command = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
            await UntilAsync(() => !original.IsAcceptingCommands);
            // Normal lifetime recycling must not create a maintenance drain deadline.
            maintenanceClock.Advance(timeout + TimeSpan.FromHours(1));
            await Assert.That(maintenanceClock.ActiveTimers).IsEqualTo(0);
            await Assert.That(original.IsConnected).IsTrue();
            await Assert.That(command.IsCompleted).IsFalse();

            var retirement = multiplexer.RetireAsync();
            await UntilAsync(() => maintenanceClock.ActiveTimers != 0);
            maintenanceClock.Advance(timeout - TimeSpan.FromTicks(1));
            await Assert.That(retirement.IsCompleted).IsFalse();
            await Assert.That(original.IsConnected).IsTrue();
            if (replyBeforeDeadline)
            {
                release.TrySetResult();
                using var response = await command.WaitAsync(Limit);
                await Assert.That(response.AsString()).IsEqualTo("PONG");
            }
            else maintenanceClock.Advance(TimeSpan.FromTicks(1));
            await retirement.WaitAsync(Limit);
            await Assert.That(original.IsConnected).IsFalse();
            await Assert.That(original.DrainedSuccessfully).IsEqualTo(replyBeforeDeadline);
            if (!replyBeforeDeadline)
                await Assert.That(async () => { using var response = await command.WaitAsync(Limit); })
                    .Throws<RespireConnectionException>();
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplyContinuationCanStopOwnerWhileReplacementPublishes(bool retire)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var clock = new LifetimeClock();
            var entered = NewSignal();
            var release = NewSignal();
            var completed = NewSignal();
            var generation = new GatedReplacementGeneration();
            await using var server = EchoServer(2);
            server.GateCommand("PING", release.Task, () => entered.TrySetResult());
            await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
                options: ConnectionOptions(clock) with { Generation = generation });
            var original = multiplexer.GetConnection();
            var awaiter = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
            awaiter.UnsafeOnCompleted(() =>
            {
                try
                {
                    using var response = awaiter.GetResult();
                    // Let publication contend with the synchronous shutdown admission boundary.
                    generation.Release.TrySetResult();
                    var stopping = retire ? multiplexer.RetireAsync() : multiplexer.DisposeAsync().AsTask();
                    stopping.WaitAsync(Limit).GetAwaiter().GetResult();
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); }
            });
            try
            {
                await entered.Task.WaitAsync(Limit);
                await UntilAsync(() => clock.ActiveTimers != 0);
                clock.Advance(Lifetime);
                await generation.Entered.Task.WaitAsync(Limit);
                release.TrySetResult();
                await completed.Task.WaitAsync(Limit + TimeSpan.FromSeconds(1));
                await Assert.That(original.IsConnected).IsFalse();
                await Assert.That(clock.ActiveTimers).IsEqualTo(0);
            }
            finally { release.TrySetResult(); generation.Release.TrySetResult(); }
        }
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OldReplyContinuationCanSynchronouslyStopOwner(bool retire)
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        var completed = NewSignal();
        await using var server = EchoServer(2);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            ConnectionMaxLifetime = Lifetime, ConnectionLifetimeTimeProvider = clock,
        });
        var multiplexer = client.Core.Multiplexer;
        var original = multiplexer.GetConnection();
        var awaiter = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var response = awaiter.GetResult();
                var stopping = retire ? multiplexer.RetireAsync() : client.DisposeAsync().AsTask();
                stopping.WaitAsync(Limit).GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
            await UntilAsync(() => !original.IsAcceptingCommands);
            release.TrySetResult();
            await completed.Task.WaitAsync(Limit + TimeSpan.FromSeconds(1));
            await Assert.That(original.IsConnected).IsFalse();
            await Assert.That(clock.ActiveTimers).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task ClosedReplacementBeforePublicationKeepsHealthyOriginalAndRetries()
    {
        var clock = new LifetimeClock();
        var generation = new CloseReplacementGeneration();
        await using var server = EchoServer(3);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { Generation = generation });
        var original = multiplexer.GetConnection();
        await UntilAsync(() => clock.ActiveTimers != 0);
        clock.Advance(Lifetime);
        await generation.Rejected.Task.WaitAsync(Limit);
        await UntilAsync(() => clock.ActiveTimers != 0);
        await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(original);
        using var response = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(response.AsString()).IsEqualTo("PONG");
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
        await UntilAsync(() => !original.IsConnected);
        await Assert.That(generation.Attempts).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedReplacementFailuresBackOffWithJitterAndResetAfterPublication(bool closeReplacement)
    {
        var clock = new LifetimeClock();
        var generation = new RepeatedFailureGeneration(closeReplacement);
        await using var server = EchoServer(10);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { Generation = generation });
        var original = multiplexer.GetConnection();
        await UntilAsync(() => clock.ActiveTimers != 0);
        clock.Advance(Lifetime);
        int[] caps = [1, 2, 4, 8, 16, 30, 30];
        for (var index = 0; index < caps.Length; index++)
        {
            var attempts = index + 2;
            await UntilAsync(() => Volatile.Read(ref generation.Attempts) == attempts && clock.ActiveTimers != 0);
            var delay = clock.NextTimerDelay;
            await Assert.That(delay).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(caps[index] / 2.0));
            await Assert.That(delay).IsLessThanOrEqualTo(TimeSpan.FromSeconds(caps[index]));
            await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(original);
            clock.Advance(delay - TimeSpan.FromTicks(1));
            using var response = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(response.AsString()).IsEqualTo("PONG");
            await Assert.That(Volatile.Read(ref generation.Attempts)).IsEqualTo(attempts);
            clock.Advance(TimeSpan.FromTicks(1));
        }
        await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
        await UntilAsync(() => clock.ActiveTimers != 0);
        var replacement = multiplexer.GetConnection();
        clock.Advance(Lifetime);
        await UntilAsync(() => Volatile.Read(ref generation.Attempts) == 10 && clock.ActiveTimers != 0);
        await Assert.That(clock.NextTimerDelay).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(clock.NextTimerDelay).IsLessThanOrEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(replacement);
        await multiplexer.DisposeAsync();
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);
    }

    [Test]
    public async Task FailedReplacementHandshakeKeepsHealthyOriginalAndRetries()
    {
        var clock = new LifetimeClock();
        var rejected = NewSignal();
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            ReplyOverride = (connectionId, command) =>
            {
                if (command == "SELECT 1" && connectionId == 1)
                {
                    rejected.TrySetResult();
                    return "-ERR replacement rejected\r\n"u8.ToArray();
                }
                return command == "PING" ? FakeRespServer.PongReply : null;
            },
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { Database = 1 });
        var original = multiplexer.GetConnection();
        await UntilAsync(() => clock.ActiveTimers != 0);
        clock.Advance(Lifetime);
        await rejected.Task.WaitAsync(Limit);
        await UntilAsync(() => clock.ActiveTimers != 0);
        await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(original);
        await Assert.That(original.IsAcceptingCommands).IsTrue();
        using var response = await original.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(response.AsString()).IsEqualTo("PONG");
        clock.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
        await UntilAsync(() => !original.IsConnected);
        await Assert.That(server.ReceivedCommands.Count(text => text == "SELECT 1")).IsEqualTo(3);
    }

    [Test]
    public async Task DisposalCancelsGatedReplacementHandshakeWithoutPublishingIt()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { Database = 1 });
        var original = multiplexer.GetConnection();
        server.GateCommand("SELECT", release.Task, () => entered.TrySetResult());
        try
        {
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await entered.Task.WaitAsync(Limit);
            await Assert.That(multiplexer.GetConnection()).IsSameReferenceAs(original);
            await multiplexer.DisposeAsync().AsTask().WaitAsync(Limit);
            await Assert.That(original.IsConnected).IsFalse();
            await Assert.That(clock.ActiveTimers).IsEqualTo(0);
            await Assert.That(() => multiplexer.GetConnection()).Throws<ObjectDisposedException>();
            await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecycleMetricLogAndCloseReasonAreRecordedOnceOutsideOwnershipLocks(bool dedicated)
    {
        using var configuration = new MetricConfigurationScope();
        var clock = new LifetimeClock();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var measurements = new ConcurrentQueue<(string Name, long Value, string? Reason)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, capture) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name is
                    "redis.client.connection.recycle" or "redis.client.connection.closed")
                    capture.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? poolName = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "db.client.connection.pool.name") poolName = tag.Value as string;
                if (tag.Key == "redis.client.connection.close.reason") reason = tag.Value as string;
            }
            if (poolName == $"127.0.0.1:{server.Port}/0/{(dedicated ? "dedicated" : "shared")}" || poolName == "overflow/shared")
                measurements.Enqueue((instrument.Name, value, reason));
        });
        listener.Start();
        var logged = NewSignal();
        var logger = new RecycleLogger();
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port, ConnectionOptions(clock), logger);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { ConnectionMaxLifetime = dedicated ? null : Lifetime }, logger: logger);
        logger.OnRecycle = () =>
        {
            try
            {
                // A separate thread must be able to take the ownership gates while the log
                // callback remains active. Disposal also joins the lifetime worker's cleanup.
                Task.Run(async () =>
                {
                    if (dedicated) await pool.DisposeAsync();
                    else await multiplexer.DisposeAsync();
                }).WaitAsync(Limit).GetAwaiter().GetResult();
                logged.TrySetResult();
            }
            catch (Exception error) { logged.TrySetException(error); }
        };
        RespireConnection original;
        if (dedicated)
        {
            original = await pool.RentAsync(default);
            pool.Return(original);
        }
        else original = multiplexer.GetConnection();
        await UntilAsync(() => clock.ActiveTimers != 0);
        clock.Advance(Lifetime);
        await logged.Task.WaitAsync(Limit);
        await UntilAsync(() => !original.IsConnected && ConnectionTelemetry.PendingMeasurements == 0);
        await Assert.That(logger.Count).IsEqualTo(1);
        await Assert.That(measurements.Where(item => item.Name == "redis.client.connection.recycle").Sum(item => item.Value)).IsEqualTo(1L);
        await Assert.That(measurements.Where(item => item.Name == "redis.client.connection.closed" && item.Reason == "max_lifetime")
            .Sum(item => item.Value)).IsEqualTo(1L);
    }

    [Test]
    public async Task RepeatedShortLifetimeRotationPreservesEveryConcurrentEchoReply()
    {
        const int rounds = 8;
        const int parallelCommands = 16;
        var clock = new LifetimeClock();
        var shortLifetime = TimeSpan.FromMilliseconds(5);
        await using var server = EchoServer(rounds + 1);
        await using var multiplexer = await CreateMultiplexer(server, clock, shortLifetime);
        for (var round = 0; round < rounds; round++)
        {
            await UntilAsync(() => clock.ActiveTimers != 0);
            var original = multiplexer.GetConnection();
            var start = NewSignal();
            var commands = Enumerable.Range(0, parallelCommands).Select(async index =>
            {
                await start.Task;
                var expected = $"round-{round}-command-{index}";
                using var response = await multiplexer.GetConnection().SendAsync(new Cmd1(new Verb("ECHO"), expected));
                await Assert.That(response.AsString()).IsEqualTo(expected);
            }).ToArray();
            start.TrySetResult();
            clock.Advance(shortLifetime);
            await Task.WhenAll(commands).WaitAsync(Limit);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
        }
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(rounds * parallelCommands);
        await Assert.That(server.ReceivedCommands.Distinct().Count()).IsEqualTo(rounds * parallelCommands);
    }

    [Test]
    public async Task ClosedSocketReleasesLifetimeTimerBeforeItsDeadline()
    {
        var clock = new LifetimeClock();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var multiplexer = await CreateMultiplexer(server, clock);
        var original = multiplexer.GetConnection();
        await UntilAsync(() => clock.ActiveTimers != 0);
        await original.DisposeAsync();
        await UntilAsync(() => clock.ActiveTimers == 0);
        await Assert.That(clock.ActiveTimers).IsEqualTo(0);
    }

    [Test]
    public async Task RecyclingFlushesTrackedCacheAndRejectsLateOldSocketFill()
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (connectionId, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith("GET ", StringComparison.Ordinal) => connectionId == 0
                    ? "$3\r\nold\r\n"u8.ToArray() : "$3\r\nnew\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp3,
            ClientSideCache = new(), ConnectionMaxLifetime = Lifetime, ConnectionLifetimeTimeProvider = clock,
        });
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var original = client.Core.Multiplexer.GetConnection();
        server.GateCommand("GET pending", release.Task, () => entered.TrySetResult());
        var pending = client.GetStringAsync("pending").AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(client.Core.Multiplexer.GetConnection(), original));
            await UntilAsync(() => !original.IsAcceptingCommands);
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
            release.TrySetResult();
            await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("old");
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
            await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("new");
            await Assert.That(await client.GetStringAsync("pending")).IsEqualTo("new");
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LifetimeDisposalFailureStillRetiresCurrentConnections(bool currentDisposalFails)
    {
        var clock = new LifetimeClock();
        var entered = NewSignal();
        var release = NewSignal();
        var oldFailure = new IOException("Old lifetime socket disposal failed.");
        var currentFailure = new IOException("Current socket disposal failed.");
        var created = 0;
        await using var server = EchoServer(2);
        server.GateCommand("PING", release.Task, () => entered.TrySetResult());
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with
            {
                TestingStreamFactory = async (host, port, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(host, port, token);
                        var failure = Interlocked.Increment(ref created) == 1 ? oldFailure
                            : currentDisposalFails ? currentFailure : null;
                        return new LifetimeDisposalFailureStream(socket, failure);
                    }
                    catch { socket.Dispose(); throw; }
                },
            });
        var original = multiplexer.GetConnection();
        var command = original.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await UntilAsync(() => clock.ActiveTimers != 0);
            clock.Advance(Lifetime);
            await UntilAsync(() => !ReferenceEquals(multiplexer.GetConnection(), original));
            await UntilAsync(() => !original.IsAcceptingCommands);
            var current = multiplexer.GetConnection();
            var retirement = multiplexer.RetireAsync();
            release.TrySetResult();
            using var response = await command.WaitAsync(Limit);
            var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).Throws<Exception>();
            await Assert.That(original.IsConnected).IsFalse();
            await Assert.That(current.IsConnected).IsFalse();
            await Assert.That(current.IsAcceptingCommands).IsFalse();
            if (currentDisposalFails)
            {
                await Assert.That(error).IsTypeOf<AggregateException>();
                var failures = ((AggregateException)error!).Flatten().InnerExceptions;
                await Assert.That(failures.Count).IsEqualTo(2);
                await Assert.That(failures.Contains(oldFailure)).IsTrue();
                await Assert.That(failures.Contains(currentFailure)).IsTrue();
            }
            else await Assert.That(error).IsSameReferenceAs(oldFailure);
        }
        finally
        {
            release.TrySetResult();
            try { await multiplexer.DisposeAsync().AsTask().WaitAsync(Limit); }
            catch (IOException error) when (ReferenceEquals(error, currentFailure)) { }
        }
    }

    private sealed class LifetimeDisposalFailureStream(Socket socket, Exception? failure)
        : NetworkStream(socket, ownsSocket: true)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && failure is not null) throw failure;
        }
    }

    private static RespireConnectionOptions ConnectionOptions(LifetimeClock clock, TimeSpan? lifetime = null)
        => new()
        {
            Protocol = RespireProtocol.Resp2, ConnectionMaxLifetime = lifetime ?? Lifetime,
            ConnectionLifetimeTimeProvider = clock,
        };

    private static Task<RespireConnectionMultiplexer> CreateMultiplexer(FakeRespServer server, LifetimeClock clock)
        => CreateMultiplexer(server, clock, Lifetime);

    private static Task<RespireConnectionMultiplexer> CreateMultiplexer(FakeRespServer server, LifetimeClock clock, TimeSpan? lifetime)
        => RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            options: ConnectionOptions(clock) with { ConnectionMaxLifetime = lifetime });

    private static DedicatedConnectionPool CreatePool(FakeRespServer server, LifetimeClock clock,
        TimeSpan jitter = default, RespireDedicatedPoolOptions? sizing = null)
        => new("127.0.0.1", server.Port, ConnectionOptions(clock) with
        {
            ConnectionMaxLifetimeJitter = jitter, DedicatedPool = sizing ?? new(),
        }, null);

    private static FakeRespServer EchoServer(int maxConnections)
        => new(maxConnections, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("ECHO ", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(command[5..])}\r\n{command[5..]}\r\n") : null,
        };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class GatedReplacementGeneration : IConnectionGeneration
    {
        internal readonly TaskCompletionSource Entered = NewSignal();
        internal readonly TaskCompletionSource Release = NewSignal();
        private int _attempts;
        public bool IsRetired => false;
        public async ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) != 2) return;
            Entered.TrySetResult();
            await Release.Task;
        }
        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response) { }
        public void ConnectionClosed(RespireConnection connection, bool unexpected) { }
    }

    private sealed class RepeatedFailureGeneration(bool closeReplacement) : IConnectionGeneration
    {
        internal int Attempts;
        public bool IsRetired => false;
        public async ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref Attempts);
            if (attempt is 1 or 9) return;
            if (!closeReplacement) throw new InvalidOperationException("Replacement setup rejected.");
            await connection.DisposeAsync();
        }
        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response) { }
        public void ConnectionClosed(RespireConnection connection, bool unexpected) { }
    }

    private sealed class CloseReplacementGeneration : IConnectionGeneration
    {
        internal readonly TaskCompletionSource Rejected = NewSignal();
        internal int Attempts;
        public bool IsRetired => false;
        public async ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Attempts) != 2) return;
            // Complete setup with a socket that closed before publication, without throwing.
            await connection.DisposeAsync();
            Rejected.TrySetResult();
        }
        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response) { }
        public void ConnectionClosed(RespireConnection connection, bool unexpected) { }
    }

    private sealed class RecycleLogger : ILogger
    {
        internal Action? OnRecycle;
        internal int Count;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name != "ConnectionRecycled") return;
            Interlocked.Increment(ref Count);
            OnRecycle?.Invoke();
        }
    }

    private sealed class LifetimeClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks = TimeSpan.FromDays(1).Ticks;
        private TimeSpan _utcShift;
        internal int ActiveTimers { get { lock (_gate) return _timers.Count; } }
        internal TimeSpan NextTimerDelay
        {
            get { lock (_gate) return TimeSpan.FromTicks(_timers.Min(timer => timer.Due) - _ticks); }
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return DateTimeOffset.UnixEpoch.AddTicks(_ticks) + _utcShift; }
        internal void ShiftUtc(TimeSpan elapsed) { lock (_gate) _utcShift += elapsed; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                if (!fireTimers) return;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due)
                {
                    if (timer.Period > TimeSpan.Zero) timer.Due = _ticks + timer.Period.Ticks;
                    else _timers.Remove(timer);
                }
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(LifetimeClock clock, TimerCallback callback, object? state) : ITimer
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
