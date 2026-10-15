using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Text;

namespace Respire.Tests.Networking;

/// <summary>Per-wake counter readings recorded inside one measured interval (preallocated).</summary>
internal sealed class WakeTrace(int wakes)
{
    public readonly long[] AllocatedAfterWake = new long[wakes];
    public readonly int[] WorkersAfterWake = new int[wakes];
    public DateTime StartedUtc;
    public DateTime EndedUtc;

    // Reports which wakes moved the thread's allocation counter, so a nonzero total shows
    // whether it is one runtime event (worker start, first-call stub) or a per-wake cost.
    public string Describe(int maxWakes = 12)
    {
        var text = new StringBuilder();
        long previous = 0;
        var allocating = 0;
        int minWorkers = int.MaxValue, maxWorkers = int.MinValue;
        var listed = new StringBuilder();
        for (var i = 0; i < AllocatedAfterWake.Length; i++)
        {
            var delta = AllocatedAfterWake[i] - previous;
            previous = AllocatedAfterWake[i];
            minWorkers = Math.Min(minWorkers, WorkersAfterWake[i]);
            maxWorkers = Math.Max(maxWorkers, WorkersAfterWake[i]);
            if (delta == 0) continue;
            if (allocating++ < maxWakes)
                listed.Append($"{Environment.NewLine}    wake {i}: +{delta} bytes, workers after wake {WorkersAfterWake[i]}");
        }
        text.Append($"{allocating} of {AllocatedAfterWake.Length} wakes allocated, {previous} bytes in total; ")
            .Append($"workers after each wake {minWorkers}..{maxWorkers}; ")
            .Append($"interval {StartedUtc:HH:mm:ss.ffffff}-{EndedUtc:HH:mm:ss.ffffff} UTC.");
        text.Append(listed);
        if (allocating > maxWakes) text.Append($"{Environment.NewLine}    ... {allocating - maxWakes} more allocating wakes");
        return text.ToString();
    }
}

/// <summary>Counter and pool state around one ordinary/control measurement pair.</summary>
internal readonly record struct WakeSample(
    long Bytes, long Control, int WorkersBefore, int WorkersAfter, int Gen0, int Gen1, int Gen2,
    long CompletedWorkItems, long PendingWorkItemsAfter);

/// <summary>
/// Records runtime events (GC, loader, JIT, exceptions, thread pool) while the probe runs.
/// Native runtime events are dispatched on the listener's own thread, so recording does not
/// allocate on the measured thread. It runs only in the isolated probe child.
/// </summary>
internal sealed class DispatchedWakeEventLog : EventListener
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords RuntimeKeywords = (EventKeywords)(
        0x1 // GC: GCStart/End reason and generation, allocation ticks
        | 0x8 // Loader: assembly and module loads
        | 0x10 // JIT: methods compiled or tiered up during the interval
        | 0x8000 // Exception
        | 0x10000); // Threading: worker thread start/stop and pool adjustments
    private const int Capacity = 20_000;

    // Field initializers run before the base constructor, which can call OnEventSourceCreated.
    private readonly ConcurrentQueue<Entry> _entries = new();
    private long _measuringThread;
    private long _dispatcherThread;
    private int _dispatcherMarked;
    private DateTime _calibrationBeforeUtc;
    private DateTime _calibrationAfterUtc;

    public DispatchedWakeEventLog()
    {
        // Runtime event timestamps and DateTime.UtcNow use different clocks. The first call of
        // this method JITs it on this thread, so its JIT event gives the offset between them.
        _calibrationBeforeUtc = DateTime.UtcNow;
        CalibrationProbe();
        _calibrationAfterUtc = DateTime.UtcNow;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalibrationProbe() { }

    public long MarkMeasuringThread()
    {
        // The marker source dispatches synchronously on this thread, which records its OS id.
        ProbeMarkers.Log.MeasuringThread();
        return Interlocked.Read(ref _measuringThread);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == RuntimeProvider) EnableEvents(eventSource, EventLevel.Verbose, RuntimeKeywords);
        else if (eventSource.Name == ProbeMarkers.SourceName) EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventSource.Name == ProbeMarkers.SourceName)
        {
            Interlocked.Exchange(ref eventData.EventId == ProbeMarkers.DispatcherEvent ? ref _dispatcherThread : ref _measuringThread,
                eventData.OSThreadId);
            return;
        }
        // Runtime events arrive on the dispatcher thread; mark it so its own JIT and GC work is filtered out.
        if (Interlocked.Exchange(ref _dispatcherMarked, 1) == 0) ProbeMarkers.Log.DispatcherThread();
        var payload = new StringBuilder();
        if (eventData.Payload is { } values && eventData.PayloadNames is { } names)
            for (var i = 0; i < values.Count && i < names.Count && payload.Length < 240; i++)
                if (names[i] is not ("ClrInstanceID" or "MethodID" or "ModuleID" or "MethodToken" or "MethodStartAddress"))
                    payload.Append(' ').Append(names[i]).Append('=').Append(values[i]);
        _entries.Enqueue(new Entry(eventData.TimeStamp.ToUniversalTime(), eventData.OSThreadId,
            eventData.EventName ?? eventData.EventId.ToString(), payload.ToString()));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    // Runtime events reach the listener asynchronously; give the dispatcher time to drain.
    public void WaitForDispatch() => Thread.Sleep(TimeSpan.FromSeconds(1.5));

    /// <summary>Lists runtime events between the boundaries, in order, with the boundaries inline.</summary>
    public string Describe(long measuringThread, int maxOtherThreadEvents, params (string Name, DateTime Utc)[] boundaries)
    {
        var dispatcher = Interlocked.Read(ref _dispatcherThread);
        var entries = _entries.Where(e => e.OsThreadId != dispatcher && !IsNoise(e.Name))
            .OrderBy(e => e.TimestampUtc).ToList();
        var calibration = entries.FirstOrDefault(e => e.Payload.Contains($"MethodName={nameof(CalibrationProbe)} "));
        var offset = TimeSpan.Zero;
        // The offset is only an estimate, and a measured interval can be shorter than its error
        // (CI once placed the ordinary interval's own JIT event outside it). Widen the window so an
        // event inside the interval is never dropped; neighbours may appear at the edges instead.
        TimeSpan margin;
        var text = new StringBuilder($"measuring OS thread {measuringThread}, listener dispatcher {dispatcher}; ");
        if (calibration.Name is null)
        {
            margin = TimeSpan.FromMilliseconds(5);
            text.Append("clock offset unknown (no calibration JIT event), boundaries uncorrected");
        }
        else
        {
            var halfWidth = (_calibrationAfterUtc - _calibrationBeforeUtc) / 2;
            offset = calibration.TimestampUtc - (_calibrationBeforeUtc + halfWidth);
            margin = TimeSpan.FromMilliseconds(Math.Max(1, 4 * halfWidth.TotalMilliseconds));
            text.Append($"event clock offset {offset.TotalMilliseconds:F3} ms "
                + $"(+/- {halfWidth.TotalMilliseconds:F3} ms)");
        }
        var from = boundaries.Min(b => b.Utc) + offset - margin;
        var to = boundaries.Max(b => b.Utc) + offset + margin;
        var interval = entries.Where(e => e.TimestampUtc >= from && e.TimestampUtc <= to).ToList();
        var lines = SelectReportedEvents(interval, measuringThread, maxOtherThreadEvents)
            .Select(e => (e.TimestampUtc, Line: $"os={e.OsThreadId}{(e.OsThreadId == measuringThread ? " (measuring)" : "")} {e.Name}{e.Payload}"))
            .ToList();
        text.Append($"; {interval.Count} events in the interval widened by {margin.TotalMilliseconds:F3} ms each side.");
        foreach (var (utc, line) in lines.Concat(boundaries.Select(b => (b.Utc + offset, Line: $"--- {b.Name}")))
                     .OrderBy(l => l.Item1))
        {
            text.Append($"{Environment.NewLine}    {utc:HH:mm:ss.ffffff} {(line.Length > 260 ? line[..260] : line)}");
        }
        if (interval.Count > lines.Count)
            text.Append($"{Environment.NewLine}    ... {interval.Count - lines.Count} more events from other threads");
        return text.ToString();
    }

    // Background tiered JIT activity can exhaust the display budget before the allocating wake.
    // Keep all events on the measured thread, even late ones, and bound only other threads' events.
    internal static IEnumerable<Entry> SelectReportedEvents(IEnumerable<Entry> entries,
        long measuringThread, int maxOtherThreadEvents)
    {
        var otherThreadEvents = 0;
        foreach (var entry in entries)
            if (entry.OsThreadId == measuringThread || otherThreadEvents++ < maxOtherThreadEvents)
                yield return entry;
    }

    // Code-heap bookkeeping repeats each JIT event without adding information.
    private static bool IsNoise(string name) => name is "MethodJitMemoryAllocatedForCode" or "MethodILToNativeMap";

    internal readonly record struct Entry(DateTime TimestampUtc, long OsThreadId, string Name, string Payload);

    [EventSource(Name = SourceName)]
    private sealed class ProbeMarkers : EventSource
    {
        public const string SourceName = "Respire-Tests-DispatchedWakeProbe";
        public static readonly ProbeMarkers Log = new();

        public const int DispatcherEvent = 2;

        [Event(1)]
        public void MeasuringThread() => WriteEvent(1);

        [Event(DispatcherEvent)]
        public void DispatcherThread() => WriteEvent(DispatcherEvent);
    }
}
