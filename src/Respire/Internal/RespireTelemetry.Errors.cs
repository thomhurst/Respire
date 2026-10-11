using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.CompilerServices;
using Reservoir;
using Respire.Protocol;

namespace Respire.Internal;

internal static partial class RespireTelemetry
{
    // A nested holder avoids relying on static field ordering across partial files.
    private static class ErrorInstruments
    {
        // Publication invokes exporter callbacks. Do not poison the holder's type initializer
        // when one throws; PublicationOnly permits another attempt after that listener leaves.
        private static readonly Lazy<Counter<long>> Instrument = new(static () => Meter.CreateCounter<long>(
            "redis.client.errors", "{error}", "Errors handled internally or surfaced at a client operation boundary."),
            LazyThreadSafetyMode.PublicationOnly);
        internal static Counter<long> Errors => Instrument.Value;
    }
    private static readonly KeyValuePair<string, object?> InternalErrorTag = new("redis.client.errors.internal", true);
    private static readonly KeyValuePair<string, object?> UserErrorTag = new("redis.client.errors.internal", false);
    // RuntimeType's name cache is weak and can disappear after GC. Retain names while their
    // types are alive without keeping collectible exception types or assemblies loaded.
    private static readonly ErrorTypeNameCache ErrorTypeNames = new(128);
    // Bound the cache independently of caller-supplied counts; larger counts retain their exact value.
    private static readonly object[] ErrorRetryCounts = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    // A physical close can fail handshake selection before that candidate is discarded.
    // Remember only failure identities, weakly, so selection can retain logical retries
    // without publishing the same physical failure twice. Successful routes never touch this.
    private static class ConnectionErrorMarkers
    {
        internal static readonly ConditionalWeakTable<Exception, object> Table = new();
        internal static readonly object Marker = new();
    }

    internal static void MarkConnectionError(Exception error)
    {
        try { ConnectionErrorMarkers.Table.GetValue(error, static _ => ConnectionErrorMarkers.Marker); }
        catch (Exception) { /* Observation must preserve the original failure. */ }
    }

    internal static bool IsObservedConnectionError(Exception error)
    {
        try
        {
            for (var depth = 0; depth < 16; depth++)
            {
                if (ConnectionErrorMarkers.Table.TryGetValue(error, out _)) return true;
                var cause = error is RespireException respire ? respire.ErrorCause
                    : error is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : null;
                if (cause is null) return false;
                error = cause;
            }
        }
        catch (Exception) { /* Observation must preserve the original failure. */ }
        return false;
    }

    internal static bool ErrorsEnabled
    {
        get
        {
            try { return IsMetricEnabled(RespireMetricGroups.Resiliency, ErrorInstruments.Errors); }
            catch (Exception) { return false; } // Includes instrument publication and listener selection.
        }
    }

    // One logical operation owns this lease through its final observer. Transport retries
    // and joined FUNCTION reload tasks can borrow it; return only after every borrower finishes.
    // Cache producers and hedge legs own separate leases. Callers copy their completed
    // retry counts; they never borrow another logical owner's pooled lease.
    // Copies retain their generation. Returning storage cannot give old borrowers access
    // to a later renter, and the private storage gate makes validation/mutation atomic.
    /// <summary>
    /// One leg of a concurrent fan-out. It borrows the caller's retry history and handled-error
    /// publication, but keeps its own endpoint, so parallel legs (and transport reroutes inside a
    /// leg) never overwrite each other's node.
    /// </summary>
    internal sealed class FanOutLegObservation(ErrorObservation caller) : IDispatchObservation
    {
        private readonly Lock _gate = new();
        private RespireEndpoint? _endpoint;

        internal ErrorObservation Observation => new(this, 0);

        internal RespireEndpoint? LegEndpoint { get { lock (_gate) return _endpoint; } }

        public bool IsOpen(long generation) => caller.IsOpen;
        public int Attempts(long generation) => caller.Attempts;
        public RespireEndpoint? Endpoint(long generation) => LegEndpoint;
        public void UseEndpoint(long generation, RespireEndpoint? endpoint) { lock (_gate) _endpoint = endpoint; }
        public void SetAttempts(long generation, int attempts) => caller.SetAttempts(attempts);
        public void Retry(long generation) => caller.Retry();

        // Handled errors always name this leg's node, never the caller's shared retained endpoint.
        public bool Handled(long generation, Exception error, RespireEndpoint? endpoint, bool overrideEndpoint)
            => caller.TryHandled(error, overrideEndpoint ? endpoint : LegEndpoint, overrideEndpoint: true);

        internal static FanOutLegObservation[] Create(int count, ErrorObservation caller)
        {
            var legs = new FanOutLegObservation[count];
            for (var i = 0; i < legs.Length; i++) legs[i] = new(caller);
            return legs;
        }

        /// <summary>
        /// Joins every leg, then names the caller's final error with the endpoint of the leg whose
        /// exception actually propagates (matched by reference, not inferred from task order).
        /// </summary>
        internal static async Task<T[]> JoinAsync<T>(Task<T>[] tasks, FanOutLegObservation[] legs, ErrorObservation caller)
        {
            try { return await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception propagated)
            {
                var surfaced = Array.FindIndex(tasks, task => task.Exception?.InnerExceptions.Any(
                    inner => ReferenceEquals(inner, propagated)) == true);
                if (surfaced < 0 && propagated is OperationCanceledException)
                    surfaced = Array.FindIndex(tasks, static task => task.IsCanceled);
                if (surfaced >= 0) caller.UseEndpoint(legs[surfaced].LegEndpoint);
                throw;
            }
        }
    }

    internal readonly struct ErrorObservation : IDisposable
    {
        private static readonly ObjectPool<ObservationState, Policy> Pool = new(4096);
        internal static Action? RentalObserverForTests;
        private readonly ObservationState? _state;
        private readonly long _generation;
        private readonly IDispatchObservation? _dispatch;

        internal ErrorObservation(IDispatchObservation dispatch, long generation)
        {
            _dispatch = dispatch;
            _generation = generation;
        }

        private ErrorObservation(ObservationState state, long generation)
        {
            _state = state;
            _generation = generation;
        }

        internal bool IsEmpty => _state is null && _dispatch is null;

        internal bool IsOpen
        {
            get
            {
                if (_dispatch is not null) return _dispatch.IsOpen(_generation);
                if (_state is null) return false;
                lock (_state.Gate) return _state.Active && _state.Generation == _generation && !_state.Final;
            }
        }

        internal int Attempts
        {
            get
            {
                if (_dispatch is not null) return _dispatch.Attempts(_generation);
                if (_state is null) return 0;
                lock (_state.Gate)
                    return _state.Active && _state.Generation == _generation ? _state.Attempts : 0;
            }
        }

        internal RespireEndpoint? Endpoint
        {
            get
            {
                if (_dispatch is not null) return _dispatch.Endpoint(_generation);
                if (_state is null) return null;
                lock (_state.Gate)
                    return _state.Active && _state.Generation == _generation ? _state.Endpoint : null;
            }
        }

        /// <summary>
        /// Records the node an attempt is about to contact. Cancellation observed before dispatch
        /// means nothing reaches Redis, so it clears the endpoint instead of naming a node.
        /// </summary>
        internal void UseEndpoint(RespireEndpoint? endpoint, CancellationToken cancellationToken)
            => UseEndpoint(cancellationToken.IsCancellationRequested ? null : endpoint);

        internal void UseEndpoint(RespireEndpoint? endpoint)
        {
            if (_dispatch is not null) { _dispatch.UseEndpoint(_generation, endpoint); return; }
            if (_state is null) return;
            lock (_state.Gate)
                if (IsActive(_state) && !_state.Final) _state.Endpoint = endpoint;
        }

        internal static ErrorObservation Rent(bool force = false)
        {
            if (!force && !ErrorsEnabled) return default;
            var state = Pool.Rent();
            Volatile.Read(ref RentalObserverForTests)?.Invoke();
            lock (state.Gate)
            {
                state.Generation = unchecked(state.Generation + 1);
                state.Attempts = 0;
                state.Endpoint = null;
                state.Final = false;
                state.Active = true;
                return new(state, state.Generation);
            }
        }
        internal void Handled(Exception error)
            => _ = TryHandled(error);

        // Parallel borrowers capture their own failed attempt's endpoint. The retained
        // endpoint still belongs to the last selection, independently of publication order.
        internal void Handled(Exception error, RespireEndpoint? endpoint)
            => _ = TryHandledCore(error, endpoint, overrideEndpoint: true);

        internal bool TryHandled(Exception error)
            => TryHandledCore(error, null, overrideEndpoint: false);

        // Forwarding views (for example coordination cleanup) keep the borrower's endpoint choice.
        internal bool TryHandled(Exception error, RespireEndpoint? endpoint, bool overrideEndpoint)
            => TryHandledCore(error, endpoint, overrideEndpoint);

        private bool TryHandledCore(Exception error, RespireEndpoint? endpoint, bool overrideEndpoint)
        {
            if (_dispatch is not null) return _dispatch.Handled(_generation, error, endpoint, overrideEndpoint);
            if (_state is null) return false;
            int attempt;
            lock (_state.Gate)
            {
                if (!IsActive(_state)) return false;
                if (!ErrorPublication.TryRecordHandled(ref _state.Attempts, _state.Final, out attempt)) return false;
                if (!overrideEndpoint) endpoint = _state.Endpoint;
            }
            RecordError(error, internallyHandled: true, attempt, endpoint);
            return true;
        }
        internal void Final(Exception error)
        {
            // DispatchResponseSource publishes after all response parsing and cleanup.
            if (_dispatch is not null) return;
            if (_state is null) return;
            int attempts;
            RespireEndpoint? endpoint;
            lock (_state.Gate)
            {
                if (!IsActive(_state) || !ErrorPublication.TryPublishFinal(_state.Attempts, ref _state.Final, out attempts)) return;
                endpoint = _state.Endpoint;
            }
            RecordError(error, internallyHandled: false, attempts, endpoint);
        }
        // Copy while the independently owned result leg is still live. Preserve the
        // caller's prefix and nested retry history without overflowing into a zero count.
        internal void CopyCompletedAttempts(int prefix, ErrorObservation completed)
        {
            SetAttempts((int)Math.Min((long)Math.Max(0, prefix) + completed.Attempts, int.MaxValue));
            // A leg cancelled before dispatch has no endpoint; that must clear the caller's
            // earlier routing selection rather than leave it on a failure that reached no node.
            UseEndpoint(completed.Endpoint);
        }

        internal void SetAttempts(int attempts)
        {
            if (_dispatch is not null) { _dispatch.SetAttempts(_generation, attempts); return; }
            if (_state is null) return;
            lock (_state.Gate)
            {
                if (!IsActive(_state)) return;
                ErrorPublication.SetAttempts(ref _state.Attempts, _state.Final, attempts);
            }
        }

        // A socket can already own this failure's internal event. Retain the caller's
        // retry without publishing again, atomically with parallel selection borrowers.
        internal void Retry()
        {
            if (_dispatch is not null) { _dispatch.Retry(_generation); return; }
            if (_state is null) return;
            lock (_state.Gate)
                if (IsActive(_state)) ErrorPublication.TryRecordRetry(ref _state.Attempts, _state.Final);
        }

        // Called under the storage gate, so validation and mutation cannot race a return/re-rent.
        private bool IsActive(ObservationState state)
        {
            if (state.Active && state.Generation == _generation) return true;
#if DEBUG
            throw new InvalidOperationException("The error observation has already been returned to its pool.");
#else
            return false;
#endif
        }
        public void Dispose() => DisposeAndGetAttempts();

        // The final owner has joined all borrowers. Capture its count and invalidate the
        // handle under one gate, avoiding a second lock per completed deferred command.
        internal int DisposeAndGetAttempts()
        {
            if (_dispatch is not null) return _dispatch.Attempts(_generation);
            if (_state is null) return 0;
            int attempts;
            lock (_state.Gate)
            {
                if (!_state.Active || _state.Generation != _generation) return 0;
                attempts = _state.Attempts;
                _state.Active = false;
                _state.Endpoint = null;
            }
            Pool.Return(_state);
            return attempts;
        }

        /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
        internal TestInspection InspectForTests() => new(this);

        internal readonly ref struct TestInspection(ErrorObservation observation)
        {
            internal object? StorageIdentity => (object?)observation._dispatch ?? observation._state;
        }

        private sealed class ObservationState
        {
            internal readonly Lock Gate = new();
            internal long Generation;
            internal int Attempts;
            internal RespireEndpoint? Endpoint;
            internal bool Final;
            internal bool Active;
        }

        private readonly struct Policy : IPooledObjectPolicy<ObservationState>
        {
            public ObservationState Create() => new();
            // Reset on rent under the storage gate. Returned handles are already inactive.
            public bool TryReset(ObservationState observation) => true;
            // Dispose returns a lease; discarded observations own no resources to destroy.
            public void Destroy(ObservationState observation) { }
        }
    }

    internal static ValueTask<T> ObserveFinalError<T>(ValueTask<T> response, ErrorObservation observation = default)
    {
        if (!response.IsCompletedSuccessfully) return AwaitFinalError(response, observation);
        // RESP sources can translate server errors in GetResult despite Succeeded status.
        // Consume once here, and keep synchronous success free of an async state machine.
        T result;
        try { result = response.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            // The shared async boundary records, disposes, and preserves cancellation status
            // when a successful-status source translates cancellation during GetResult.
            return AwaitFinalError(ValueTask.FromException<T>(error), observation);
        }
        observation.Dispose();
        return new(result);
    }

    internal static ValueTask ObserveFinalError(ValueTask response, ErrorObservation observation = default)
    {
        if (!response.IsCompletedSuccessfully) return AwaitFinalError(response, observation);
        try
        {
#pragma warning disable CA1849 // IsCompletedSuccessfully proves this GetResult cannot wait; RESP sources can still throw here.
            response.GetAwaiter().GetResult();
#pragma warning restore CA1849
        }
        catch (Exception error)
        {
            return AwaitFinalError(ValueTask.FromException(error), observation);
        }
        observation.Dispose();
        return ValueTask.CompletedTask;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<Stream?> ObserveStreamError(ValueTask<Stream?> response, ErrorObservation observation)
    {
        try
        {
            var stream = await response.ConfigureAwait(false);
            Networking.RespBulkPayloadPipe.SetErrorAttempts(stream, observation.Attempts);
            return stream;
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
        finally { observation.Dispose(); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask AwaitFinalError(ValueTask response, ErrorObservation observation)
    {
        try { await response.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (observation.IsEmpty) RecordError(error, internallyHandled: false);
            else observation.Final(error);
            throw;
        }
        finally { observation.Dispose(); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<T> AwaitFinalError<T>(ValueTask<T> response, ErrorObservation observation)
    {
        try { return await response.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (observation.IsEmpty) RecordError(error, internallyHandled: false);
            else observation.Final(error);
            throw;
        }
        finally { observation.Dispose(); }
    }

    internal static void RecordDiscardedError(in RespValue value, string? commandName, int retryAttempts = 0,
        RespireEndpoint? endpoint = null)
    {
        if (value.IsError && ErrorsEnabled)
            RecordError(ResponseReader.ServerError(in value, commandName), internallyHandled: true, retryAttempts, endpoint);
    }

    // Call only where the owner handles an error or publishes its final failure. Exception
    // construction and physical connection failure do not establish a logical command failure.
    // One owner reports each final boundary: pooled typed sources, routing/script scopes,
    // cache waiters, or deferred execution owners after cleanup. Never wrap one of these
    // with another final observer. Retry producers may be shared by several caller boundaries.
    // Other not-submitted failures (authentication, reconnect limits) were rejected by a node,
    // so only waits that end before ring admission drop the endpoint.
    private static bool IsBeforeAdmission(Exception error)
        => error is RespireCommandNotSubmittedException or RespireTimeoutException { IsCommandNotSubmitted: true };

    internal static void RecordError(Exception error, bool internallyHandled, int retryAttempts = 0,
        RespireEndpoint? endpoint = null)
    {
        if (!ErrorsEnabled) return;
        var relevant = error;
        var notSubmitted = false;
        // Unwrap transport wrappers without losing explicit authentication or timeout meaning.
        // Sixteen is a defensive work bound for arbitrarily nested external exception chains,
        // not a protocol limit. At the cap, classify the remaining wrapper without guessing its cause.
        for (var depth = 0; depth < 16; depth++)
        {
            notSubmitted |= IsBeforeAdmission(relevant);
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
        // Circuit rejection happens before transport dispatch but carries the exact
        // rejected endpoint. A route observation can still name an earlier selection (for
        // example the primary before Nearest picked a replica), so the circuit wins.
        if (relevant is RespireCircuitOpenException circuit) endpoint = circuit.Endpoint;
        // Cancellation or a capacity timeout before admission (capacity, streaming or credential
        // waits) never reached a node, so it must not keep the endpoint routing retained.
        else if (notSubmitted || IsBeforeAdmission(relevant)) endpoint = null;
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
        if (endpoint is { } target)
        {
            tags.Add("server.address", target.Host);
            if (target.Port != DefaultRedisPort) tags.Add("server.port", target.Port);
        }
        try { ErrorInstruments.Errors.Add(1, in tags); }
        catch (Exception) { /* A listener must never replace an application error or prevent recovery. */ }
    }

    // Lua and modules can return arbitrary prefixes, including application data. Unknown
    // prefixes retain the server category/type but never become metric label values.
    private static string? StandardErrorCode(string code) => code switch
    {
        "ERR" or "WRONGTYPE" or "NOSCRIPT" or "BUSYGROUP" or "NOAUTH" or "NOPERM"
            or "WRONGPASS" or "LOADING" or "BUSY" or "BUSYKEY" or "CLUSTERDOWN"
            or "TRYAGAIN" or "MASTERDOWN" or "MOVED" or "ASK" or "READONLY" or "OOM"
            or "EXECABORT" or "CROSSSLOT" or "MISCONF" or "NOGROUP" or "NOREPLICAS" => code,
        _ => null,
    };
}
