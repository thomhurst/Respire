using System.Diagnostics;

namespace Respire.Streaming;

internal sealed class JobEnqueueTrace : IDisposable
{
    private static readonly ActivitySource? Source = CreateSource();
    private readonly Activity? _previous = Activity.Current;
    private readonly Activity? _activity;
    internal string Parent { get; }
    internal string State { get; }
    internal bool Succeeded { get; set; }

    internal JobEnqueueTrace() : this(Source) { }

    internal JobEnqueueTrace(ActivitySource? source)
    {
        try { _activity = source?.CreateActivity("job enqueue", ActivityKind.Producer)?.SetIdFormat(ActivityIdFormat.W3C).Start(); }
        catch
        {
            if (!ReferenceEquals(Activity.Current, _previous))
                try { Activity.Current?.Dispose(); } catch { }
            Restore();
        }
        // With no sampled producer span, forward an ambient W3C context if one exists.
        // Never allocate a synthetic activity just for propagation.
        var context = _activity ?? _previous;
        Parent = context?.IdFormat == ActivityIdFormat.W3C ? context.Id ?? "" : "";
        State = Parent.Length != 0 ? context?.TraceStateString ?? "" : "";
    }

    private static ActivitySource? CreateSource()
    {
        try { return new(RespireStreamWorkerTelemetry.ActivitySourceName); }
        catch { return null; }
    }

    private void Restore()
    {
        try { Activity.Current = _previous; } catch { }
    }

    public void Dispose()
    {
        try
        {
            if (!Succeeded) _activity?.SetStatus(ActivityStatusCode.Error);
            _activity?.Dispose();
        }
        catch { /* Telemetry listeners cannot change the enqueue outcome. */ }
        finally { Restore(); }
    }
}
