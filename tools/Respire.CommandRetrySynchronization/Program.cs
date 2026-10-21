using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Respire.Benchmarks;

// Issue #2065. Keep raw task/thread-pool/contention events, rather than interpreting
// CPU sample gaps or process CPU minus wall time as network/scheduler wait.
if (args is ["--summarize", var input, var directory])
{
    using var trace = new TraceLog(TraceLog.CreateFromEventPipeDataFile(input));
    using var timeline = new StreamWriter(Path.Combine(directory, "events.jsonl"));
    var counts = new Dictionary<string, int>();
    var boundaries = new List<(int Operation, int Start)>();
    var activeOperation = -1;
    long previousUnixMicroseconds = 0;
    var validClock = true;
    foreach (var entry in trace.Events)
    {
        if (entry.ProviderName != SynchronizationEvents.ProviderName
            && entry.ProviderName != "System.Threading.Tasks.TplEventSource"
            && entry.ProviderName != "Microsoft-Windows-DotNETRuntime") continue;
        if (entry.ProviderName == SynchronizationEvents.ProviderName)
        {
            var operation = (int)entry.PayloadByName("operation");
            var start = (int)entry.PayloadByName("start");
            var unixMicroseconds = (long)entry.PayloadByName("unixMicroseconds");
            validClock &= unixMicroseconds >= previousUnixMicroseconds && unixMicroseconds > 0;
            previousUnixMicroseconds = unixMicroseconds;
            boundaries.Add((operation, start));
            activeOperation = operation;
        }
        else if (activeOperation < 0) continue;
        var name = $"{entry.ProviderName}/{entry.EventName}";
        counts[name] = counts.GetValueOrDefault(name) + 1;
        var payload = entry.PayloadNames.ToDictionary(name => name,
            name => Convert.ToString(entry.PayloadByName(name), CultureInfo.InvariantCulture));
        timeline.WriteLine(JsonSerializer.Serialize(new
        {
            operation = activeOperation, relativeMSec = entry.TimeStampRelativeMSec, entry.ProcessID, entry.ThreadID,
            entry.ActivityID, entry.RelatedActivityID, provider = entry.ProviderName,
            eventName = entry.EventName, payload,
        }));
        if (entry.ProviderName == SynchronizationEvents.ProviderName && boundaries[^1].Start == 0)
            activeOperation = -1;
    }
    var expected = Enumerable.Range(0, 512).SelectMany(i => new[] { (i, 1), (i, 0) });
    var complete = validClock && boundaries.SequenceEqual(expected);
    var tasks = counts.Where(pair => pair.Key.StartsWith("System.Threading.Tasks.TplEventSource/", StringComparison.Ordinal))
        .Sum(pair => pair.Value);
    File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(new
    {
        trace.EventsLost, operations = boundaries.Count / 2, complete, taskEvents = tasks, counts,
        warning = "Instrumented diagnostic only. Task waits are not kernel off-CPU attribution. "
            + "Use operation markers, task IDs and native thread IDs with syscall timestamps; "
            + "do not sum overlapping waits or interpret strace durations as uninstrumented latency. "
            + "Absence of contention events is not proof of absence of all synchronization.",
    }));
    if (!complete || trace.EventsLost != 0 || tasks == 0)
        throw new InvalidOperationException("Incomplete synchronization trace; inspect raw evidence.");
    return;
}
if (args is not ["Default" or "Disabled" or "Enabled", var output])
    throw new ArgumentException("Use POLICY DIRECTORY or --summarize TRACE DIRECTORY.");

Directory.CreateDirectory(output);
var fixture = new CommandRetryBenchmarks { Policy = args[0] };
try
{
    await fixture.Setup();
    for (var i = 0; i < 4096; i++) await Check();
    // Initialize the provider before starting collection so its metadata is available.
    _ = SynchronizationEvents.Log;
    using var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(
    [
        new EventPipeProvider(SynchronizationEvents.ProviderName, EventLevel.Informational),
        new EventPipeProvider("System.Threading.Tasks.TplEventSource", EventLevel.Verbose, -1),
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
            (long)(ClrTraceEventParser.Keywords.Threading | ClrTraceEventParser.Keywords.Contention
                | ClrTraceEventParser.Keywords.GC)),
    ], requestRundown: true, circularBufferMB: 128);
    await using var trace = File.Create(Path.Combine(output, "synchronization.nettrace"));
    var copy = session.EventStream.CopyToAsync(trace);
    try
    {
        Console.WriteLine($"SYNC_RANGE_START unixMicroseconds={UnixMicroseconds()} pid={Environment.ProcessId}");
        for (var i = 0; i < 512; i++)
        {
            SynchronizationEvents.Log.Operation(i, 1, UnixMicroseconds());
            await Check();
            SynchronizationEvents.Log.Operation(i, 0, UnixMicroseconds());
        }
        Console.WriteLine($"SYNC_RANGE_STOP unixMicroseconds={UnixMicroseconds()} pid={Environment.ProcessId}");
        File.WriteAllText(Path.Combine(output, "exercise.json"), JsonSerializer.Serialize(new
        {
            policy = args[0], operations = 512, warmup = 4096, processId = Environment.ProcessId,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            assemblies = new[] { typeof(Respire.RespireClient).Assembly, typeof(SynchronizationEvents).Assembly }
                .Select(assembly => new { name = assembly.GetName().Name,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }),
            warning = "EventPipe and optional strace perturb execution. No benchmark scores or performance acceptance.",
        }));
    }
    finally
    {
        session.Stop();
        await copy;
    }
}
finally
{
    await fixture.Cleanup();
}

async ValueTask Check()
{
    if (await fixture.ScriptLength() != 5)
        throw new InvalidOperationException("Unexpected script result.");
}

static long UnixMicroseconds() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

[EventSource(Name = ProviderName)]
internal sealed class SynchronizationEvents : EventSource
{
    internal const string ProviderName = "Respire-CommandRetrySynchronization";
    internal static readonly SynchronizationEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    // Force the object[] overload: the long/long/long overload pads the two int
    // fields to 64 bits and silently corrupts the EventPipe payload layout.
    public void Operation(int operation, int start, long unixMicroseconds) =>
        WriteEvent(1, new object[] { operation, start, unixMicroseconds });
}
