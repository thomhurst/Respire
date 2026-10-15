using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Respire.ComparisonBenchmarks;

// Issue #1744 investigation harness. Calls the published methods unchanged;
// these async-loop observations are diagnostics, never BenchmarkDotNet scores.
var cases = new[] { typeof(CommonOperationsBenchmarks), typeof(ClientSideCachingBenchmarks) }
    .SelectMany(type => type.GetMethods().Where(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null
        && (type == typeof(CommonOperationsBenchmarks) || method.Name.EndsWith("_ServerRead", StringComparison.Ordinal))))
    .OrderBy(method => method.DeclaringType!.Name).ThenBy(method => method.Name).ToArray();
if (args is ["--list"] or ["--list", _])
{
    var manifest = JsonSerializer.Serialize(cases.Select(method => new
    {
        name = $"{method.DeclaringType!.Name}.{method.Name}",
        category = method.GetCustomAttribute<BenchmarkCategoryAttribute>()!.Categories.Single(),
        operationsPerInvoke = method.GetCustomAttribute<BenchmarkAttribute>()!.OperationsPerInvoke,
    }));
    if (args.Length == 2) File.WriteAllText(args[1], manifest);
    else Console.WriteLine(manifest);
    return;
}
if (args is ["--summarize", var tracePath, var reportPath])
{
    using var trace = new TraceLog(TraceLog.CreateFromEventPipeDataFile(tracePath));
    var stacks = new Dictionary<(string Type, string Stack), int>();
    var samples = 0;
    var missingStacks = 0;
    var measuring = false;
    var starts = 0;
    var stops = 0;
    foreach (var entry in trace.Events)
    {
        if (entry.ProviderName == LatencyDiagnosticEvents.ProviderName)
        {
            measuring = (int)entry.PayloadByName("start") == 1;
            if (measuring) starts++; else stops++;
        }
        if (!measuring || entry is not ClrThreadSampleTraceData sample) continue;
        samples++;
        var frames = new List<string>();
        for (var frame = entry.CallStack(); frame is not null; frame = frame.Caller)
            frames.Add(string.IsNullOrEmpty(frame.CodeAddress.FullMethodName)
                ? frame.CodeAddress.ToString() : frame.CodeAddress.FullMethodName);
        if (frames.Count == 0) missingStacks++;
        var stack = (sample.Type.ToString(), string.Join("\n", frames));
        stacks[stack] = stacks.GetValueOrDefault(stack) + 1;
    }
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
    {
        samples, missingStacks, starts, stops, eventsLost = trace.EventsLost,
        stacks = stacks.OrderByDescending(pair => pair.Value).Select(pair => new { count = pair.Value, type = pair.Key.Type, stack = pair.Key.Stack }),
        warning = "Samples include harness/trace-writer work. External samples include waits and are not CPU attribution. Neither sample type provides kernel off-CPU attribution. Retain raw events and missing-stack/lost-event counts.",
    }));
    if (samples == 0 || samples == missingStacks || trace.EventsLost != 0 || starts != 1 || stops != 1 || measuring)
        throw new InvalidOperationException("Incomplete profile: inspect the raw trace before attribution.");
    return;
}
if (args is not [var caseName, var directory, var mode] || mode is not ("plain" or "trace"))
    throw new ArgumentException("Use --list, --summarize TRACE REPORT, or CASE DIRECTORY plain|trace.");
var selected = cases.Single(method => $"{method.DeclaringType!.Name}.{method.Name}" == caseName);
var fixture = Activator.CreateInstance(selected.DeclaringType!)!;
var invoke = Bind(selected, fixture);
Directory.CreateDirectory(directory);
await ((Task)selected.DeclaringType!.GetMethod("Setup")!.Invoke(fixture, null)!);
try
{
    // Time-based warmup avoids giving the 100/200-operation methods 100/200x more warmup.
    await Exercise(invoke, TimeSpan.FromSeconds(2));
    EventPipeSession? session = null;
    FileStream? output = null;
    Task? copy = null;
    try
    {
        if (mode == "trace")
        {
            session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(
            [
                new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                new EventPipeProvider(LatencyDiagnosticEvents.ProviderName, EventLevel.Informational),
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational,
                    (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Threading
                        | ClrTraceEventParser.Keywords.Contention | ClrTraceEventParser.Keywords.Stack)),
            ], requestRundown: true, circularBufferMB: 128);
            output = File.Create(Path.Combine(directory, "cpu.nettrace"));
            copy = session.EventStream.CopyToAsync(output);
        }
        using var process = Process.GetCurrentProcess();
        var startedUtc = DateTimeOffset.UtcNow;
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        if (mode == "trace") LatencyDiagnosticEvents.Log.Boundary(1);
        var started = Stopwatch.GetTimestamp();
        var invocations = await Exercise(invoke, TimeSpan.FromSeconds(3));
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (mode == "trace") LatencyDiagnosticEvents.Log.Boundary(0);
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var operationsPerInvoke = selected.GetCustomAttribute<BenchmarkAttribute>()!.OperationsPerInvoke;
        var operations = invocations * operationsPerInvoke;
        File.WriteAllText(Path.Combine(directory, "observation.json"), JsonSerializer.Serialize(new
        {
            caseName, mode, startedUtc, processId = Environment.ProcessId,
            revision = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            phase = Environment.GetEnvironmentVariable("LATENCY_PHASE"),
            endpoint = $"{Environment.GetEnvironmentVariable("REDIS_HOST")}:{Environment.GetEnvironmentVariable("REDIS_PORT")}",
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            assemblies = new[] { typeof(Respire.RespireClient).Assembly, typeof(CommonOperationsBenchmarks).Assembly,
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly, Assembly.GetExecutingAssembly() }
                .Select(assembly => new { name = assembly.GetName().Name,
                    version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }),
            invocations, operationsPerInvoke, operations,
            wallNanosecondsPerOperation = elapsed.TotalNanoseconds / operations,
            processCpuNanosecondsPerOperation = cpu.TotalNanoseconds / operations,
            processAllocatedBytesPerOperation = (double)allocated / operations,
            warning = "Async-loop diagnostic, not a BDN result. Process totals include all fixture clients and harness work; CPU sums all threads and must not be subtracted from wall time to infer waiting. Trace mode includes profiler overhead.",
        }));
    }
    finally
    {
        try
        {
            if (session is not null) session.Stop();
            if (copy is not null) await copy;
        }
        finally
        {
            session?.Dispose();
            if (output is not null) await output.DisposeAsync();
        }
    }
}
finally
{
    await ((Task)selected.DeclaringType!.GetMethod("Cleanup")!.Invoke(fixture, null)!);
}

static async Task<long> Exercise(Func<ValueTask> invoke, TimeSpan duration)
{
    var started = Stopwatch.GetTimestamp();
    long count = 0;
    do
    {
        await invoke().ConfigureAwait(false);
        count++;
    } while (Stopwatch.GetElapsedTime(started) < duration);
    return count;
}

static Func<ValueTask> Bind(MethodInfo method, object fixture)
{
    if (method.ReturnType == typeof(Task))
    {
        var call = method.CreateDelegate<Func<Task>>(fixture);
        return () => new ValueTask(call());
    }
    var adapter = typeof(Adapters).GetMethod(method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
        ? nameof(Adapters.FromTask) : nameof(Adapters.FromValueTask))!;
    return (Func<ValueTask>)adapter.MakeGenericMethod(method.ReturnType.GenericTypeArguments.Single())
        .Invoke(null, [method, fixture])!;
}

internal static class Adapters
{
    public static Func<ValueTask> FromTask<T>(MethodInfo method, object fixture)
    {
        var call = method.CreateDelegate<Func<Task<T>>>(fixture);
        return async () => { _ = await call().ConfigureAwait(false); };
    }

    public static Func<ValueTask> FromValueTask<T>(MethodInfo method, object fixture)
    {
        var call = method.CreateDelegate<Func<ValueTask<T>>>(fixture);
        return async () => { _ = await call().ConfigureAwait(false); };
    }
}

[EventSource(Name = ProviderName)]
internal sealed class LatencyDiagnosticEvents : EventSource
{
    internal const string ProviderName = "Respire-ServerLatencyDiagnostics";
    internal static readonly LatencyDiagnosticEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(int start) => WriteEvent(1, start);
}
