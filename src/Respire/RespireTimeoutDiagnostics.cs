namespace Respire;

/// <summary>The last observed stage of a command that timed out.</summary>
public enum RespireCommandStage
{
    /// <summary>No physical command was available to inspect.</summary>
    Unknown,
    /// <summary>Waiting for a connection or its initialization.</summary>
    Connecting,
    /// <summary>Waiting for space in the connection's in-flight queue; not enqueued.</summary>
    WaitingForCapacity,
    /// <summary>Serialized into the write buffer; no bytes of this command have been sent.</summary>
    Buffered,
    /// <summary>Some bytes have been sent, but the complete command has not been written.</summary>
    Writing,
    /// <summary>The complete command has been written; its reply has not completed.</summary>
    AwaitingReply
}

/// <summary>A best-effort, immutable diagnostic snapshot captured when a command times out.</summary>
/// <remarks>
/// Counters can change concurrently and are not an atomic view of the connection. Null means
/// the information was unavailable. No keys, values, credentials, or command arguments are captured.
/// Sending bytes does not prove that Redis received or executed them. Cause hints are diagnostic
/// possibilities, not a determination of server or network health.
/// </remarks>
public sealed class RespireTimeoutDiagnostics
{
    private RespireTimeoutDiagnostics() { }

    internal static RespireTimeoutDiagnostics Unavailable { get; } = new();

    /// <summary>The last observed command stage.</summary>
    public RespireCommandStage Stage { get; private set; }
    private long? WrittenBytes { get; init; }
    /// <summary>The physical connection endpoint, when known.</summary>
    public RespireEndpoint? Endpoint { get; private init; }
    /// <summary>A process-local physical connection identity, distinct from Redis CLIENT ID.</summary>
    public long? ConnectionId { get; private init; }
    /// <summary>The Redis CLIENT ID, if already obtained; capture performs no network I/O.</summary>
    public long? ServerClientId { get; private init; }
    /// <summary>Outstanding reply slots, including abandoned waits and transaction intermediates.</summary>
    public int? InflightCount { get; private init; }
    /// <summary>Serialized command bytes whose final replies have not yet been dequeued.
    /// Multi-command frames, including transactions, retain their full byte count until the final reply.</summary>
    public long? InflightBytes { get; private init; }
    /// <summary>Serialized bytes not yet accepted by the socket or TLS stream.</summary>
    public long? PendingWriteBytes { get; private init; }
    /// <summary>Time since the last successful socket or TLS read, if any.</summary>
    public TimeSpan? TimeSinceLastRead { get; private init; }
    /// <summary>Time since the last successful socket or TLS write, if any.</summary>
    public TimeSpan? TimeSinceLastWrite { get; private init; }
    /// <summary>Whether the inspected physical connection remains open.</summary>
    public bool? IsConnected { get; private init; }
    /// <summary>Whether the inspected connection owner is reconnecting, when known.</summary>
    public bool? IsReconnecting { get; private init; }
    /// <summary>Busy thread-pool worker threads at capture time.</summary>
    public int? BusyWorkerThreads { get; private init; }
    /// <summary>The configured minimum worker thread count.</summary>
    public int? MinWorkerThreads { get; private init; }
    /// <summary>Busy thread-pool I/O completion threads at capture time.</summary>
    public int? BusyIoThreads { get; private init; }
    /// <summary>The configured minimum I/O completion thread count.</summary>
    public int? MinIoThreads { get; private init; }
    /// <summary>Queued thread-pool work items at capture time.</summary>
    public long? PendingWorkItems { get; private init; }
    /// <summary>A heuristic: work is queued and busy workers have reached the configured minimum.</summary>
    public bool PossibleThreadPoolStarvation => PendingWorkItems > 0 && BusyWorkerThreads >= MinWorkerThreads;
    /// <summary>Suggested checks based on the observed stage and counters.</summary>
    public string Hint
    {
        get
        {
            if (Stage == RespireCommandStage.Unknown && PendingWorkItems is null)
                return "No timeout observations are available; inspect the original operation and its connection.";
            if (PossibleThreadPoolStarvation)
                return "Possible thread-pool starvation: inspect blocking work and worker availability.";
            if (Stage == RespireCommandStage.Connecting || IsReconnecting == true)
                return "Connection initialization or reconnect is delayed; check endpoint availability, DNS, TLS, and authentication.";
            if (Stage is RespireCommandStage.Buffered or RespireCommandStage.Writing || PendingWriteBytes > 0)
                return "Writes are queued: check large payloads ahead, socket backpressure, network throughput, and server reads.";
            if (Stage == RespireCommandStage.WaitingForCapacity)
                return "The in-flight queue is full: check slow server commands, reply sizes, and network latency.";
            return "Check slow server commands (SLOWLOG), large replies ahead, network latency, and thread-pool scheduling.";
        }
    }

    internal static RespireTimeoutDiagnostics Capture(
        RespireCommandStage stage = RespireCommandStage.Unknown, RespireEndpoint? endpoint = null,
        long? connectionId = null, long? serverClientId = null, int? inflightCount = null,
        long? inflightBytes = null, long? pendingWriteBytes = null,
        TimeSpan? timeSinceLastRead = null, TimeSpan? timeSinceLastWrite = null,
        bool? isConnected = null, bool? isReconnecting = null, long? writtenBytes = null)
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        return new RespireTimeoutDiagnostics
        {
            Stage = stage, WrittenBytes = writtenBytes, Endpoint = endpoint, ConnectionId = connectionId, ServerClientId = serverClientId,
            InflightCount = inflightCount, InflightBytes = inflightBytes, PendingWriteBytes = pendingWriteBytes,
            TimeSinceLastRead = timeSinceLastRead, TimeSinceLastWrite = timeSinceLastWrite,
            IsConnected = isConnected, IsReconnecting = isReconnecting,
            BusyWorkerThreads = Math.Max(0, maxWorkers - availableWorkers), MinWorkerThreads = minWorkers,
            BusyIoThreads = Math.Max(0, maxIo - availableIo), MinIoThreads = minIo,
            PendingWorkItems = ThreadPool.PendingWorkItemCount
        };
    }

    // Reuse connection and thread-pool observations across one deadline sweep. Only the
    // command stage differs; cloning does not sample counters or inspect the thread pool.
    internal RespireTimeoutDiagnostics ForCommand(long writeStart, long writeEnd)
    {
        if (WrittenBytes is not { } sent || writeEnd <= 0)
            return this;

        var stage = ComputeStage(sent, writeStart, writeEnd);
        if (Stage == stage)
            return this;

        var snapshot = (RespireTimeoutDiagnostics)MemberwiseClone();
        snapshot.Stage = stage;
        return snapshot;
    }

    internal static RespireCommandStage ComputeStage(long sent, long writeStart, long writeEnd)
        => sent >= writeEnd ? RespireCommandStage.AwaitingReply
            : sent > writeStart ? RespireCommandStage.Writing : RespireCommandStage.Buffered;

    internal string Describe()
        => $"Stage={Stage}; endpoint={Endpoint?.ToString() ?? "unknown"}; connection={ConnectionId?.ToString() ?? "unknown"}; " +
           $"in-flight={InflightCount}; in-flight bytes={InflightBytes}; pending write bytes={PendingWriteBytes}; " +
           $"last read={TimeSinceLastRead}; last write={TimeSinceLastWrite}; reconnecting={IsReconnecting}; " +
           $"workers busy/min={BusyWorkerThreads}/{MinWorkerThreads}; IO busy/min={BusyIoThreads}/{MinIoThreads}; " +
           $"pending work={PendingWorkItems}. {Hint}";
}
