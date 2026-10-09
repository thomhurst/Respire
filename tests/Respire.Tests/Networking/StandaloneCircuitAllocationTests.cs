using System.Runtime.CompilerServices;
using Respire.Networking;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class StandaloneCircuitAllocationTests
{
    private const string ProbeMode = "RESPIRE_CIRCUIT_DISABLED_ALLOCATION_PROBE";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisabledDispatchAddsNoAllocation(bool raw)
    {
        // A blocked producer can trigger pool growth, whose Thread/StartHelper allocations
        // are charged to the signaling thread. Reuse the bounded isolated-probe workflow.
        var start = AsyncFlushSignalTests.CreateProbeStartInfo(Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), typeof(StandaloneCircuitAllocationTests).Assembly.Location);
        start.Environment[ProbeMode] = Environment.Version.ToString();
        start.Environment["RESPIRE_RAW_DISPATCH_ALLOCATION_PROBE"] = raw.ToString();
        // Measure fully optimized steady state, independent of asynchronous tier promotion.
        // Throughput comparisons retain the runtime's normal tiering settings.
        start.Environment["DOTNET_TieredCompilation"] = "0";
        await AsyncFlushSignalTests.RunProbeAsync(start, TimeSpan.FromSeconds(30));
    }

    internal static int? RunIsolatedAllocationProbe()
    {
        var runtime = Environment.GetEnvironmentVariable(ProbeMode);
        if (runtime is null) return null;
        try
        {
            if (runtime != Environment.Version.ToString()) throw new InvalidOperationException("Probe runtime differs from parent.");
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
            RunMeasurementAsync(bool.Parse(Environment.GetEnvironmentVariable("RESPIRE_RAW_DISPATCH_ALLOCATION_PROBE") ?? "False"))
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task RunMeasurementAsync(bool raw)
    {
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        await using var server = new FakeRespServer(4, ":5\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
        });
        for (var i = 0; i < 32; i++) await client.Strings.LengthAsync("warm");
        server.SuppressReply = command => command == "STRLEN measured";
        var connection = client.Core.Multiplexer.GetConnection();
        RespireValue[] arguments = ["measured"];
        Measure(client, server, connection, false, raw, arguments);
        Measure(client, server, connection, true, raw, arguments);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            var workers = ThreadPool.ThreadCount;
            return (Bytes: Measure(client, server, connection, false, raw, arguments), Control: Measure(client, server, connection, true, raw, arguments),
                WorkersBefore: workers, WorkersAfter: ThreadPool.ThreadCount);
        });
        Console.WriteLine($"runtime={Environment.Version}; raw={raw}; {measured}");
        if (measured.Bytes != 0 || measured.Control < 32L * 37 || measured.WorkersBefore != 2 || measured.WorkersAfter != 2)
            throw new InvalidOperationException($"Expected zero disabled dispatch allocations, positive control, and two existing workers; observed {measured}.");
    }

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
            if (!SpinWait.SpinUntil(() => server.CommandsSeen > commands, TimeSpan.FromSeconds(5))) throw new TimeoutException();
            server.SendRawAsync(":5\r\n"u8.ToArray()).GetAwaiter().GetResult();
            if (!SpinWait.SpinUntil(() => (raw ? rawPending.IsCompleted : pending.IsCompleted) && Volatile.Read(ref ResponseReferences(source!)) <= 1,
                TimeSpan.FromSeconds(5))) throw new TimeoutException();
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
        rawPending = raw ? client.ExecuteAsync(RespireCommands.String.STRLEN, arguments) : default;
        if (control) GC.KeepAlive(new byte[37]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_refs")]
    private static extern ref int ResponseReferences(PendingResponse source);
}
