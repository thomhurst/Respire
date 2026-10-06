using System.Diagnostics.Metrics;
using System.Text;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class ConnectionMetricCleanupTests
{
    private const string QueuedSelectionProbe = "RESPIRE_TEST_METRIC_QUEUED_SELECTION";

    [Test]
    public async Task QueuedLifecycleDeliveryRetainsEventTimeGroupSelection()
    {
        // Constrain only a fresh child process, never the parallel test runner.
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(ConnectionMetricCleanupTests).Assembly.Location);
        start.Environment[QueuedSelectionProbe] = Environment.Version.ToString();
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedQueuedSelectionProbe()
    {
        var expectedRuntime = Environment.GetEnvironmentVariable(QueuedSelectionProbe);
        if (expectedRuntime is null) return null;
        try
        {
            Require(expectedRuntime == Environment.Version.ToString(), "Probe runtime differs from the parent.");
            RunQueuedSelectionProbe();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void RunQueuedSelectionProbe()
    {
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.All });
        var server = new FakeRespServer(1, FakeRespServer.PongReply);
        RespireConnection? connection = null;
        try
        {
            connection = RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new() { Protocol = RespProtocol.Resp2 }).GetAwaiter().GetResult();
            using var listener = new MeterListener();
            var deliveries = 0;
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.connection.wait_time")
                    current.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref deliveries));
            listener.Start();
            var droppedBefore = ConnectionTelemetry.DroppedMeasurements;
            ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
            ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var gateQueued = false;
            try
            {
                Require(ThreadPool.SetMinThreads(1, minIo) && ThreadPool.SetMaxThreads(1, maxIo),
                    "Could not configure the isolated one-worker pool.");
                gateQueued = ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    entered.Set();
                    // Only the test releases this gate; the parent bounds the child lifetime.
                    release.Wait();
                    finished.Set();
                }, 0, preferLocal: false);
                Require(gateQueued, "Could not queue the worker gate.");
                Require(entered.Wait(TimeSpan.FromSeconds(5)), "Worker gate did not start.");
                connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
                Require(ConnectionTelemetry.PendingMeasurements == 1, "Expected one accepted measurement.");
                Require(Volatile.Read(ref deliveries) == 0 && !finished.IsSet,
                    "Measurement callback must remain queued before groups change.");
                RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
                release.Set();
                Require(SpinWait.SpinUntil(() => ConnectionTelemetry.PendingMeasurements == 0,
                    TimeSpan.FromSeconds(5)), "Queued measurement did not drain.");
                Require(Volatile.Read(ref deliveries) == 1, "Group change discarded the queued measurement.");
                connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
                Require(ConnectionTelemetry.PendingMeasurements == 0 && Volatile.Read(ref deliveries) == 1,
                    "Disabled groups selected a new measurement.");
                Require(ConnectionTelemetry.DroppedMeasurements == droppedBefore, "Probe dropped a measurement.");
            }
            finally
            {
                release.Set();
                ThreadPool.SetMaxThreads(maxWorkers, maxIo);
                ThreadPool.SetMinThreads(minWorkers, minIo);
                if (gateQueued)
                    Require(finished.Wait(TimeSpan.FromSeconds(5)), "Worker gate did not finish.");
            }
        }
        finally
        {
            try
            {
                if (connection is not null)
                    connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            finally { server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task BlockedLifecycleDeliveriesAreBoundedAndResumeAfterDrain()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        using var listener = new BlockingListener("db.client.connection.wait_time");
        var pendingWhileBlocked = 0;
        var droppedBefore = ConnectionTelemetry.DroppedMeasurements;
        var exportedWhileBlocked = -1L;
        using var drops = new MeterListener();
        drops.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.connection.measurements.dropped")
                current.EnableMeasurementEvents(instrument);
        };
        drops.SetMeasurementEventCallback<long>((_, value, _, _) => exportedWhileBlocked = value);
        drops.Start();
        try
        {
            connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            // Keep one callback blocked while a burst exceeds the process-wide capacity.
            // Read before releasing so queued and running deliveries are both counted.
            for (var i = 0; i < 256; i++)
                connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
            pendingWhileBlocked = ConnectionTelemetry.PendingMeasurements;
            drops.RecordObservableInstruments();
            // Accepted measurements retain selection; the isolated probe separately
            // guarantees that a callback has not started before the group change.
            RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        }
        finally { listener.Release(); }

        await WaitForDeliveries();
        await Assert.That(pendingWhileBlocked).IsEqualTo(64);
        await Assert.That(listener.Deliveries).IsEqualTo(64);
        await Assert.That(ConnectionTelemetry.DroppedMeasurements - droppedBefore).IsEqualTo(193L);
        await Assert.That(exportedWhileBlocked - droppedBefore).IsEqualTo(193L);

        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.All });
        connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
        await WaitForDeliveries();
        await Assert.That(listener.Deliveries).IsEqualTo(65);
        await Assert.That(ConnectionTelemetry.DroppedMeasurements - droppedBefore).IsEqualTo(193L);
    }

    [Test]
    public async Task BlockedPoolListenersDoNotSerializeOtherPoolDelivery()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var blocked = new ConnectionTelemetry.State(connection, new("blocked", false), true);
        var available = new ConnectionTelemetry.State(connection, new("available", false), true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedCount = 0;
        var droppedBefore = ConnectionTelemetry.DroppedMeasurements;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.connection.wait_time")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "db.client.connection.pool.name") continue;
                if (Equals(tag.Value, "available")) { delivered.TrySetResult(); return; }
                if (!Equals(tag.Value, "blocked")) continue;
                if (Interlocked.Increment(ref blockedCount) == 4) entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });
        listener.Start();
        try
        {
            for (var i = 0; i < 4; i++) blocked.Waited(System.Diagnostics.Stopwatch.GetTimestamp());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            available.Waited(System.Diagnostics.Stopwatch.GetTimestamp());
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(ConnectionTelemetry.DroppedMeasurements).IsEqualTo(droppedBefore);
        }
        finally
        {
            release.TrySetResult();
            await WaitForDeliveries();
        }
    }

    [Test]
    public async Task ThrowingLifecycleListenersReleaseDeliveryCapacity()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var delivered = 0;
        var droppedBefore = ConnectionTelemetry.DroppedMeasurements;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.connection.wait_time")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            Interlocked.Increment(ref delivered);
            throw new InvalidOperationException("Listener failure.");
        });
        listener.Start();
        for (var burst = 0; burst < 2; burst++)
        {
            for (var i = 0; i < 64; i++) connection.RecordConnectionWait(System.Diagnostics.Stopwatch.GetTimestamp());
            await WaitForDeliveries();
        }
        await Assert.That(delivered).IsEqualTo(128);
        await Assert.That(ConnectionTelemetry.DroppedMeasurements).IsEqualTo(droppedBefore);
    }

    private static async Task WaitForDeliveries()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ConnectionTelemetry.PendingMeasurements != 0) await Task.Delay(1, deadline.Token);
    }

    [Test]
    [Arguments("db.client.connection.create_time")]
    [Arguments("db.client.connection.wait_time")]
    public async Task AcquisitionListenerCannotDelayRentalOrPoolDisposal(string instrument)
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        using var listener = new BlockingListener(instrument);
        var rental = Task.Run(async () => await pool.RentAsync(default));
        Task? cleanup = null;
        try
        {
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var connection = await rental.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup = Task.Run(async () => await pool.DisposeAsync());
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(connection.IsConnected).IsFalse();
            await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Release();
            var connection = await rental.WaitAsync(TimeSpan.FromSeconds(5));
            pool.Return(connection);
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task HandoffListenerRunsAfterRetirementCacheFence()
    {
        using var configuration = new MetricConfigurationScope();
        await using var source = MaintenanceServer();
        await using var target = MaintenanceServer();
        var fenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", source.Port,
            options: new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
                CredentialCacheRetirementFence = () => fenced.TrySetResult(),
            });
        using var listener = new BlockingListener("redis.client.connection.handoff");
        try
        {
            await source.SendRawAsync(Encoding.UTF8.GetBytes($">4\r\n+MOVING\r\n:1\r\n:1\r\n+127.0.0.1:{target.Port}\r\n"));
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(fenced.Task.IsCompletedSuccessfully).IsTrue();
        }
        finally { listener.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloseListenerCannotDelayPendingFailureOrDisposal(bool dispose)
    {
        using var configuration = new MetricConfigurationScope();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        { SuppressReply = _ => { sent.TrySetResult(); return true; } };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        var pending = connection.SendAsync(new Commands.RawCommand(FakeRespServer.PingFrame)).AsTask();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var listener = new BlockingListener("redis.client.connection.closed");
        Task? cleanup = null;
        try
        {
            if (dispose) cleanup = Task.Run(async () => await connection.DisposeAsync());
            else server.CloseConnections();
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); })
                .Throws<RespireConnectionException>();
            cleanup ??= Task.Run(async () => await connection.DisposeAsync());
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Release();
            if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            try { using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (RespireConnectionException) { }
        }
    }

    [Test]
    public async Task CloseListenerCannotStopPoolCleanupAtFirstIdleSocket()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 }, null);
        var first = await pool.RentAsync(default);
        var second = await pool.RentAsync(default, reuseIdle: false);
        pool.Return(first);
        pool.Return(second);
        using var listener = new BlockingListener("redis.client.connection.closed");
        var cleanup = Task.Run(async () => await pool.DisposeAsync());
        try
        {
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(first.IsConnected || second.IsConnected).IsFalse();
            // Both physical closes finish while their selected metric deliveries remain
            // outstanding. The outer configuration scope must drain them after release.
            await Assert.That(ConnectionTelemetry.PendingMeasurements).IsEqualTo(2);
        }
        finally
        {
            listener.Release();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task IdleRetirementReturnsAndCompletesWhileCloseListenerBlocks()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { Protocol = RespProtocol.Resp2 });
        using var listener = new BlockingListener("redis.client.connection.closed");
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = Task.Run(async () =>
        {
            var drain = connection.RetireAsync();
            // The MOVING owner can register its drain deadline as soon as this returns.
            returned.TrySetResult();
            await drain;
        });
        try
        {
            await listener.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Release();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static FakeRespServer MaintenanceServer() => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
            : command == "CLIENT MAINT_NOTIFICATIONS ON" ? FakeRespServer.OkReply : null,
    };

    internal sealed class BlockingListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deliveries;
        internal Task Entered => _entered.Task;
        internal int Deliveries => Volatile.Read(ref _deliveries);

        internal BlockingListener(string name)
        {
            _listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == name)
                    current.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            {
                Interlocked.Increment(ref _deliveries);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            });
            _listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
            {
                Interlocked.Increment(ref _deliveries);
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
            });
            _listener.Start();
        }

        internal void Release() => _release.TrySetResult();
        public void Dispose() { Release(); _listener.Dispose(); }
    }
}
