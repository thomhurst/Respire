using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;

namespace Respire.Internal;

internal static partial class RespireTelemetry
{
    // A nested holder avoids static field ordering across partial files. PublicationOnly
    // lets another listener try after a throwing exporter rejects instrument publication.
    private static class ErrorInstruments
    {
        private static readonly Lazy<Counter<long>> Instrument = new(static () => Meter.CreateCounter<long>(
            "redis.client.errors", "{error}", "Errors handled internally or surfaced at a client operation boundary."),
            LazyThreadSafetyMode.PublicationOnly);
        internal static Counter<long> Errors => Instrument.Value;
    }

    private static readonly KeyValuePair<string, object?> InternalErrorTag = new("redis.client.errors.internal", true);
    private static readonly KeyValuePair<string, object?> UserErrorTag = new("redis.client.errors.internal", false);
    private static readonly ErrorTypeNameCache ErrorTypeNames = new(128);
    // Larger counts retain their exact value without expanding this bounded boxing cache.
    private static readonly object[] ErrorRetryCounts = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    internal static bool ErrorsEnabled
    {
        get
        {
            try { return IsMetricEnabled(RespireMetricGroups.Resiliency, ErrorInstruments.Errors); }
            catch (Exception) { return false; }
        }
    }

    // Only the final owner may consume a response here. Borrowed, shared, native pooled
    // and deferred results require their own ownership policy before using this helper.
    // Successful responses do not rent observation storage or consult metric listeners.
    internal static ValueTask<T> ObserveFinalError<T>(ValueTask<T> response, int retryAttempts = 0)
    {
        if (!response.IsCompletedSuccessfully) return AwaitFinalError(response, retryAttempts);
        // A RESP source can report Succeeded and still translate an error in GetResult.
        try { return new(response.GetAwaiter().GetResult()); }
        catch (Exception error)
        {
            // The shared async boundary preserves cancellation status even when a
            // successful-status source translates cancellation during GetResult.
            return AwaitFinalError(ValueTask.FromException<T>(error), retryAttempts);
        }
    }

    internal static ValueTask ObserveFinalError(ValueTask response, int retryAttempts = 0)
    {
        if (!response.IsCompletedSuccessfully) return AwaitFinalError(response, retryAttempts);
        try
        {
#pragma warning disable CA1849 // IsCompletedSuccessfully proves GetResult cannot wait; a RESP source can still throw.
            response.GetAwaiter().GetResult();
#pragma warning restore CA1849
            return ValueTask.CompletedTask;
        }
        catch (Exception error)
        {
            return AwaitFinalError(ValueTask.FromException(error), retryAttempts);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<T> AwaitFinalError<T>(ValueTask<T> response, int retryAttempts)
    {
        try { return await response.ConfigureAwait(false); }
        catch (Exception error)
        {
            RecordError(error, internallyHandled: false, retryAttempts);
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitFinalError(ValueTask response, int retryAttempts)
    {
        try { await response.ConfigureAwait(false); }
        catch (Exception error)
        {
            RecordError(error, internallyHandled: false, retryAttempts);
            throw;
        }
    }

    // Call at a handled-error or final-failure boundary, never at exception construction.
    // Transport failures do not establish a logical command failure. Route integration
    // must declare one final owner and must not wrap another owner's final observation.
    internal static void RecordError(Exception error, bool internallyHandled, int retryAttempts = 0)
    {
        if (!ErrorsEnabled) return;
        var relevant = error;
        // Bound work for arbitrary external chains. At the cap retain the wrapper's meaning.
        for (var depth = 0; depth < 16; depth++)
        {
            if (relevant is RespireException { ErrorCause: { } cause }) relevant = cause;
            else if (relevant is AggregateException { InnerExceptions.Count: 1 } aggregate)
                relevant = aggregate.InnerExceptions[0];
            else break;
        }
        var code = relevant switch
        {
            RespireServerException server => StandardErrorCode(server.Code),
            RespireAuthenticationException { InnerException: RespireServerException server } => StandardErrorCode(server.Code),
            _ => null,
        };
        var category = relevant switch
        {
            // This boundary cannot infer the initiator from an exception token. Count
            // surfaced cancellation separately instead of calling it a server/network error.
            OperationCanceledException => "cancelled",
            RespireAuthenticationException => "auth",
            RespireServerException when code is "NOAUTH" or "NOPERM" or "WRONGPASS" => "auth",
            AuthenticationException => "tls",
            RespireServerException => "server",
            SocketException or IOException or RespireConnectionException or RespireTimeoutException => "network",
            _ => "other",
        };
        var tags = new TagList
        {
            LibraryTag, SystemTag,
            new("redis.client.errors.category", category),
            internallyHandled ? InternalErrorTag : UserErrorTag,
            new("error.type", ErrorTypeNames.Get(relevant.GetType())),
            new("redis.client.operation.retry_attempts", retryAttempts <= 0 ? ErrorRetryCounts[0]
                : retryAttempts < ErrorRetryCounts.Length ? ErrorRetryCounts[retryAttempts] : retryAttempts),
        };
        if (code is not null) tags.Add("db.response.status_code", code);
        try { ErrorInstruments.Errors.Add(1, in tags); }
        catch (Exception) { /* Listener failures cannot replace application errors or prevent recovery. */ }
    }

    // Lua and modules can return application data in arbitrary prefixes. Only bounded
    // standard codes become labels; unknown prefixes retain their category and type.
    private static string? StandardErrorCode(string code) => code switch
    {
        "ERR" or "WRONGTYPE" or "NOSCRIPT" or "BUSYGROUP" or "NOAUTH" or "NOPERM"
            or "WRONGPASS" or "LOADING" or "BUSY" or "BUSYKEY" or "CLUSTERDOWN"
            or "TRYAGAIN" or "MASTERDOWN" or "MOVED" or "ASK" or "READONLY" or "OOM"
            or "EXECABORT" or "CROSSSLOT" or "MISCONF" or "NOGROUP" or "NOREPLICAS" => code,
        _ => null,
    };
}
