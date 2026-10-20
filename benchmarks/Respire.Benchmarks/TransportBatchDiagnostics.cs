using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Respire.Benchmarks;

// Deliberately not a BenchmarkDotNet benchmark: tracing and controls never enter
// the public timing region. Each invocation is one explicitly identified process.
internal static class TransportBatchDiagnostics
{
    internal static async Task RunAsync(string directory, string revision, string phase,
        int connections, int count, int launch, int iterations)
    {
        if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$") || connections is not (1 or 2)
            || count is not (32 or 100) || launch < 1 || iterations is < 128 or > 4096)
            throw new ArgumentException("A full revision, 1/2 connections, 32/100 commands, positive launch and 128–4096 iterations are required.");
        Directory.CreateDirectory(directory);
        var fixture = new TransportAcceptanceBenchmarks { Connections = connections };
        var observations = new List<object>();
        await fixture.Setup().ConfigureAwait(false);
        try
        {
            // Warm both controls; retain normal runtime tiering and thread-pool policy.
            for (var i = 0; i < 64; i++)
            {
                await fixture.StandaloneBatch(count).ConfigureAwait(false);
                await ValueTaskFixtureAsync(fixture.DiagnosticClient, count).ConfigureAwait(false);
            }
            using var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(
            [
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                    (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Threading
                        | ClrTraceEventParser.Keywords.Stack)),
                new EventPipeProvider(BatchDiagnosticEvents.ProviderName, EventLevel.Informational),
            ], requestRundown: true, circularBufferMB: 128);
            await using var output = File.Create(Path.Combine(directory, "allocations.nettrace"));
            var copy = session.EventStream.CopyToAsync(output);
            try
            {
                // Instantiate the provider after starting EventPipe, before the first boundary.
                BatchDiagnosticEvents.Log.Boundary("ready", "start");
                await ObserveAsync("idle-before", iterations, observations,
                    async () => { await Task.Delay(250).ConfigureAwait(false); return 0; }).ConfigureAwait(false);

                var batches = new RespireBatch[iterations];
                BatchDiagnosticEvents.Log.Boundary("construction", "start");
                var construction = Construct(fixture.DiagnosticClient, count, batches);
                BatchDiagnosticEvents.Log.Boundary("construction", "stop");
                observations.Add(new { control = "construction", operations = iterations, construction });
                // Discard faults allocate; deliberately keep disposal outside construction.
                foreach (var batch in batches) batch.Dispose();

                await ObserveAsync("public-task", iterations, observations, async () =>
                {
                    var suspended = 0;
                    for (var i = 0; i < iterations; i++)
                    {
                        var task = fixture.StandaloneBatch(count);
                        if (!task.IsCompleted) suspended++;
                        _ = await task.ConfigureAwait(false);
                    }
                    return suspended;
                }).ConfigureAwait(false);
                await ObserveAsync("fixture-pooled-valuetask", iterations, observations, async () =>
                {
                    var suspended = 0;
                    for (var i = 0; i < iterations; i++)
                    {
                        var task = ValueTaskFixtureAsync(fixture.DiagnosticClient, count);
                        if (!task.IsCompleted) suspended++;
                        _ = await task.ConfigureAwait(false);
                    }
                    return suspended;
                }).ConfigureAwait(false);

                var gate = new BatchReplyGate();
                var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
                var port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");
#pragma warning disable RESP010 // One gated client is reused across all batch observations, outside measured boundaries.
                await using (var client = await RespireClient.ConnectAsync(new RespireOptions
                {
                    Endpoints = [new(host, port)], Connections = connections, Protocol = RespireProtocol.Resp2,
                    ThreadPoolMonitoring = false, TestingStreamFactory = gate.ConnectAsync,
                }).ConfigureAwait(false))
#pragma warning restore RESP010
                {
                    // This changes the receive transport only in the explicitly labeled
                    // causal control. Direct socket fixtures above remain unchanged.
                    await ObserveAsync("gated-first", 1, observations,
                        () => RunGatedAsync(client, gate, count, 1, fixtureTask: false)).ConfigureAwait(false);
                    // Build outside the pool-demand boundary, then keep 128 batch
                    // executions alive together. 12,800 commands fit the unchanged
                    // default 16,384-command capacity, even in the 100-command case.
                    var overlap = new RespireBatch[128];
                    var pending = new RespirePending<long>[overlap.Length];
                    var executing = new ValueTask<RespireBatchResult>[overlap.Length];
                    for (var i = 0; i < overlap.Length; i++) overlap[i] = Create(client, count, out pending[i]);
                    try
                    {
                        await ObserveAsync("gated-pool-demand", overlap.Length, observations,
                            () => RunOverlapAsync(gate, overlap, pending, executing)).ConfigureAwait(false);
                    }
                    finally { foreach (var batch in overlap) batch.Dispose(); }
                    await ObserveAsync("gated-direct", iterations, observations,
                        () => RunGatedAsync(client, gate, count, iterations, fixtureTask: false)).ConfigureAwait(false);
                    await ObserveAsync("gated-task", iterations, observations,
                        () => RunGatedAsync(client, gate, count, iterations, fixtureTask: true)).ConfigureAwait(false);
                }
                await ObserveAsync("idle-after", iterations, observations,
                    async () => { await Task.Delay(250).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
                await ObserveAsync("background-positive", 128, observations,
                    () => Task.Run(AllocateBackgroundControl)).ConfigureAwait(false);
                BatchDiagnosticEvents.Log.Boundary("ready", "stop");
            }
            finally
            {
                session.Stop();
                await copy.ConfigureAwait(false);
            }
            var metadata = new
            {
                schemaVersion = 1, revision, phase, connections, count, launch, iterations,
                processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
                fixtureAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TransportBatchDiagnostics).Assembly.Location))),
                libraryAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RespireClient).Assembly.Location))),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                errorMetrics = Environment.GetEnvironmentVariable("RESPIRE_BENCH_ERRORS") ?? "disabled",
                timestamp = DateTimeOffset.UtcNow, observations,
                limitations = "Allocation ticks sample types/stacks, not exact per-type bytes. Process totals include collector and controller work. Internal suspension/pool-miss counts are not instrumented; gated batches guarantee at least one incomplete reply. Diagnostic launches are separate from public timing launches.",
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        }
        finally { await fixture.Cleanup().ConfigureAwait(false); }
    }

    private static async Task ObserveAsync(string name, int operations, List<object> observations, Func<Task<int>> action)
    {
        BatchDiagnosticEvents.Log.Boundary(name, "start");
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var timestamp = Stopwatch.GetTimestamp();
        var incompleteAtCall = await action().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        var processBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        BatchDiagnosticEvents.Log.Boundary(name, "stop");
        observations.Add(new { control = name, operations, incompleteAtCall, processBytes, elapsedMilliseconds = elapsed });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocateBackgroundControl()
    {
        // A known worker allocation calibrates process-wide capture independently
        // of all batch activity. Its stack must never be called a transport cost.
        for (var i = 0; i < 128; i++) GC.KeepAlive(new byte[64 * 1024]);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Construct(RespireClient client, int count, RespireBatch[] batches)
    {
        var collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < batches.Length; i++) batches[i] = Create(client, count, out _);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new { threadBytes = bytes, bytesPerBatch = (double)bytes / batches.Length,
            collectionsDuring = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - collections };
    }

    private static RespireBatch Create(RespireClient client, int count, out RespirePending<long> first)
    {
        var batch = client.CreateBatch();
        first = batch.Increment("transport-acceptance:batch");
        for (var index = 1; index < count; index++) _ = batch.Increment("transport-acceptance:batch");
        return batch;
    }

    private static async Task<long> TaskFixtureAsync(RespireBatch batch, RespirePending<long> first)
    {
        await batch.ExecuteAsync().ConfigureAwait(false);
        return first.Result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<long> ValueTaskFixtureAsync(RespireClient client, int count)
    {
        using var batch = Create(client, count, out var first);
        await batch.ExecuteAsync().ConfigureAwait(false);
        return first.Result;
    }

    private static async Task<int> RunGatedAsync(RespireClient client, BatchReplyGate gate,
        int count, int iterations, bool fixtureTask)
    {
        var suspended = 0;
        for (var i = 0; i < iterations; i++)
        {
            using var batch = Create(client, count, out var first);
            var received = gate.Arm();
            Task<long>? task = null;
            ValueTask<RespireBatchResult> direct = default;
            try
            {
                if (fixtureTask) task = TaskFixtureAsync(batch, first);
                else direct = batch.ExecuteAsync();
                await received.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (fixtureTask ? task!.IsCompleted : direct.IsCompleted)
                    throw new InvalidOperationException("Batch completed while its replies were held.");
                suspended++;
            }
            finally { gate.Release(); }
            if (fixtureTask) _ = await task!.ConfigureAwait(false);
            else _ = await direct.ConfigureAwait(false);
            if (batch.Count != count || first.Result <= 0)
                throw new InvalidOperationException("Gated batch lost its pending results.");
        }
        return suspended;
    }

    private static async Task<int> RunOverlapAsync(BatchReplyGate gate, RespireBatch[] batches,
        RespirePending<long>[] pending, ValueTask<RespireBatchResult>[] executing)
    {
        var received = gate.Arm();
        try
        {
            for (var i = 0; i < batches.Length; i++) executing[i] = batches[i].ExecuteAsync();
            await received.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (executing.Any(static execution => execution.IsCompleted))
                throw new InvalidOperationException("An overlapping batch completed while replies were held.");
        }
        finally { gate.Release(); }
        for (var i = 0; i < batches.Length; i++)
        {
            var result = await executing[i].ConfigureAwait(false);
            if (result.Count != batches[i].Count || pending[i].Result <= 0)
                throw new InvalidOperationException("Overlapping batch lost its pending results.");
        }
        return batches.Length;
    }
}

[EventSource(Name = ProviderName)]
internal sealed class BatchDiagnosticEvents : EventSource
{
    internal const string ProviderName = "Respire-TransportBatchDiagnostics";
    internal static readonly BatchDiagnosticEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string control, string boundary) => WriteEvent(1, control, boundary);
}
