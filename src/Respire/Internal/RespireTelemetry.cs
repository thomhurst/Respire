using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Built-in observability following the OTel database and Redis semantic conventions. Query
/// text is deliberately not collected: Respire cannot reliably distinguish sensitive Redis
/// values from safe identifiers for arbitrary commands. Subscribe with
/// <c>tracing.AddSource("Respire")</c> / <c>metrics.AddMeter("Respire")</c>.
/// </summary>
internal static class RespireTelemetry
{
    public const string SourceName = "Respire";
    private const string DatabaseSystem = "redis";
    private const int DefaultRedisPort = 6379;

    private static readonly string Version =
        typeof(RespireTelemetry).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static readonly ActivitySource Source = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);
    private static readonly KeyValuePair<string, object?> LibraryTag = new("redis.client.library", "Respire:" + Version);
    private static readonly KeyValuePair<string, object?> SystemTag = new("db.system.name", DatabaseSystem);
    private static readonly MetricOperationNames MetricNames = new();

    private static readonly Counter<long> TransactionConflicts = Meter.CreateCounter<long>(
        "respire.transaction.watch.conflicts", "{attempt}", "Watched transaction attempts discarded by Redis.");
    private static readonly Counter<long> TransactionRetries = Meter.CreateCounter<long>(
        "respire.transaction.watch.retries", "{attempt}", "Additional watched transaction attempts started after conflicts.");

    internal static void RecordTransactionConflict()
    {
        try { TransactionConflicts.Add(1); }
        catch (Exception) { /* Diagnostics must not change transaction outcomes. */ }
    }

    internal static void RecordTransactionRetry()
    {
        try { TransactionRetries.Add(1); }
        catch (Exception) { /* Diagnostics must not prevent transaction retries. */ }
    }

    private static readonly Counter<long> HedgesSent = Meter.CreateCounter<long>(
        "respire.read.hedge.sent", "{request}", "Additional idempotent read requests dispatched by hedging.");
    private static readonly Counter<long> HedgesWon = Meter.CreateCounter<long>(
        "respire.read.hedge.won", "{request}", "Hedged read requests whose successful response was returned.");
    private static readonly Histogram<double> HedgeExtraLoad = Meter.CreateHistogram<double>(
        "respire.read.hedge.extra_load", "1", "Extra requests per eligible logical read (zero or one); the mean is the extra-load ratio.");

    internal static void RecordHedgeSent(RespireConnection connection)
    {
        try { HedgesSent.Add(1, new("server.address", connection.Host), new("server.port", connection.Port)); }
        catch (Exception) { /* Diagnostics cannot prevent a read from completing. */ }
    }

    internal static void RecordHedgeWon(RespireConnection connection)
    {
        try { HedgesWon.Add(1, new("server.address", connection.Host), new("server.port", connection.Port)); }
        catch (Exception) { /* Diagnostics cannot prevent a read from completing. */ }
    }

    internal static void RecordHedgeExtraLoad(RespireConnection connection, bool sent)
    {
        try { HedgeExtraLoad.Record(sent ? 1d : 0d, new("server.address", connection.Host), new("server.port", connection.Port)); }
        catch (Exception) { /* Diagnostics cannot prevent a read from completing. */ }
    }

    public static readonly Counter<long> SentinelFailovers = Meter.CreateCounter<long>(
        "respire.sentinel.failover", unit: "{failover}", description: "Validated Sentinel primary endpoint changes published by the client.");

    private static readonly Counter<long> SentinelGuardedLoggingFailures = Meter.CreateCounter<long>(
        "respire.sentinel.guarded_logging.failures", unit: "{failure}", description: "Non-fatal logger failures caught by Sentinel notification and optional discovery logging wrappers.");

    internal static void RecordSentinelGuardedLoggingFailure()
    {
        try { SentinelGuardedLoggingFailures.Add(1); }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        {
            // A failing metrics listener must not replace the logger failure or stop recovery.
        }
    }

    public static readonly ObservableGauge<long> SentinelRetiredGenerations = Meter.CreateObservableGauge(
        "respire.sentinel.generations.retired", () => SentinelRouter.RetiredGenerationCount, "{generation}",
        "Process-wide retired Sentinel generations still owned while commands, leases, or correction fences drain.");

    public static readonly Counter<long> CredentialRefreshes = Meter.CreateCounter<long>(
        "respire.authentication.refresh", "{attempt}", "Credential renewal outcomes, without credential material.");

    internal static void RecordCredentialRefresh(string host, int port, bool? succeeded, string stage, ILogger? logger)
    {
        try
        {
            CredentialRefreshes.Add(1,
                new KeyValuePair<string, object?>("server.address", host),
                new KeyValuePair<string, object?>("server.port", port),
                new KeyValuePair<string, object?>("respire.authentication.stage", stage),
                new KeyValuePair<string, object?>("respire.authentication.outcome", succeeded switch { true => "success", false => "failure", null => "retry" }));
        }
        catch { /* Instrumentation must not change authentication or transport state. */ }
        try
        {
            if (succeeded == false)
                logger?.LogWarning(new EventId(4001, "CredentialRefreshFailed"),
                    "Credential renewal failed at {Stage} for {Host}:{Port}", stage, host, port);
        }
        catch { /* User loggers must not terminate renewal. */ }
    }

    public static readonly ObservableGauge<double> ThreadPoolSchedulingDelay = Meter.CreateObservableGauge(
        "respire.thread_pool.scheduling.delay", ThreadPoolMonitor.ObserveDelay, "s",
        "Latest process-wide probe scheduling delay; a lower bound while the probe is pending.");
    public static readonly ObservableGauge<long> ThreadPoolBusyWorkers = Meter.CreateObservableGauge(
        "respire.thread_pool.workers.busy", ThreadPoolMonitor.ObserveBusyWorkers, "{thread}",
        "Busy worker threads when the scheduling probe was sampled.");
    public static readonly ObservableGauge<long> ThreadPoolMinimumWorkers = Meter.CreateObservableGauge(
        "respire.thread_pool.workers.min", ThreadPoolMonitor.ObserveMinimumWorkers, "{thread}",
        "Configured minimum worker threads when the scheduling probe was sampled.");
    public static readonly ObservableGauge<long> ThreadPoolPendingWork = Meter.CreateObservableGauge(
        "respire.thread_pool.work.pending", ThreadPoolMonitor.ObservePendingWork, "{work_item}",
        "Queued thread-pool work items when the scheduling probe was sampled.");

    public static readonly Histogram<long> ReconnectAttempts = Meter.CreateHistogram<long>(
        "respire.connection.reconnect.attempt", unit: "{attempt}", description: "One-based scheduled replacement attempt within a failed connection episode.");
    public static readonly Histogram<double> ReconnectDelays = Meter.CreateHistogram<double>(
        "respire.connection.reconnect.delay", unit: "s", description: "Scheduled delay before a configured connection replacement or discovery fallback attempt.");

    public static readonly Counter<long> MaintenanceNotifications = Meter.CreateCounter<long>(
        "redis.client.maintenance.notifications", unit: "{notification}", description: "Valid maintenance notifications delivered to diagnostics.");

    internal static void RecordMaintenanceNotification(string host, int port, string kind)
    {
        if (!IsMetricEnabled(RespireMetricGroups.Resiliency, MaintenanceNotifications)) return;
        TagList tags = default;
        tags.Add(LibraryTag);
        tags.Add(SystemTag);
        tags.Add("server.address", host);
        tags.Add("server.port", port);
        tags.Add("redis.client.connection.notification", kind);
        MaintenanceNotifications.Add(1, tags);
    }
    public static readonly Counter<long> MaintenanceNotificationsDropped = Meter.CreateCounter<long>(
        "respire.maintenance.notifications.dropped", unit: "{notification}", description: "Maintenance diagnostics dropped while listeners lag; protocol handling is unaffected.");
    public static readonly Counter<long> ClusterSlotMigrationsSkipped = Meter.CreateCounter<long>(
        "respire.cluster.slot_migrations.skipped", unit: "{notification}", description: "SMIGRATED notifications or entries that did not update Cluster slot ownership proactively; MOVED handling and discovery remain the fallback.");

    public static readonly Counter<long> ReconnectExhaustions = Meter.CreateCounter<long>(
        "respire.connection.reconnect.exhausted", unit: "{episode}", description: "Recovery episodes stopped by the configured replacement attempt limit.");

    public static readonly Counter<long> CoordinationCleanupsAbandoned = Meter.CreateCounter<long>(
        "respire.coordination.cleanup.abandoned", unit: "{cleanup}",
        description: "Background coordination cleanups that stopped without confirming their fence or release.");

    /// <summary>
    /// Records a coordination cleanup that gave up, for example a semaphore fence that Redis kept
    /// rejecting. Without it the leaked permit is invisible until capacity runs out.
    /// </summary>
    internal static void RecordCoordinationCleanupAbandoned(
        string primitive, string stage, string reason, ILogger? logger)
    {
        try
        {
            CoordinationCleanupsAbandoned.Add(1,
                new KeyValuePair<string, object?>("respire.coordination.primitive", primitive),
                new KeyValuePair<string, object?>("respire.coordination.cleanup.stage", stage),
                new KeyValuePair<string, object?>("respire.coordination.cleanup.reason", reason));
        }
        catch { /* Instrumentation must not change cleanup behaviour. */ }
        try
        {
            logger?.LogWarning(new EventId(4101, "CoordinationCleanupAbandoned"),
                "Background {Primitive} cleanup stopped at its {Stage} step ({Reason}); the owner may stay on Redis until it expires or is removed manually",
                primitive, stage, reason);
        }
        catch { /* User loggers must not terminate cleanup. */ }
    }

    public static readonly Counter<long> FailoverProbes = Meter.CreateCounter<long>(
        "respire.failover.probes", unit: "{probe}", description: "Failover deployment health probes.");

    public static readonly Counter<long> FailoverSwitches = Meter.CreateCounter<long>(
        "respire.failover.endpoint.switches", unit: "{switch}", description: "Selected deployment changes in failover groups.");
    private static readonly Counter<long> GeographicFailovers = Meter.CreateCounter<long>(
        "redis.client.geofailover.failovers", "{failover}", "Automatic switches between distinct, known failover deployments.");

    public static readonly Counter<long> FailoverMonitorErrors = Meter.CreateCounter<long>(
        "respire.failover.monitor.errors", unit: "{error}", description: "Unexpected errors while updating failover health.");

    public static readonly Histogram<double> FailoverProbeDuration = Meter.CreateHistogram<double>(
        "respire.failover.probe.duration", unit: "s", description: "Failover deployment health probe duration.");

    internal static void RecordFailoverProbe(RespireEndpoint endpoint, bool succeeded, double durationSeconds)
    {
        try
        {
            FailoverProbes.Add(1,
                new KeyValuePair<string, object?>("server.address", endpoint.Host),
                new KeyValuePair<string, object?>("server.port", endpoint.Port),
                new KeyValuePair<string, object?>("respire.failover.probe.result", succeeded ? "success" : "failure"));
            FailoverProbeDuration.Record(durationSeconds,
                new KeyValuePair<string, object?>("server.address", endpoint.Host),
                new KeyValuePair<string, object?>("server.port", endpoint.Port),
                new KeyValuePair<string, object?>("respire.failover.probe.result", succeeded ? "success" : "failure"));
        }
        catch { /* Metrics listeners must not change health decisions. */ }
    }

    internal static void RecordFailoverSwitch(RespireEndpoint? previous, RespireEndpoint? current, string reason)
    {
        if (IsMetricEnabled(RespireMetricGroups.Resiliency, GeographicFailovers) && previous is { } from && current is { } to && from != to)
        {
            try
            {
                TagList tags = default;
                tags.Add(LibraryTag);
                tags.Add(SystemTag);
                tags.Add("db.client.geofailover.reason", "automatic");
                // The only production caller is the failover group's health-driven selection loop.
                // Endpoints contain host/port only; authentication options are stored separately.
                tags.Add("db.client.geofailover.fail_from", from.ToString());
                tags.Add("db.client.geofailover.fail_to", to.ToString());
                GeographicFailovers.Add(1, tags);
            }
            catch { /* Metrics listeners must not change health decisions. */ }
        }
        if (!FailoverSwitches.Enabled) return;
        try
        {
            FailoverSwitches.Add(1,
                new KeyValuePair<string, object?>("respire.failover.switch.reason", reason),
                new KeyValuePair<string, object?>("respire.failover.endpoint.previous", previous?.ToString() ?? "none"),
                new KeyValuePair<string, object?>("respire.failover.endpoint.current", current?.ToString() ?? "none"));
        }
        catch { /* Metrics listeners must not change health decisions. */ }
    }

    internal static void RecordFailoverMonitorError(string source, Exception error)
    {
        try
        {
            FailoverMonitorErrors.Add(1,
                new KeyValuePair<string, object?>("respire.failover.error.source", source),
                new KeyValuePair<string, object?>("error.type", error.GetType().FullName));
        }
        catch { /* Metrics listeners must not stop health monitoring. */ }
    }

    internal static void RecordReconnectExhaustion(string host, int port, RespireReconnectSource source = RespireReconnectSource.Command)
        => ReconnectExhaustions.Add(1, new KeyValuePair<string, object?>("server.address", host),
            new KeyValuePair<string, object?>("server.port", port),
            new KeyValuePair<string, object?>("respire.connection.source", ReconnectSourceName(source)));

    internal static void RecordReconnectAttempt(string host, int port, int attempt, TimeSpan delay, RespireReconnectSource source = RespireReconnectSource.Command)
    {
        var address = new KeyValuePair<string, object?>("server.address", host);
        var endpointPort = new KeyValuePair<string, object?>("server.port", port);
        var sourceTag = new KeyValuePair<string, object?>("respire.connection.source", ReconnectSourceName(source));
        ReconnectAttempts.Record(attempt, address, endpointPort, sourceTag);
        ReconnectDelays.Record(delay.TotalSeconds, address, endpointPort, sourceTag);
    }

    // ClusterDiscovery and SentinelMonitor use RecordDiscoveryReconnect's scope tag instead.
    private static string ReconnectSourceName(RespireReconnectSource source) => source switch
    {
        RespireReconnectSource.Command => "command",
        RespireReconnectSource.Dedicated => "dedicated",
        RespireReconnectSource.PubSub => "pubsub",
        RespireReconnectSource.SentinelMonitor => "sentinel-monitor",
        _ => "unspecified",
    };

    internal static void RecordDiscoveryReconnect(RespireEndpoint endpoint, string scope, int attempt,
        TimeSpan? delay, ILogger? logger)
    {
        try
        {
            var address = new KeyValuePair<string, object?>("server.address", endpoint.Host);
            var port = new KeyValuePair<string, object?>("server.port", endpoint.Port);
            var scopeTag = new KeyValuePair<string, object?>("respire.reconnect.scope", scope);
            if (delay is { } scheduled)
            {
                ReconnectAttempts.Record(attempt, address, port, scopeTag);
                ReconnectDelays.Record(scheduled.TotalSeconds, address, port, scopeTag);
            }
            else
            {
                ReconnectExhaustions.Add(1, address, port, scopeTag);
            }
        }
        catch (Exception error)
        {
            // Meter listeners are user code and must not change discovery or its budget.
            try { logger?.LogWarning(error, "Reconnect telemetry listener threw for {Scope}", scope); }
            catch (Exception logError) when (logError is not OutOfMemoryException)
            { /* User loggers must not interrupt discovery or recovery notifications. */ }
        }
    }

    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration",
        unit: "s",
        description: "Duration of database client operations.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10],
        });

    public static readonly Counter<long> SubscriptionMessagesDropped = Meter.CreateCounter<long>(
        "respire.pubsub.messages.dropped",
        unit: "{message}",
        description: "Messages discarded because a subscription buffer was full.");

    public static readonly Counter<long> SubscriptionGaps = Meter.CreateCounter<long>(
        "respire.pubsub.delivery.gaps",
        unit: "{gap}",
        description: "Observed subscription interruptions and buffer discards; adjacent stream markers may coalesce.");

    private static readonly Counter<long> ClientCacheRequests = Meter.CreateCounter<long>(
        "redis.client.csc.requests", "{request}", "Client-side cache lookups by hit or miss result.");

    internal static void RecordCacheRequest(bool hit)
    {
        if (!IsMetricEnabled(RespireMetricGroups.ClientSideCaching, ClientCacheRequests)) return;
        try { ClientCacheRequests.Add(1, LibraryTag, SystemTag, new("redis.client.csc.result", hit ? "hit" : "miss")); }
        catch { /* Metrics listeners must not change cache results. */ }
    }

    public static readonly ObservableCounter<long> ClientCacheSharedReadRetirements = Meter.CreateObservableCounter(
        "respire.client_cache.shared_read.retirements", () => ClientSideCacheCoordinator.SharedReadRetirements,
        unit: "{request}", description: "Joinable shared reads retired by invalidation, clearing, or continuity loss.");

    public static readonly Counter<long> ClientCacheInvalidations = Meter.CreateCounter<long>(
        "respire.client_cache.invalidations",
        unit: "{key}",
        description: "Key invalidations observed by the client-side cache.");

    public static readonly Counter<long> ClientCacheEvictions = Meter.CreateCounter<long>(
        "redis.client.csc.evictions", "{eviction}", "Cached responses removed by capacity limits, expiration, or server invalidation.");

    internal static void RecordCacheEvictions(long count, string? reason = null)
    {
        if (!IsMetricEnabled(RespireMetricGroups.ClientSideCaching, ClientCacheEvictions) || count <= 0) return;
        try
        {
            if (reason is null) ClientCacheEvictions.Add(count, LibraryTag, SystemTag);
            else ClientCacheEvictions.Add(count, LibraryTag, SystemTag, new("redis.client.csc.reason", reason));
        }
        catch { /* Metrics listeners must not interrupt removal or invalidation. */ }
    }

    public static readonly Counter<long> ClientCacheContinuityFlushes = Meter.CreateCounter<long>(
        "respire.client_cache.continuity_flushes",
        unit: "{flush}",
        description: "Client-side cache flushes caused by uncertain tracking continuity.");

    internal static bool IsMetricEnabled(RespireMetricGroups group, Instrument instrument)
        => RespireMetrics.Current.Includes(group) && instrument.Enabled;

    public static bool IsEnabled => Source.HasListeners() || IsMetricEnabled(RespireMetricGroups.Command, OperationDuration);

    internal static bool IsOperationEnabled(string operation)
        => Source.HasListeners() || IsCommandMetricEnabled(operation);

    // Dispatch gates select a path only. Once captured, OperationStart is authoritative
    // through acquisition and completion; do not re-check the current policy after awaits.
    private static bool IsCommandMetricEnabled(string operation)
    {
        var selection = RespireMetrics.Current;
        return selection.Includes(RespireMetricGroups.Command) && OperationDuration.Enabled && selection.IncludesCommand(operation);
    }

    /// <summary>Retains metric eligibility and timing before connection acquisition can await.</summary>
    internal readonly record struct OperationStart(long Timestamp, bool MetricEnabled);

    internal static OperationStart CaptureOperationStart(string operation)
        => CaptureOperationStart(IsCommandMetricEnabled(operation));

    internal static OperationStart CaptureBatchStart<T>(string prefix, IReadOnlyList<T> operations, Func<T, string> operationName)
        => CaptureOperationStart(IsBatchMetricEnabled(prefix, operations, operationName));

    private static OperationStart CaptureOperationStart(bool metricEnabled)
        => new(Source.HasListeners() || metricEnabled ? Stopwatch.GetTimestamp() : 0, metricEnabled);

    private static bool IsBatchMetricEnabled<T>(string prefix, IReadOnlyList<T> operations, Func<T, string> operationName)
    {
        var selection = RespireMetrics.Current;
        if (!selection.Includes(RespireMetricGroups.Command) || !OperationDuration.Enabled) return false;
        if (!selection.HasCommandFilters) return true;
        if ((prefix is "WAIT" or "WAITAOF") && !selection.IncludesCommand(prefix)) return false;
        // One measurement covers the whole pipeline/transaction: suppress it if any member is excluded.
        for (var i = 0; i < operations.Count; i++)
            if (!selection.IncludesCommand(operationName(operations[i]))) return false;
        return operations.Count != 0 || selection.IncludesCommand(prefix);
    }

    internal static void RecordUnroutedBatchFailure<T>(string prefix, IReadOnlyList<T> operations,
        Func<T, string> operationName, int database, OperationStart started, Exception error, RespireEndpoint? endpoint = null)
    {
        if (started.Timestamp == 0) return;
        var metricEnabled = started.MetricEnabled;
        if (!Source.HasListeners() && !metricEnabled) return;
        var operation = BatchOperationName(prefix, operations, operationName);
        int? batchSize = operations.Count == 1 ? null : operations.Count;
        StartOperationCore(operation, endpoint?.Host, endpoint?.Port ?? DefaultRedisPort, database,
            batchSize, null, started.Timestamp, metricEnabled).Complete(operation, endpoint?.Host,
                endpoint?.Port ?? DefaultRedisPort, database, error: error, batchSize: batchSize);
    }

    internal static void RecordUnroutedFailure(string operation, int database, OperationStart started,
        Exception error, string? storedProcedureName = null, int? batchSize = null, RespireEndpoint? endpoint = null)
    {
        if (started.Timestamp == 0) return;
        // No data connection was acquired. Use an endpoint only when the caller can identify
        // the intended server; never identify a Sentinel seed or historical generation.
        var host = endpoint?.Host;
        var port = endpoint?.Port ?? DefaultRedisPort;
        StartOperation(operation, host, port, database, batchSize,
            storedProcedureName, started).Complete(operation, host, port,
                database, storedProcedureName, error, batchSize: batchSize);
    }

    public static void RecordSubscriptionMessageDropped(
        SubscriptionKind kind,
        SubscriptionOverflow overflow)
        => SubscriptionMessagesDropped.Add(1,
            new KeyValuePair<string, object?>("respire.subscription.kind", kind.ToString()),
            new KeyValuePair<string, object?>("respire.subscription.overflow", overflow.ToString()));

    public static void RecordSubscriptionGap(SubscriptionKind kind, RespireSubscriptionGapReason reason)
    {
        if (!SubscriptionGaps.Enabled) return;
        SubscriptionGaps.Add(1,
            new KeyValuePair<string, object?>("respire.subscription.kind", kind.ToString()),
            new KeyValuePair<string, object?>("respire.subscription.gap.reason", reason.ToString()));
    }

    public static OperationScope StartOperation(
        string operation, RespireEndpoint endpoint, int database,
        int? batchSize = null, string? storedProcedureName = null)
        => StartOperation(operation, endpoint.Host, endpoint.Port, database, batchSize, storedProcedureName);

    public static OperationScope StartOperation(
        string operation,
        string? host,
        int port,
        int database,
        int? batchSize = null,
        string? storedProcedureName = null,
        OperationStart? started = null)
        => StartOperationCore(operation, host, port, database, batchSize, storedProcedureName,
            started?.Timestamp ?? 0, started?.MetricEnabled ?? IsCommandMetricEnabled(operation));

    private static OperationScope StartOperationCore(string operation, string? host, int port, int database,
        int? batchSize, string? storedProcedureName, long started, bool metricEnabled)
    {
        var traceEnabled = Source.HasListeners();
        if (!traceEnabled && !metricEnabled)
        {
            return default;
        }

        Activity? activity = null;
        if (traceEnabled)
        {
            var tags = new ActivityTagsCollection
            {
                { "db.system.name", DatabaseSystem },
                { "db.namespace", database.ToString(CultureInfo.InvariantCulture) },
                { "db.operation.name", operation },
            };
            if (host is not null)
            {
                tags.Add("server.address", host);
                if (port != DefaultRedisPort) tags.Add("server.port", port);
            }

            if (batchSize is not null)
            {
                tags.Add("db.operation.batch.size", batchSize.Value);
            }

            if (storedProcedureName is not null)
            {
                tags.Add("db.stored_procedure.name", storedProcedureName);
            }

            // Sampling-relevant attributes must be supplied at creation time.
            var activityName = storedProcedureName is null
                ? operation
                : $"{operation} {storedProcedureName}";
            activity = Source.StartActivity(
                activityName, ActivityKind.Client, default(ActivityContext), tags: tags,
                startTime: started == 0 ? default : DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(started));
        }

        if (metricEnabled && started == 0) started = Stopwatch.GetTimestamp();
        return new OperationScope(activity, metricEnabled ? started : 0);
    }

    public static OperationScope StartBatchOperation<T>(
        string prefix, IReadOnlyList<T> operations, Func<T, string> operationName,
        RespireEndpoint endpoint, int database, out string operation)
        => StartBatchOperation(prefix, operations, operationName, endpoint.Host, endpoint.Port, database, out operation);

    public static OperationScope StartBatchOperation<T>(
        string prefix, IReadOnlyList<T> operations, Func<T, string> operationName,
        int database, out string operation, OperationStart? started = null)
    {
        var metricEnabled = started?.MetricEnabled ?? IsBatchMetricEnabled(prefix, operations, operationName);
        if (!Source.HasListeners() && !metricEnabled)
        {
            operation = prefix;
            return default;
        }

        operation = BatchOperationName(prefix, operations, operationName);
        return StartOperationCore(operation, null, DefaultRedisPort, database,
            operations.Count == 1 ? null : operations.Count, null, started?.Timestamp ?? 0, metricEnabled);
    }

    public static OperationScope StartBatchOperation<T>(
        string prefix,
        IReadOnlyList<T> operations,
        Func<T, string> operationName,
        string host,
        int port,
        int database,
        out string operation,
        OperationStart? started = null)
    {
        var metricEnabled = started?.MetricEnabled ?? IsBatchMetricEnabled(prefix, operations, operationName);
        if (!Source.HasListeners() && !metricEnabled)
        {
            operation = prefix;
            return default;
        }

        operation = BatchOperationName(prefix, operations, operationName);
        return StartOperationCore(
            operation,
            host,
            port,
            database,
            operations.Count == 1 ? null : operations.Count,
            null, started?.Timestamp ?? 0, metricEnabled);
    }

    private static string BatchOperationName<T>(
        string prefix, IReadOnlyList<T> operations, Func<T, string> operationName)
    {
        if (operations.Count == 1)
        {
            return operationName(operations[0]);
        }

        if (operations.Count > 1)
        {
            var first = operationName(operations[0]);
            for (var i = 1; i < operations.Count; i++)
            {
                if (!string.Equals(first, operationName(operations[i]), StringComparison.Ordinal))
                {
                    return prefix;
                }
            }

            return $"{prefix} {first}";
        }

        return prefix;
    }

    internal readonly struct OperationScope(Activity? activity, long startTimestamp)
    {
        internal void UpdateServerEndpoint(string host, int port)
        {
            if (activity is null) return;
            activity.SetTag("server.address", host);
            activity.SetTag("server.port", port == DefaultRedisPort ? (int?)null : port);
        }

        public void Complete(
            ClientCore core,
            string operation,
            string? storedProcedureName = null,
            Exception? error = null,
            RespireConnection? connection = null,
            int? batchSize = null)
        {
            if (activity is null && startTimestamp == 0) return;
            var endpoint = connection is null ? core.Endpoint : new RespireEndpoint(connection.Host, connection.Port);
            Complete(operation, endpoint.Host, endpoint.Port, core.Options.Database,
                storedProcedureName, error, connection, batchSize);
        }

        public void Complete(
            string operation,
            string? host,
            int port,
            int database,
            string? storedProcedureName = null,
            Exception? error = null,
            RespireConnection? connection = null,
            int? batchSize = null)
        {
            if (activity is null && startTimestamp == 0)
            {
                return;
            }

            var errorType = ErrorType(error);
            var responseStatusCode = error is RespireServerException { Code.Length: > 0 } serverError
                ? serverError.Code
                : null;
            var peerAddress = connection?.NetworkPeerAddress;
            var peerPort = connection?.NetworkPeerPort;

            double? activityDurationSeconds = null;
            if (activity is not null)
            {
                if (peerAddress is not null)
                {
                    activity.SetTag("network.peer.address", peerAddress);
                    activity.SetTag("network.peer.port", peerPort);
                }

                if (error is not null)
                {
                    activity.SetTag("error.type", errorType);
                    if (responseStatusCode is not null)
                    {
                        activity.SetTag("db.response.status_code", responseStatusCode);
                    }

                    activity.SetStatus(ActivityStatusCode.Error, errorType);
                }

                activity.Stop();
                activityDurationSeconds = activity.Duration.TotalSeconds;
            }

            if (startTimestamp == 0 || !OperationDuration.Enabled)
            {
                return;
            }

            var tags = new TagList
            {
                { "db.system.name", DatabaseSystem },
                { "db.namespace", database.ToString(CultureInfo.InvariantCulture) },
                { "db.operation.name", MetricNames.GetName(operation) },
            };
            if (host is not null)
            {
                tags.Add("server.address", host);
                if (port != DefaultRedisPort) tags.Add("server.port", port);
            }

            if (batchSize is not null)
            {
                tags.Add("db.operation.batch.size", batchSize.Value);
            }

            if (peerAddress is not null)
            {
                tags.Add("network.peer.address", peerAddress);
                tags.Add("network.peer.port", peerPort);
            }

            if (error is not null)
            {
                tags.Add("error.type", errorType);
                if (responseStatusCode is not null)
                {
                    tags.Add("db.response.status_code", responseStatusCode);
                }
            }

            OperationDuration.Record(
                activityDurationSeconds ?? Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                tags);
        }

        private static string? ErrorType(Exception? error)
        {
            if (error is null)
            {
                return null;
            }

            if (error is RespireServerException { Code.Length: > 0 } serverError)
            {
                return serverError.Code;
            }

            var relevantError = error is RespireConnectionException { InnerException: not null }
                ? error.InnerException
                : error;
            return relevantError.GetType().FullName ?? relevantError.GetType().Name;
        }
    }
}
