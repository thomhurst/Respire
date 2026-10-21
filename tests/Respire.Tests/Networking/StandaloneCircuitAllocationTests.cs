using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// The allocation measurement runs in a child process, which provides the isolation.
public class StandaloneCircuitAllocationTests
{
    private const string ProbeMode = "RESPIRE_CIRCUIT_DISABLED_ALLOCATION_PROBE";
    private const string BusyWorkerControl = "RESPIRE_CIRCUIT_BUSY_WORKER_CONTROL";
    private const string StallStageControl = "RESPIRE_CIRCUIT_STALL_STAGE_CONTROL";
    private const string ReadyMarker = "RESPIRE_CIRCUIT_PROBE_READY";
    private static readonly long ProbeStarted = Stopwatch.GetTimestamp();

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DisabledDispatchAddsNoAllocation(bool raw, bool disabledRetryPolicy)
        => await RunProbeAsync(raw, disabledRetryPolicy, busyWorker: false);

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DisabledDispatchCompletesWithOneWorkerBusy(bool raw, bool disabledRetryPolicy)
        => await RunProbeAsync(raw, disabledRetryPolicy, busyWorker: true);

    private static async Task RunProbeAsync(bool raw, bool disabledRetryPolicy, bool busyWorker)
    {
        var start = CreateProbeStartInfo(raw, disabledRetryPolicy);
        start.Environment[BusyWorkerControl] = busyWorker.ToString();
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    private static ProcessStartInfo CreateProbeStartInfo(bool raw, bool disabledRetryPolicy)
    {
        // A blocked producer can trigger pool growth, whose Thread/StartHelper allocations
        // are charged to the signaling thread. Reuse the bounded isolated-probe workflow.
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(StandaloneCircuitAllocationTests).Assembly.Location);
        start.Environment[ProbeMode] = Environment.Version.ToString();
        start.Environment["RESPIRE_RAW_DISPATCH_ALLOCATION_PROBE"] = raw.ToString();
        start.Environment["RESPIRE_DISABLED_RETRY_ALLOCATION_PROBE"] = disabledRetryPolicy.ToString();
        // Measure fully optimized steady state, independent of asynchronous tier promotion.
        // Throughput comparisons retain the runtime's normal tiering settings.
        start.Environment["DOTNET_TieredCompilation"] = "0";
        return start;
    }

    [Test, ParallelLimiter<ChildProcessProbes>]
    [Arguments("entry")]
    [Arguments("pool-warmup")]
    [Arguments("measurement-dispatch")]
    [Arguments("client-disposal")]
    public async Task KilledProbeReportsLastStage(string stage)
    {
        var start = CreateProbeStartInfo(raw: false, disabledRetryPolicy: true);
        var marker = Path.Combine(Path.GetTempPath(), $"respire-circuit-ready-{Guid.NewGuid():N}");
        start.Environment[StallStageControl] = stage;
        start.Environment[ReadyMarker] = marker;
        try
        {
            // Start the short fault-injection deadline only after the child flushes its stage.
            var error = await Assert.That(() => AsyncFlushSignalTests.RunProbeAsync(start,
                TimeSpan.FromMilliseconds(250), marker)).Throws<InvalidOperationException>();
            await Assert.That(error!.InnerException is TimeoutException).IsTrue();
            await Assert.That(error.Message).Contains($"circuit probe stage={stage};");
            await Assert.That(error.Message).Contains($"runtime={Environment.Version};");
            await Assert.That(error.Message).Contains("raw=False; disabledRetry=True;");
        }
        finally { File.Delete(marker); }
    }

    private static void ReportStage(string stage)
    {
        // A fixed number of records, outside the dispatch counter boundaries. Flush each so a
        // killed child retains its last completed stage, even if the next operation never returns.
        Console.Error.WriteLine($"circuit probe stage={stage}; runtime={Environment.Version}; "
            + $"raw={Environment.GetEnvironmentVariable("RESPIRE_RAW_DISPATCH_ALLOCATION_PROBE")}; "
            + $"disabledRetry={Environment.GetEnvironmentVariable("RESPIRE_DISABLED_RETRY_ALLOCATION_PROBE")}; "
            + $"poolWorker={Thread.CurrentThread.IsThreadPoolThread}; workers={ThreadPool.ThreadCount}; "
            + $"elapsedMs={Stopwatch.GetElapsedTime(ProbeStarted).TotalMilliseconds:F0}");
        Console.Error.Flush();
        if (Environment.GetEnvironmentVariable(StallStageControl) == stage)
        {
            File.WriteAllText(Environment.GetEnvironmentVariable(ReadyMarker)!, "");
            Thread.Sleep(Timeout.Infinite);
        }
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var runtime = Environment.GetEnvironmentVariable(ProbeMode);
        if (runtime is null) return null;
        try
        {
            ReportStage("entry");
            if (runtime != Environment.Version.ToString()) throw new InvalidOperationException("Probe runtime differs from parent.");
            ReportStage("pool-warmup");
            ThreadPool.GetMinThreads(out _, out var minIo);
            ThreadPool.GetMaxThreads(out _, out var maxIo);
            if (!ThreadPool.SetMinThreads(2, minIo) || !ThreadPool.SetMaxThreads(2, maxIo))
                throw new InvalidOperationException("Could not configure the isolated two-worker pool.");
            using var entered = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            using var finished = new CountdownEvent(2);
            for (var i = 0; i < 2; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    entered.Signal();
                    release.Wait(TimeSpan.FromSeconds(5));
                    finished.Signal();
                }, 0, preferLocal: false);
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Workers did not start.");
            }
            finally
            {
                release.Set();
                if (!finished.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Workers did not finish warming.");
            }
            ReportStage("pool-ready");
            RunMeasurement(bool.Parse(Environment.GetEnvironmentVariable("RESPIRE_RAW_DISPATCH_ALLOCATION_PROBE") ?? "False"));
            ReportStage("complete");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // Keep the synchronous dispatch/wait loop on the child entry thread. An async setup
    // continuation can occupy one of the two fixed workers and starve socket/flush work.
#pragma warning disable RESP009 // Only the child entry thread blocks; both warmed pool workers remain available for network work.
    private static void RunMeasurement(bool raw)
    {
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        ReportStage("server-start");
        var server = new FakeRespServer(4, ":5\r\n"u8.ToArray());
        RespireClient? client = null;
        try
        {
            ReportStage("client-connect");
            client = RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
                Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false,
                RetryPolicy = bool.Parse(Environment.GetEnvironmentVariable("RESPIRE_DISABLED_RETRY_ALLOCATION_PROBE") ?? "False")
                    ? new() { MaxAttempts = 1 } : null,
            }).GetAwaiter().GetResult();
            ReportStage("command-warmup");
            for (var i = 0; i < 32; i++) client.Strings.LengthAsync("warm").AsTask().GetAwaiter().GetResult();
            server.SuppressReply = command => command == "STRLEN measured";
            var connection = client.Core.Multiplexer.GetConnection();
            RespireValue[] arguments = ["measured"];
            using var busyEntered = new ManualResetEventSlim();
            using var busyRelease = new ManualResetEventSlim();
            using var busyFinished = new ManualResetEventSlim();
            var busyWorker = bool.Parse(Environment.GetEnvironmentVariable(BusyWorkerControl) ?? "False");
            var busyExpired = false;
            try
            {
                if (busyWorker)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ =>
                    {
                        busyEntered.Set();
                        busyExpired = !busyRelease.Wait(TimeSpan.FromSeconds(20));
                        busyFinished.Set();
                    }, 0, preferLocal: false);
                    if (!busyEntered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Busy worker did not start.");
                }
                ReportStage("dispatch-warmup");
                Measure(client, server, connection, false, raw, arguments);
                ReportStage("control-warmup");
                Measure(client, server, connection, true, raw, arguments);
                ReportStage("no-gc-preparation");
                var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
                {
                    var workers = ThreadPool.ThreadCount;
                    ReportStage("measurement-dispatch");
                    var bytes = Measure(client, server, connection, false, raw, arguments);
                    ReportStage("measurement-control");
                    return (Bytes: bytes, Control: Measure(client, server, connection, true, raw, arguments),
                        WorkersBefore: workers, WorkersAfter: ThreadPool.ThreadCount);
                });
                Console.WriteLine($"runtime={Environment.Version}; raw={raw}; {measured}");
                if (measured.Bytes != 0 || measured.Control < 32L * 37 || measured.WorkersBefore != 2 || measured.WorkersAfter != 2)
                    throw new InvalidOperationException($"Expected zero disabled dispatch allocations, positive control, and two existing workers; observed {measured}.");
            }
            finally
            {
                busyRelease.Set();
                if (busyWorker && (!busyFinished.Wait(TimeSpan.FromSeconds(5)) || busyExpired))
                    throw new TimeoutException("Busy worker did not remain parked until measurement finished.");
            }
        }
        finally
        {
            try
            {
                ReportStage("client-disposal");
                client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                ReportStage("server-disposal");
                server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
#pragma warning restore RESP009

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireClient client, FakeRespServer server, RespireConnection connection, bool control,
        bool raw, RespireValue[] arguments)
    {
        long total = 0;
        for (var i = 0; i < 32; i++)
        {
            var commands = server.CommandsSeen;
            total += MeasureDispatch(client, control, raw, arguments, out var pending, out var rawPending);
            if (!connection.InspectForTests().Inflight.TryPeek(out var source)) throw new InvalidOperationException("Missing reply source.");
            if (!SpinWait.SpinUntil(() => server.CommandsSeen > commands, TimeSpan.FromSeconds(5)))
                throw new TimeoutException($"Server command receipt stalled: iteration={i}; raw={raw}; control={control}; "
                    + $"commandsBefore={commands}; commandsNow={server.CommandsSeen}; poolWorker={Thread.CurrentThread.IsThreadPoolThread}.");
#pragma warning disable RESP009 // Keep the measurement loop on one thread while releasing each deliberately parked reply.
            server.SendRawAsync(":5\r\n"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
#pragma warning restore RESP009
            if (!SpinWait.SpinUntil(() => (raw ? rawPending.IsCompleted : pending.IsCompleted) && source!.InspectForTests().ReferenceCount <= 1,
                TimeSpan.FromSeconds(5)))
                throw new TimeoutException($"Reply completion stalled: iteration={i}; raw={raw}; control={control}; "
                    + $"references={source!.InspectForTests().ReferenceCount}; poolWorker={Thread.CurrentThread.IsThreadPoolThread}.");
            if (raw)
            {
                using var result = rawPending.GetAwaiter().GetResult();
                if (result.AsInteger() != 5) throw new InvalidOperationException("Unexpected raw reply.");
            }
            else GC.KeepAlive(pending.GetAwaiter().GetResult());
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDispatch(RespireClient client, bool control, bool raw, RespireValue[] arguments,
        out ValueTask<long> pending, out ValueTask<RespireResult> rawPending)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        pending = raw ? default : client.Strings.LengthAsync("measured");
        // The leased raw path retains the allocation-free dispatch contract. The owned
        // default adds result materialization, which is separate from disabled telemetry.
        rawPending = raw ? client.ExecuteLeaseAsync(RespireCommands.String.STRLEN, arguments) : default;
        if (control) GC.KeepAlive(new byte[37]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
