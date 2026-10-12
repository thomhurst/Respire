using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Respire.Benchmarks;

internal static class TransportBatchTraceReport
{
    internal static void Write(string tracePath, string reportPath)
    {
        var converted = TraceLog.CreateFromEventPipeDataFile(tracePath);
        using var trace = new TraceLog(converted);
        using var output = new StreamWriter(reportPath);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(tracePath)!, "metadata.json")));
        output.WriteLine(JsonSerializer.Serialize(new { kind = "identity", metadata = metadata.RootElement }));
        var processId = metadata.RootElement.GetProperty("processId").GetInt32();
        var starts = new HashSet<string>();
        var stops = new HashSet<string>();
        var control = "outside-controls";
        var samples = 0;
        var stacks = 0;
        var types = 0;
        foreach (var entry in trace.Events)
        {
            if (entry.ProviderName == BatchDiagnosticEvents.ProviderName && entry.ID == (Microsoft.Diagnostics.Tracing.TraceEventID)1)
            {
                var name = entry.PayloadByName("control").ToString()!;
                var boundary = entry.PayloadByName("boundary").ToString()!;
                if (entry.ProcessID != processId || !(boundary == "start" ? starts : stops).Add(name))
                    throw new InvalidOperationException("Trace contains mismatched process identity or duplicate control boundaries.");
                control = boundary == "start" ? name : "outside-controls";
                output.WriteLine(JsonSerializer.Serialize(new { kind = "boundary", name, boundary,
                    timestampMilliseconds = entry.TimeStampRelativeMSec, processId = entry.ProcessID }));
            }
            if (entry is not GCAllocationTickTraceData allocation) continue;
            var stack = new List<string>();
            for (var frame = entry.CallStack(); frame is not null; frame = frame.Caller)
                stack.Add(string.IsNullOrEmpty(frame.CodeAddress.FullMethodName)
                    ? frame.CodeAddress.ToString() : frame.CodeAddress.FullMethodName);
            samples++;
            if (stack.Count != 0) stacks++;
            if (!string.IsNullOrEmpty(allocation.TypeName)) types++;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "allocation-sample", control, timestampMilliseconds = entry.TimeStampRelativeMSec,
                processId = entry.ProcessID, threadId = entry.ThreadID, type = allocation.TypeName,
                sampledObjectBytes = allocation.ObjectSize, allocationIntervalBytes = allocation.AllocationAmount64, stack,
            }));
        }
        output.WriteLine(JsonSerializer.Serialize(new { kind = "summary", samples, samplesWithStacks = stacks, samplesWithTypes = types,
            eventsLost = trace.EventsLost, controls = starts,
            warning = "AllocationIntervalBytes is all allocation since a tick, not bytes attributable to its sampled type. Missing stacks and dropped events prevent complete attribution." }));
        var expected = metadata.RootElement.GetProperty("observations").EnumerateArray()
            .Select(observation => observation.GetProperty("control").GetString()!).Append("ready").ToHashSet();
        if (samples == 0 || stacks == 0 || types == 0 || trace.EventsLost != 0 || !starts.SetEquals(expected) || !stops.SetEquals(expected))
            throw new InvalidOperationException("Allocation trace lacks samples, types, stacks or control boundaries, or lost events; retain raw evidence and do not claim complete attribution.");
    }
}
