using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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

    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration",
        unit: "s",
        description: "Duration of database client operations.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10],
        });

    public static bool IsEnabled => Source.HasListeners() || OperationDuration.Enabled;

    public static OperationScope StartOperation(
        string operation,
        string host,
        int port,
        int database,
        int? batchSize = null,
        string? storedProcedureName = null)
    {
        var traceEnabled = Source.HasListeners();
        var metricEnabled = OperationDuration.Enabled;
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
                { "server.address", host },
            };
            if (port != DefaultRedisPort)
            {
                tags.Add("server.port", port);
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
                activityName, ActivityKind.Client, default(ActivityContext), tags: tags);
        }

        return new OperationScope(activity, metricEnabled ? Stopwatch.GetTimestamp() : 0);
    }

    public static OperationScope StartBatchOperation<T>(
        string prefix,
        IReadOnlyList<T> operations,
        Func<T, string> operationName,
        string host,
        int port,
        int database,
        out string operation)
    {
        if (!IsEnabled)
        {
            operation = prefix;
            return default;
        }

        operation = BatchOperationName(prefix, operations, operationName);
        return StartOperation(
            operation,
            host,
            port,
            database,
            batchSize: operations.Count == 1 ? null : operations.Count);
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
        public void Complete(
            ClientCore core,
            string operation,
            string? storedProcedureName = null,
            Exception? error = null,
            RespireConnection? connection = null,
            int? batchSize = null)
            => Complete(
                operation,
                core.Multiplexer.Host,
                core.Multiplexer.Port,
                core.Options.Database,
                storedProcedureName,
                error,
                connection,
                batchSize);

        public void Complete(
            string operation,
            string host,
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

            if (startTimestamp == 0)
            {
                return;
            }

            var tags = new TagList
            {
                { "db.system.name", DatabaseSystem },
                { "db.namespace", database.ToString(CultureInfo.InvariantCulture) },
                { "db.operation.name", operation },
                { "server.address", host },
            };
            if (port != DefaultRedisPort)
            {
                tags.Add("server.port", port);
            }

            if (storedProcedureName is not null)
            {
                tags.Add("db.stored_procedure.name", storedProcedureName);
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
