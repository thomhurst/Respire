using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>One LATENCY HISTORY event sample, recorded with millisecond latency precision.</summary>
public readonly record struct RespireLatencyHistorySample(DateTimeOffset Timestamp, TimeSpan Latency);

/// <summary>A LATENCY HISTOGRAM upper bound in microseconds and its cumulative sample count.</summary>
public readonly record struct RespireLatencyHistogramBucket(long UpperBoundMicroseconds, long CumulativeCount);

/// <summary>An owned command latency distribution. Buckets contain cumulative counts, not per-bucket counts.</summary>
/// <remarks>Command names are server-reported, including subcommand names. Repeated requested names can
/// produce repeated results. Buckets are caller-owned and mutable; record equality compares array references.
/// Unknown fields contain recursively copied GC-owned results; disposal is optional.</remarks>
public sealed record RespireLatencyHistogram(
    string Command, long Calls,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireLatencyHistogramBucket[] Buckets,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

public partial interface IServerCommands
{
    /// <summary>Returns one node's human-readable latency analysis. Redis 2.8.13+: LATENCY DOCTOR.</summary>
    ValueTask<string> LatencyDoctorAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns owned timestamp/latency samples for one event. An unknown event returns an empty array.</summary>
    /// <remarks>Redis 2.8.13+: LATENCY HISTORY. Event names are literal and never key-prefixed.</remarks>
    ValueTask<RespireLatencyHistorySample[]> LatencyHistoryAsync(string eventName, CancellationToken cancellationToken = default);
    /// <summary>Returns owned cumulative command latency histograms. Empty commands requests all available histograms.</summary>
    /// <remarks>Redis 7+: LATENCY HISTOGRAM. Names are copied before asynchronous work; unknown names and
    /// commands without samples are omitted by the server. Command names are not tokenized or key-prefixed.</remarks>
    ValueTask<RespireLatencyHistogram[]> LatencyHistogramsAsync(ReadOnlySpan<string> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Returns one node's human-readable memory analysis. Redis 4+: MEMORY DOCTOR.</summary>
    ValueTask<string> MemoryDoctorAsync(CancellationToken cancellationToken = default);
    /// <summary>Asks one node's allocator to release reclaimable memory. Requires AllowAdmin. Redis 4+: MEMORY PURGE.</summary>
    /// <remarks>This may block the server. An OK reply does not promise a reduction in resident memory.</remarks>
    ValueTask PurgeMemoryAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns one node's current number of slow-log entries. Redis 2.2.12+: SLOWLOG LEN.</summary>
    ValueTask<long> SlowLogLengthAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a latency analysis separately from every discovered node, including replicas.</summary>
    /// <remarks>Discovery cancellation throws; cancellation after discovery is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<string>[]> LatencyDoctorOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns event history separately from every discovered node, including replicas.</summary>
    /// <remarks>Discovery cancellation throws; cancellation after discovery is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<RespireLatencyHistorySample[]>[]> LatencyHistoryOnAllNodesAsync(string eventName, CancellationToken cancellationToken = default);
    /// <summary>Returns command histograms separately from every discovered node, without aggregating their distributions.</summary>
    /// <remarks>Discovery cancellation throws; cancellation after discovery is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<RespireLatencyHistogram[]>[]> LatencyHistogramsOnAllNodesAsync(ReadOnlySpan<string> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Returns memory analysis separately from every discovered node, including replicas.</summary>
    /// <remarks>Discovery cancellation throws; cancellation after discovery is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<string>[]> MemoryDoctorOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Requests allocator purging on every discovered node. Requires AllowAdmin; each successful result is true.</summary>
    /// <remarks>Purges may partly succeed. Discovery cancellation throws; later cancellation is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<bool>[]> PurgeMemoryOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns slow-log lengths separately from every discovered node, including replicas.</summary>
    /// <remarks>Discovery cancellation throws; cancellation after discovery is retained as per-node errors.</remarks>
    ValueTask<RespireServerResult<long>[]> SlowLogLengthOnAllNodesAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    private readonly record struct DiagnosticCall<T>(string Operation, CmdN Command,
        ResponseConverter<ServerCommands, T> Convert, bool RequiresAdmin = false);

    // Construct verbs alongside their calls; static initialization order across partials is unspecified.
    private static readonly DiagnosticCall<string> LatencyDoctorCall = new("LATENCY DOCTOR", new(new Verb(-1, "LATENCY", "DOCTOR"), []),
        static (ServerCommands _, in RespValue value) => ServerDiagnosticsParser.Text(in value));
    private static readonly DiagnosticCall<string> MemoryDoctorCall = new("MEMORY DOCTOR", new(new Verb(-1, "MEMORY", "DOCTOR"), []),
        static (ServerCommands _, in RespValue value) => ServerDiagnosticsParser.Text(in value));
    private static readonly DiagnosticCall<bool> PurgeMemoryCall = new("MEMORY PURGE", new(new Verb(-1, "MEMORY", "PURGE"), []),
        static (ServerCommands _, in RespValue value) => { ResponseReader.ExpectOk(in value); return true; }, true);
    private static readonly DiagnosticCall<long> SlowLogLengthCall = new("SLOWLOG LEN", new(new Verb(-1, "SLOWLOG", "LEN"), []),
        static (ServerCommands _, in RespValue value) => ServerDiagnosticsParser.NonnegativeInteger(in value));

    public ValueTask<string> LatencyDoctorAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticAsync(LatencyDoctorCall, cancellationToken);
    public ValueTask<RespireLatencyHistorySample[]> LatencyHistoryAsync(string eventName, CancellationToken cancellationToken = default) => ExecuteDiagnosticAsync(HistoryCall(eventName), cancellationToken);
    public ValueTask<RespireLatencyHistogram[]> LatencyHistogramsAsync(ReadOnlySpan<string> commands = default, CancellationToken cancellationToken = default) => ExecuteDiagnosticAsync(HistogramsCall(commands), cancellationToken);
    public ValueTask<string> MemoryDoctorAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticAsync(MemoryDoctorCall, cancellationToken);
    public async ValueTask PurgeMemoryAsync(CancellationToken cancellationToken = default) => _ = await ExecuteDiagnosticAsync(PurgeMemoryCall, cancellationToken).ConfigureAwait(false);
    public ValueTask<long> SlowLogLengthAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticAsync(SlowLogLengthCall, cancellationToken);

    public ValueTask<RespireServerResult<string>[]> LatencyDoctorOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(LatencyDoctorCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireLatencyHistorySample[]>[]> LatencyHistoryOnAllNodesAsync(string eventName, CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(HistoryCall(eventName), cancellationToken);
    public ValueTask<RespireServerResult<RespireLatencyHistogram[]>[]> LatencyHistogramsOnAllNodesAsync(ReadOnlySpan<string> commands = default, CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(HistogramsCall(commands), cancellationToken);
    public ValueTask<RespireServerResult<string>[]> MemoryDoctorOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(MemoryDoctorCall, cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> PurgeMemoryOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(PurgeMemoryCall, cancellationToken);
    public ValueTask<RespireServerResult<long>[]> SlowLogLengthOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteDiagnosticOnAllNodesAsync(SlowLogLengthCall, cancellationToken);

    private ValueTask<T> ExecuteDiagnosticAsync<T>(DiagnosticCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertAsync(call.Operation, call.Command, cancellationToken, call.Convert);
    }

    private ValueTask<RespireServerResult<T>[]> ExecuteDiagnosticOnAllNodesAsync<T>(DiagnosticCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        cancellationToken.ThrowIfCancellationRequested();
        return FanOutAsync(call.Operation, call.Command, cancellationToken, call.Convert);
    }

    private static DiagnosticCall<RespireLatencyHistorySample[]> HistoryCall(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return new("LATENCY HISTORY", new(new Verb(-1, "LATENCY", "HISTORY"), [eventName]),
            static (ServerCommands _, in RespValue value) => ServerDiagnosticsParser.History(in value));
    }

    private static DiagnosticCall<RespireLatencyHistogram[]> HistogramsCall(ReadOnlySpan<string> commands)
    {
        var arguments = new RespireValue[commands.Length];
        for (var index = 0; index < commands.Length; index++)
        {
            if (commands[index] is null)
                throw new ArgumentNullException(nameof(commands), $"Command at index {index} must not be null.");
            arguments[index] = commands[index];
        }
        return new("LATENCY HISTOGRAM", new(new Verb(-1, "LATENCY", "HISTOGRAM"), arguments),
            static (ServerCommands _, in RespValue value) => ServerDiagnosticsParser.Histograms(in value));
    }
}
