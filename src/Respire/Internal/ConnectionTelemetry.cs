using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using Respire.Networking;

namespace Respire.Internal;

// Current values are observed outside transport locks. Late listeners need no synthetic
// connect/disconnect events, and observing counts adds no work to command submission.
internal static class ConnectionTelemetry
{
    private static readonly Registry Pools = new();
    private const int MaximumPendingMeasurements = 64;
    private static int _pendingMeasurements;
    private static long _droppedMeasurements;

    // Diagnostic delivery has its own lifetime; transport disposal never waits for it.
    internal static int PendingMeasurements => Volatile.Read(ref _pendingMeasurements);
    // Counts rejected delivery attempts, not listener failures or shutdown loss.
    internal static long DroppedMeasurements => Interlocked.Read(ref _droppedMeasurements);

    internal static class CloseReason
    {
        internal const string Application = "application_close";
        internal const string Server = "server_close";
        internal const string Error = "error";
        internal const string IdleEviction = "pool_eviction_idle";
    }

    internal static State Attach(RespireConnection connection, RespireConnectionOptions options)
    {
        _ = RespireTelemetry.Meter;
        var pool = ForConnection(connection.Host, connection.Port, options);
        var state = new State(connection, pool, options.IsDedicatedConnection);
        pool.Add(state);
        return state;
    }

    private static Pool ForConnection(string host, int port, RespireConnectionOptions options)
    {
        var purpose = options switch
        {
            { SubscriptionConfirmationHandler: not null } => "pubsub",
            { IsDedicatedConnection: true } => "dedicated",
            _ => "shared",
        };
        return Pools.ForPool(host, port, options.Database, purpose);
    }

    internal static void ClosedBeforeHandshake(string host, int port, RespireConnectionOptions options,
        Exception error, CancellationToken callerToken, bool peerClosed)
    {
        if (RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionsClosed))
        {
            var reason = CloseReason.Error;
            if (peerClosed) reason = CloseReason.Server;
            else if (IsCallerCancellation(error, callerToken)) reason = CloseReason.Application;
            QueueClosed(ForConnection(host, port, options), reason, reason == CloseReason.Error ? error : null);
        }
    }

    internal static bool IsCallerCancellation(Exception error, CancellationToken callerToken)
        => callerToken.IsCancellationRequested && error is OperationCanceledException canceled
            && canceled.CancellationToken == callerToken;

    internal static bool IsPeerReset(Exception? error)
    {
        // TLS can wrap the socket reset in IOException. Do not classify local aborts,
        // timeouts or arbitrary I/O errors as a peer close.
        for (; error is not null; error = error.InnerException)
            if (error is SocketException { SocketErrorCode: SocketError.ConnectionReset } or EndOfStreamException)
                return true;
        return false;
    }

    internal static bool IsHandshakePeerClose(Socket socket, Exception error)
    {
        if (IsPeerReset(error)) return true;
        if (error is not IOException) return false;
        try
        {
            // SslStream reports handshake EOF as an IOException without an inner cause.
            // A readable socket with no bytes confirms FIN without matching exception text.
            return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
        }
        catch (SocketException socketError) { return IsPeerReset(socketError); }
        catch (ObjectDisposedException) { return false; }
    }

    internal static IEnumerable<Measurement<long>> ObserveConnections()
    {
        if (!RespireMetrics.Current.Includes(RespireMetricGroups.ConnectionBasic)) yield break;
        foreach (var pool in Pools.Snapshot)
        {
            var (idle, used, _) = pool.Read();
            yield return new(idle, pool.IdleTags);
            yield return new(used, pool.UsedTags);
        }
    }

    internal static IEnumerable<Measurement<long>> ObservePendingRequests()
    {
        if (!RespireMetrics.Current.Includes(RespireMetricGroups.ConnectionAdvanced)) yield break;
        foreach (var pool in Pools.Snapshot)
            yield return new(pool.Read().Pending, pool.Tags);
    }

    internal static IEnumerable<Measurement<long>> ObserveRelaxedTimeouts()
    {
        if (!RespireMetrics.Current.Includes(RespireMetricGroups.ConnectionBasic)) yield break;
        foreach (var pool in Pools.Snapshot)
            yield return new(pool.ReadRelaxedTimeouts(), pool.Tags);
    }

    internal sealed class State
    {
        private readonly WeakReference<RespireConnection> _connection;
        private readonly Pool _pool;
        private readonly bool _dedicated;
        private int _ready;
        private int _closed;
        private int _rented;
        private string? _requestedCloseReason;

        internal State(RespireConnection connection, Pool pool, bool dedicated)
        {
            _connection = new(connection);
            _pool = pool;
            _dedicated = dedicated;
            _rented = dedicated ? 1 : 0;
        }

        internal void Ready(long started)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            Volatile.Write(ref _ready, 1);
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionBasic, RespireTelemetry.ConnectionCreateTime)) return;
            QueueDuration(RespireTelemetry.ConnectionCreateTime, _pool, started);
        }

        internal void SetRented(bool rented) => Volatile.Write(ref _rented, rented ? 1 : 0);
        internal void RequestClose(string reason) => Volatile.Write(ref _requestedCloseReason, reason);

        internal bool IsCollected => !_connection.TryGetTarget(out _);

        internal void Waited(long started)
        {
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionWaitTime)) return;
            QueueDuration(RespireTelemetry.ConnectionWaitTime, _pool, started);
        }

        internal void HandedOff()
        {
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionBasic, RespireTelemetry.ConnectionHandoffs)) return;
            Queue(static pool => RespireTelemetry.ConnectionHandoffs.Add(1, pool.Tags), _pool);
        }

        internal bool HasRelaxedTimeout => Volatile.Read(ref _ready) != 0 && Volatile.Read(ref _closed) == 0
            && _connection.TryGetTarget(out var connection) && connection.IsConnected && connection.HasRelaxedTimeoutAllowance;

        internal void Closed(Exception? error, bool peerClosed)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _pool.Remove(this);
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionsClosed)) return;
            var reason = CloseReason.Error;
            if (peerClosed || IsPeerReset(error)) reason = CloseReason.Server;
            else if (error is null or RespireConnectionRetiredException
                || error is OperationCanceledException && Volatile.Read(ref _requestedCloseReason) == CloseReason.Application)
                reason = Volatile.Read(ref _requestedCloseReason) ?? CloseReason.Application;
            QueueClosed(_pool, reason, reason == CloseReason.Error ? error : null);
        }

        internal bool TryRead(out bool used, out int pending)
        {
            used = false;
            pending = 0;
            if (Volatile.Read(ref _ready) == 0 || Volatile.Read(ref _closed) != 0 ||
                !_connection.TryGetTarget(out var connection) || !connection.IsConnected) return false;
            pending = connection.PendingResponseCount;
            used = _pool.PubSub || pending != 0 || _dedicated && Volatile.Read(ref _rented) != 0;
            return true;
        }
    }

    private static void QueueDuration(Histogram<double> instrument, Pool pool, long started)
        => Queue(static state => state.Instrument.Record(state.Seconds, state.Pool.Tags),
            (Instrument: instrument, Pool: pool, Seconds: Stopwatch.GetElapsedTime(started).TotalSeconds));

    private static void QueueClosed(Pool pool, string reason, Exception? error)
        => Queue(static state => RecordClosed(state.Pool, state.Reason, state.Error),
            (Pool: pool, Reason: reason, Error: error));

    private static void Queue<T>(Action<T> record, T value)
    {
        // Enablement, duration and ownership are captured before queueing. Delivery must
        // not retain an acquisition reservation or hold up transport cleanup/retirement.
        // Bound queued and running callbacks together. A stuck listener must not retain
        // an unbounded backlog or keep consuming additional thread-pool workers.
        var pending = Volatile.Read(ref _pendingMeasurements);
        while (true)
        {
            if (pending >= MaximumPendingMeasurements)
            {
                Interlocked.Increment(ref _droppedMeasurements);
                return;
            }
            var observed = Interlocked.CompareExchange(ref _pendingMeasurements, pending + 1, pending);
            if (observed == pending) break;
            pending = observed;
        }
        try
        {
            if (ThreadPool.UnsafeQueueUserWorkItem(static state =>
            {
                try { state.Record(state.Value); }
                catch { /* Listener failures must not replace connection outcomes. */ }
                finally { Interlocked.Decrement(ref _pendingMeasurements); }
            }, (Record: record, Value: value), preferLocal: false)) return;
        }
        catch { /* A failed diagnostic enqueue must not replace transport or disposal outcomes. */ }
        Interlocked.Decrement(ref _pendingMeasurements);
        Interlocked.Increment(ref _droppedMeasurements);
    }

    private static void RecordClosed(Pool pool, string reason, Exception? error)
    {
        try
        {
            TagList tags = default;
            foreach (var tag in pool.Tags) tags.Add(tag);
            tags.Add("redis.client.connection.close.reason", reason);
            if (error is not null)
            {
                var cause = error is RespireConnectionException { InnerException: not null } and not RespireAuthenticationException
                    ? error.InnerException! : error;
                tags.Add("error.type", cause.GetType().FullName ?? cause.GetType().Name);
                tags.Add("redis.client.errors.category", cause switch
                {
                    AuthenticationException => "tls",
                    RespireAuthenticationException => "auth",
                    RespireServerException { Code: "NOAUTH" or "NOPERM" or "WRONGPASS" } => "auth",
                    RespireServerException => "server",
                    SocketException or IOException or RespireConnectionException => "network",
                    _ => "other",
                });
            }
            RespireTelemetry.ConnectionsClosed.Add(1, tags);
        }
        catch { /* Listener failures must not replace transport or disposal outcomes. */ }
    }

    internal sealed class Pool
    {
        private readonly Lock _gate = new();
        private readonly HashSet<State> _connections = [];
        private int _nextPruneCount = 64;
        internal readonly bool PubSub;
        internal readonly KeyValuePair<string, object?>[] Tags;
        internal readonly KeyValuePair<string, object?>[] IdleTags;
        internal readonly KeyValuePair<string, object?>[] UsedTags;

        internal Pool(string name, bool pubsub)
        {
            PubSub = pubsub;
            Tags = [RespireTelemetry.ConnectionLibraryTag, RespireTelemetry.ConnectionSystemTag,
                new("db.client.connection.pool.name", name)];
            IdleTags = [.. Tags, new("db.client.connection.state", "idle"), new("redis.client.connection.pubsub", pubsub)];
            UsedTags = [.. Tags, new("db.client.connection.state", "used"), new("redis.client.connection.pubsub", pubsub)];
        }

        internal void Add(State state)
        {
            lock (_gate)
            {
                _connections.Add(state);
                // Geometric growth amortizes scans while bounding abandoned records even
                // when no listener ever observes the pool. Live targets remain untouched.
                if (_connections.Count >= _nextPruneCount) PruneCollected();
            }
        }

        internal void Remove(State state)
        {
            lock (_gate) _connections.Remove(state);
        }

        internal State[] SnapshotForObservation()
        {
            lock (_gate)
            {
                // Churn changes membership in O(1); only observations copy the set. Reclaim
                // abandoned weak targets without inventing physical-close events.
                PruneCollected();
                return [.. _connections];
            }
        }

        private void PruneCollected()
        {
            _connections.RemoveWhere(static state => state.IsCollected);
            _nextPruneCount = (int)Math.Min(int.MaxValue, Math.Max(64L, 2L * _connections.Count));
        }

        internal (long Idle, long Used, long Pending) Read()
        {
            long idle = 0, used = 0, pending = 0;
            foreach (var state in SnapshotForObservation())
            {
                if (!state.TryRead(out var busy, out var count)) continue;
                if (busy) used++;
                else idle++;
                pending += count;
            }
            return (idle, used, pending);
        }

        internal long ReadRelaxedTimeouts()
        {
            long count = 0;
            foreach (var state in SnapshotForObservation())
                if (state.HasRelaxedTimeout) count++;
            return count;
        }
    }

    // Retain zero-valued series after disposal, but bound endpoint labels for the process lifetime.
    internal sealed class Registry
    {
        private const int MaximumPoolNames = 64;
        private const int MaximumPoolNameLength = 256;
        private readonly Lock _gate = new();
        private readonly Dictionary<(string Host, int Port, int Database, string Purpose), Pool> _pools = [];
        private Pool[] _snapshot = [];
        private Pool? _ordinaryOverflow;
        private Pool? _pubsubOverflow;
        internal Pool[] Snapshot => Volatile.Read(ref _snapshot);

        internal Pool ForPool(string host, int port, int database, string purpose)
        {
            _ = RespireTelemetry.Meter;
            lock (_gate)
            {
                var key = (host, port, database, purpose);
                if (_pools.TryGetValue(key, out var existing)) return existing;
                // Format tags only for a new identity that can fit the lifetime budget.
                var name = host.Length <= MaximumPoolNameLength && _pools.Count < MaximumPoolNames
                    ? host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/"
                        + database.ToString(CultureInfo.InvariantCulture) + "/" + purpose
                    : null;
                var pubsub = purpose == "pubsub";
                Pool pool;
                if (name is null || name.Length > MaximumPoolNameLength)
                {
                    ref var overflow = ref (pubsub ? ref _pubsubOverflow : ref _ordinaryOverflow);
                    if (overflow is not null) return overflow;
                    pool = overflow = new Pool(pubsub ? "overflow/pubsub" : "overflow/shared", pubsub);
                }
                else
                {
                    pool = new Pool(name, pubsub);
                    _pools.Add(key, pool);
                }
                Volatile.Write(ref _snapshot, [.. _snapshot, pool]);
                return pool;
            }
        }
    }
}
