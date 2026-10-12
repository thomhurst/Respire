using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using Respire.Networking;

namespace Respire.Benchmarks;

// Allocation tracing runs in separate identified processes, never in public BDN timing.
internal static class TransportStreamDiagnostics
{
    private const int PayloadLength = 256 * 1024;
    private const string Key = "transport-acceptance:stream";

    internal static async Task RunAsync(string directory, string revision, string phase,
        int connections, string streamCase, int launch, int iterations)
    {
        if (!Regex.IsMatch(revision, "^[0-9a-f]{40}$") || connections is not (1 or 2)
            || streamCase is not ("instant" or "delayed" or "circuit-get")
            || launch < 1 || iterations is < 128 or > 4096)
            throw new ArgumentException("A full revision, 1/2 connections, instant/delayed/circuit-get case, positive launch and 128–4096 iterations are required.");
        Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, "metadata.json")) || File.Exists(Path.Combine(directory, "allocations.nettrace")))
            throw new IOException("Diagnostic launch evidence already exists; choose a new output directory.");
        var fixture = new TransportAcceptanceBenchmarks { Connections = connections };
        var observations = new List<object>();
        using var source = CreateSource(streamCase);
        var buffer = new byte[RespireConnection.StreamChunkSize];
        await fixture.Setup().ConfigureAwait(false);
        try
        {
            for (var i = 0; i < 64; i++)
            {
                await PublicAsync(fixture, streamCase).ConfigureAwait(false);
                await DirectAsync(fixture, streamCase, source, buffer).ConfigureAwait(false);
            }
            using var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(
            [
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                    (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Threading
                        | ClrTraceEventParser.Keywords.Stack)),
                new EventPipeProvider(StreamDiagnosticEvents.ProviderName, EventLevel.Informational),
            ], requestRundown: true, circularBufferMB: 128);
            await using var output = File.Create(Path.Combine(directory, "allocations.nettrace"));
            var copy = session.EventStream.CopyToAsync(output);
            try
            {
                StreamDiagnosticEvents.Log.Boundary("ready", "start");
                await ObserveAsync("idle-before", 1, observations,
                    async () => { await Task.Delay(250).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
                await ObserveAsync("linked-cancellation", iterations * 8, observations, () =>
                {
                    using var closed = new CancellationTokenSource();
                    using var caller = new CancellationTokenSource();
                    for (var i = 0; i < iterations * 8; i++)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, closed.Token);
                        GC.KeepAlive(linked.Token);
                    }
                    return Task.FromResult(0);
                }).ConfigureAwait(false);
                await ObserveAsync("exact-reader", iterations * 8, observations,
                    () => ReadExactAsync(iterations * 8)).ConfigureAwait(false);
                if (streamCase == "delayed")
                    await ObserveAsync("derived-reader", iterations, observations,
                        () => ReadDerivedAsync(iterations)).ConfigureAwait(false);
                await ObserveAsync("public-task", iterations, observations, async () =>
                {
                    var incomplete = 0;
                    for (var i = 0; i < iterations; i++)
                    {
                        var task = PublicAsync(fixture, streamCase);
                        if (!task.IsCompleted) incomplete++;
                        await task.ConfigureAwait(false);
                    }
                    return incomplete;
                }).ConfigureAwait(false);
                await ObserveAsync("pooled-valuetask-fixture", iterations, observations, async () =>
                {
                    var incomplete = 0;
                    for (var i = 0; i < iterations; i++)
                    {
                        var task = DirectAsync(fixture, streamCase, source, buffer);
                        if (!task.IsCompleted) incomplete++;
                        await task.ConfigureAwait(false);
                    }
                    return incomplete;
                }).ConfigureAwait(false);
                await RunTransportControlAsync(streamCase, connections, iterations, observations).ConfigureAwait(false);
                await ObserveAsync("write-wait-no-cancellation", 1024, observations,
                    () => RunWriteWaitsAsync(cancellable: false)).ConfigureAwait(false);
                await ObserveAsync("write-wait-cancellable", 1024, observations,
                    () => RunWriteWaitsAsync(cancellable: true)).ConfigureAwait(false);
                await ObserveAsync("final-wait-sequential", 1024, observations, RunFinalWaitSequentialAsync).ConfigureAwait(false);
                await ObserveAsync("final-wait-pool-demand", 1024, observations, ForceFinalWaitDemandAsync).ConfigureAwait(false);
                await ObserveAsync("idle-after", 1, observations,
                    async () => { await Task.Delay(250).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
                await ObserveAsync("background-positive", 128, observations, () => Task.Run(() =>
                {
                    for (var i = 0; i < 128; i++) GC.KeepAlive(new byte[64 * 1024]);
                    return 0;
                })).ConfigureAwait(false);
                StreamDiagnosticEvents.Log.Boundary("ready", "stop");
            }
            finally { session.Stop(); await copy.ConfigureAwait(false); }
            var metadata = new
            {
                schemaVersion = 1, revision, phase, connections, streamCase, launch, iterations,
                processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
                fixtureAssemblySha256 = Hash(typeof(TransportStreamDiagnostics).Assembly.Location),
                libraryAssemblySha256 = Hash(typeof(RespireClient).Assembly.Location),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                errorMetrics = Environment.GetEnvironmentVariable("RESPIRE_BENCH_ERRORS") ?? "disabled",
                timestamp = DateTimeOffset.UtcNow, observations,
                limitations = "Allocation ticks sample types/stacks, not exact per-type totals. Process bytes and durations include collector/controller work. Gates add their own wait/cancellation state. Pool-demand counts incomplete actual final waits, not internal pool misses. Separate diagnostic launches cannot identify the historical public GET launch regime or establish public latency/retention acceptance.",
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        }
        finally { await fixture.Cleanup().ConfigureAwait(false); }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task ObserveAsync(string control, int operations, List<object> observations, Func<Task<int>> action)
    {
        StreamDiagnosticEvents.Log.Boundary(control, "start");
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var timestamp = Stopwatch.GetTimestamp();
        var incompleteAtCall = await action().ConfigureAwait(false);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        var processBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        StreamDiagnosticEvents.Log.Boundary(control, "stop");
        observations.Add(new { control, operations, incompleteAtCall, processBytes, elapsedMilliseconds });
    }

    private static Task PublicAsync(TransportAcceptanceBenchmarks fixture, string streamCase) => streamCase switch
    {
        "instant" => fixture.StreamedSetWithInstantSource(),
        "delayed" => fixture.StreamedSetWithDelayedSource(),
        _ => fixture.CircuitStreamedGet(),
    };

    private static MemoryStream CreateSource(string streamCase) => streamCase == "delayed"
        ? new DelayedSource(new byte[PayloadLength]) : new MemoryStream(new byte[PayloadLength]);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask DirectAsync(TransportAcceptanceBenchmarks fixture, string streamCase,
        MemoryStream source, byte[] buffer)
    {
        if (streamCase == "circuit-get")
        {
            using var stream = await fixture.DiagnosticCircuitClient.Strings.GetStreamAsync(Key).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Missing streamed GET payload.");
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0) total += read;
            if (total != PayloadLength) throw new InvalidOperationException("Unexpected streamed GET length.");
        }
        else
        {
            source.Position = 0;
            if (!await fixture.DiagnosticClient.Strings.SetAsync(Key, source, PayloadLength).ConfigureAwait(false))
                throw new InvalidOperationException("Unexpected streamed SET reply.");
        }
    }

    private static async ValueTask UploadAsync(RespireClient client, MemoryStream source)
    {
        source.Position = 0;
        if (!await client.Strings.SetAsync(Key, source, PayloadLength).ConfigureAwait(false))
            throw new InvalidOperationException("Unexpected streamed SET reply.");
    }

    private static async ValueTask ReadGetAsync(RespireClient client, byte[] buffer)
    {
        using var stream = await client.Strings.GetStreamAsync(Key).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Missing streamed GET payload.");
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0) total += read;
        if (total != PayloadLength) throw new InvalidOperationException("Unexpected streamed GET length.");
    }

    private static Task<int> ReadExactAsync(int iterations)
    {
        using var source = new MemoryStream(new byte[PayloadLength]);
        for (var i = 0; i < iterations; i++)
        {
            source.Position = 0;
            using var reader = new RespireConnection.StreamPayloadReader(source, PayloadLength);
            var first = reader.StartRead(CancellationToken.None);
            for (var chunk = 0; chunk < 8; chunk++)
            {
                var fill = chunk == 0 ? first : reader.StartRead(CancellationToken.None);
                if (!fill.IsCompletedSuccessfully || !ReferenceEquals(first, fill)
                    || fill.Result.Length != RespireConnection.StreamChunkSize)
                    throw new InvalidOperationException("Exact reader did not reuse its completed eight-chunk task.");
            }
            if (!reader.IsComplete) throw new InvalidOperationException("Exact reader lost bytes.");
        }
        return Task.FromResult(0);
    }

    private static async Task<int> ReadDerivedAsync(int iterations)
    {
        using var source = new DelayedSource(new byte[PayloadLength]);
        var incomplete = 0;
        for (var i = 0; i < iterations; i++)
        {
            source.Position = 0;
            using var reader = new RespireConnection.StreamPayloadReader(source, PayloadLength);
            while (!reader.IsComplete)
            {
                var fill = reader.StartRead(CancellationToken.None);
                if (!fill.IsCompleted) incomplete++;
                if ((await fill.ConfigureAwait(false)).Length != RespireConnection.StreamChunkSize)
                    throw new InvalidOperationException("Derived reader lost a chunk.");
            }
        }
        return incomplete;
    }

    private static async Task RunTransportControlAsync(string streamCase, int connections, int iterations, List<object> observations)
    {
        var get = streamCase == "circuit-get";
        var gate = new StreamTransportGate(writes: !get);
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, port)], Connections = connections, Protocol = RespProtocol.Resp2,
            ThreadPoolMonitoring = false, TestingStreamFactory = gate.ConnectAsync,
            CircuitBreaker = get ? new() : null,
        }).ConfigureAwait(false);
        using var source = CreateSource(streamCase);
        var buffer = new byte[RespireConnection.StreamChunkSize];
        await ObserveAsync(get ? "held-get-receive" : "held-upload-payload-write", iterations, observations, async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var entered = gate.Arm(writesBeforeHold: get ? 0 : 1);
                var operation = get ? ReadGetAsync(client, buffer) : UploadAsync(client, source);
                try
                {
                    await entered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (operation.IsCompleted) throw new InvalidOperationException("Stream operation completed while its transport was held.");
                }
                finally { gate.Release(); }
                await operation.ConfigureAwait(false);
            }
            return iterations;
        }).ConfigureAwait(false);
    }

    private static async Task<int> RunFinalWaitSequentialAsync()
    {
        for (var i = 0; i < 1024; i++)
        {
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = RespireConnection.WaitForFinalFrameWriteAsync(write.Task, CancellationToken.None);
            if (wait.IsCompleted) throw new InvalidOperationException("Final wait failed to suspend.");
            write.SetResult();
            await wait.ConfigureAwait(false);
        }
        return 1024;
    }

    private static async Task<int> RunWriteWaitsAsync(bool cancellable)
    {
        using var cancellation = new CancellationTokenSource();
        for (var i = 0; i < 1024; i++)
        {
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = write.Task.WaitAsync(cancellable ? cancellation.Token : CancellationToken.None);
            if (wait.IsCompleted || ReferenceEquals(wait, write.Task) == cancellable)
                throw new InvalidOperationException("Cancellable write wait did not select the expected owner.");
            write.SetResult();
            await wait.ConfigureAwait(false);
        }
        return 1024;
    }

    private static async Task<int> ForceFinalWaitDemandAsync()
    {
        // Keep actual pooled owners live simultaneously. Task/TCS/controller allocations
        // have different stacks and must not be attributed to production pool misses.
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = new ValueTask[1024];
        try
        {
            for (var i = 0; i < waits.Length; i++)
            {
                waits[i] = RespireConnection.WaitForFinalFrameWriteAsync(write.Task, CancellationToken.None);
                if (waits[i].IsCompleted) throw new InvalidOperationException("Final wait failed to suspend.");
            }
        }
        finally { write.TrySetResult(); }
        foreach (var wait in waits) await wait.ConfigureAwait(false);
        return waits.Length;
    }

    private sealed class DelayedSource(byte[] payload) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}

[EventSource(Name = ProviderName)]
internal sealed class StreamDiagnosticEvents : EventSource
{
    internal const string ProviderName = "Respire-TransportStreamDiagnostics";
    internal static readonly StreamDiagnosticEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string control, string boundary) => WriteEvent(1, control, boundary);
}
