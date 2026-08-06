using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Respire.Internal;

/// <summary>
/// Built-in observability following the OTel database semantic conventions. Zero-cost until a
/// listener attaches: activities are only created when sampled, and metric timestamps are only
/// taken when an instrument is enabled. Subscribe with
/// <c>tracing.AddSource("Respire")</c> / <c>metrics.AddMeter("Respire")</c>.
/// </summary>
internal static class RespireTelemetry
{
    public const string SourceName = "Respire";

    private static readonly string Version =
        typeof(RespireTelemetry).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly ActivitySource Source = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    public static readonly Counter<long> Commands = Meter.CreateCounter<long>(
        "respire.commands", unit: "{command}", description: "Commands sent, tagged by operation and status.");

    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "respire.command.duration", unit: "ms", description: "Round-trip time per command.");

    /// <summary>A start timestamp, or 0 when no metric listener is attached (skips the clock read).</summary>
    public static long TimestampIfEnabled()
        => Commands.Enabled || CommandDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    public static Activity? StartActivity(string operation, string host, int port)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("db.system.name", "redis");
            activity.SetTag("db.operation.name", operation);
            activity.SetTag("server.address", host);
            activity.SetTag("server.port", port);
        }

        return activity;
    }

    public static void Record(string operation, long startTimestamp, bool success)
    {
        if (startTimestamp == 0)
        {
            return;
        }

        var tags = new TagList
        {
            { "db.operation.name", operation },
            { "status", success ? "ok" : "error" },
        };
        Commands.Add(1, tags);
        CommandDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, tags);
    }
}
