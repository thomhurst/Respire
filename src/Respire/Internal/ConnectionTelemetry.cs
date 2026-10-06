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
        var pubsub = options.SubscriptionConfirmationHandler is not null;
        var purpose = options switch
        {
            { SubscriptionConfirmationHandler: not null } => "pubsub",
            { IsDedicatedConnection: true } => "dedicated",
            _ => "shared",
        };
        var name = host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/"
            + options.Database.ToString(CultureInfo.InvariantCulture) + "/" + purpose;
        return Pools.ForPool(name, pubsub);
    }

    internal static void ClosedBeforeHandshake(string host, int port, RespireConnectionOptions options, Exception error)
    {
        if (RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionsClosed))
            RecordClosed(ForConnection(host, port, options), "error", error);
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
            try { RespireTelemetry.ConnectionCreateTime.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, _pool.Tags); }
            catch { /* Listener failures must not prevent connection publication. */ }
        }

        internal void SetRented(bool rented) => Volatile.Write(ref _rented, rented ? 1 : 0);
        internal void RequestClose(string reason) => Volatile.Write(ref _requestedCloseReason, reason);

        internal void Waited(long started)
        {
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionWaitTime)) return;
            try { RespireTelemetry.ConnectionWaitTime.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, _pool.Tags); }
            catch { /* A listener cannot fail an acquired lease. */ }
        }

        internal void HandedOff()
        {
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionBasic, RespireTelemetry.ConnectionHandoffs)) return;
            try { RespireTelemetry.ConnectionHandoffs.Add(1, _pool.Tags); }
            catch { /* A listener cannot undo a published handoff. */ }
        }

        internal bool HasRelaxedTimeout => Volatile.Read(ref _ready) != 0 && Volatile.Read(ref _closed) == 0
            && _connection.TryGetTarget(out var connection) && connection.IsConnected && connection.HasRelaxedTimeoutAllowance;

        internal void Closed(Exception? error, bool peerClosed)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _pool.Remove(this);
            if (!RespireTelemetry.IsMetricEnabled(RespireMetricGroups.ConnectionAdvanced, RespireTelemetry.ConnectionsClosed)) return;
            var reason = "error";
            if (peerClosed) reason = "server_close";
            else if (error is null or RespireConnectionRetiredException)
                reason = Volatile.Read(ref _requestedCloseReason) ?? "application_close";
            RecordClosed(_pool, reason, reason == "error" ? error : null);
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
        private State[] _connections = [];
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
            lock (_gate) Volatile.Write(ref _connections, [.. _connections, state]);
        }

        internal void Remove(State state)
        {
            lock (_gate) Volatile.Write(ref _connections, _connections.Where(item => !ReferenceEquals(item, state)).ToArray());
        }

        internal (long Idle, long Used, long Pending) Read()
        {
            long idle = 0, used = 0, pending = 0;
            foreach (var state in Volatile.Read(ref _connections))
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
            foreach (var state in Volatile.Read(ref _connections))
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
        private readonly Dictionary<(string Name, bool PubSub), Pool> _pools = [];
        private Pool[] _snapshot = [];
        private Pool? _ordinaryOverflow;
        private Pool? _pubsubOverflow;
        internal Pool[] Snapshot => Volatile.Read(ref _snapshot);

        internal Pool ForPool(string name, bool pubsub)
        {
            _ = RespireTelemetry.Meter;
            lock (_gate)
            {
                if (_pools.TryGetValue((name, pubsub), out var existing)) return existing;
                Pool pool;
                if (name.Length > MaximumPoolNameLength || _pools.Count >= MaximumPoolNames)
                {
                    ref var overflow = ref (pubsub ? ref _pubsubOverflow : ref _ordinaryOverflow);
                    if (overflow is not null) return overflow;
                    pool = overflow = new Pool(pubsub ? "overflow/pubsub" : "overflow/shared", pubsub);
                }
                else
                {
                    pool = new Pool(name, pubsub);
                    _pools.Add((name, pubsub), pool);
                }
                Volatile.Write(ref _snapshot, [.. _snapshot, pool]);
                return pool;
            }
        }
    }
}
