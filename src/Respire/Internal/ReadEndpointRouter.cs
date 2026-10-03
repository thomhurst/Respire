using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter(ClientCore core) : IAsyncDisposable
{
    // Longest wait before a removed replica starts draining. Entry._closed is only set under the
    // entry gate, so a read on the lock-free fast path can take a connection just before removal
    // and write its command just after. The grace period lets that write land before the
    // connection stops accepting commands; the drain then waits for its reply.
    private static readonly TimeSpan s_retirementGrace = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<RespireEndpoint, Entry> _entries = new(RespireEndpointComparer.Instance);
    // Entries removed from the topology drain before closing so reads already using them can finish.
    private readonly ConcurrentDictionary<Entry, byte> _retiring = new();
    private readonly SemaphoreSlim _sentinelRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RespireEndpoint[] _replicas = string.IsNullOrWhiteSpace(core.Options.SentinelPrimaryName)
        ? Order(core.Options.ReplicaEndpoints)
        : [];
    private int _nextReplica;
    private int _disposed;
    private int _backgroundRefresh;
    // Consecutive failed Sentinel discoveries; grows the retry delay during an outage.
    private int _refreshFailures;
    // Stopwatch timestamp of the last discovery attempt; monotonic, so clock changes cannot stall it.
    private long _lastSentinelRefresh;

    // ReplicaRefreshInterval drives three separate jobs. They are kept as separate internal knobs so
    // a later options change can expose them individually without changing how the router works.

    /// <summary>How long a ROLE check stays valid for one physical replica connection.</summary>
    internal TimeSpan RoleRevalidationInterval { get; set; } = core.Options.ReplicaRefreshInterval;

    /// <summary>Minimum delay between Sentinel replica discoveries (before outage back-off).</summary>
    internal TimeSpan SentinelRefreshInterval { get; set; } = core.Options.ReplicaRefreshInterval;

    /// <summary>How long a replica that failed is skipped before it is tried again.</summary>
    internal TimeSpan FailedReplicaCooldown { get; set; } = core.Options.ReplicaRefreshInterval;

    /// <summary>Sets every interval that <see cref="RespireOptions.ReplicaRefreshInterval"/> configures.</summary>
    internal TimeSpan RefreshInterval
    {
        set
        {
            RoleRevalidationInterval = value;
            SentinelRefreshInterval = value;
            FailedReplicaCooldown = value;
        }
    }

    /// <summary>Cursor affinity for scans and raw cursor commands.</summary>
    internal ReadCursorAffinity Cursors { get; } = new();

    // Upper bound for the Sentinel retry delay during a discovery outage.
    private static readonly TimeSpan s_maxSentinelRetryDelay = TimeSpan.FromSeconds(30);

    // Sorted so replica order survives Sentinel reply reordering.
    internal static RespireEndpoint[] Order(IEnumerable<RespireEndpoint> endpoints)
        => endpoints.Distinct(RespireEndpointComparer.Instance)
            .OrderBy(static endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static endpoint => endpoint.Port)
            .ToArray();

    /// <summary>Endpoints with open replica connections, for terminal state publication on disposal.</summary>
    internal RespireEndpoint[] GetOpenEndpoints()
        => _entries.Values.Concat(_retiring.Keys)
            .Where(static entry => entry.IsOpen)
            .Select(static entry => entry.Endpoint)
            .Distinct(RespireEndpointComparer.Instance)
            .ToArray();

    internal bool IsConnected => _entries.Values.Any(static entry => entry.IsConnected);

    private void SetEndpoints(IEnumerable<RespireEndpoint> replicas)
    {
        // SENTINEL REPLICAS never lists the current primary, and ROLE validation rejects a node
        // that is not a replica. Filtering by a cached primary endpoint could wrongly drop a former
        // primary that rejoined as a replica while the cached generation is stale.
        var endpoints = Order(replicas);
        // Full fence: the sweep below must not read _entries before the new topology is visible.
        // A reader that inserts an entry the sweep misses then sees this array on its recheck.
        Interlocked.Exchange(ref _replicas, endpoints);
        var retained = endpoints.ToHashSet(RespireEndpointComparer.Instance);
        foreach (var pair in _entries)
        {
            if (retained.Contains(pair.Key) || !_entries.TryRemove(pair)) continue;
            _retiring.TryAdd(pair.Value, 0);
            _ = RetireAsync(pair.Value);
        }
    }

    private static bool ContainsEndpoint(RespireEndpoint[] endpoints, RespireEndpoint endpoint)
    {
        foreach (var candidate in endpoints)
            if (RespireEndpointComparer.Instance.Equals(candidate, endpoint)) return true;
        return false;
    }

    private static bool SameEndpoints(RespireEndpoint[] left, RespireEndpoint[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (!RespireEndpointComparer.Instance.Equals(left[i], right[i])) return false;
        return true;
    }

    private async Task RetireAsync(Entry entry)
    {
        try
        {
            var timeout = core.Options.CommandTimeout;
            var grace = timeout is { } limit && limit < s_retirementGrace ? limit : s_retirementGrace;
            await Task.Delay(grace, _lifetime.Token).ConfigureAwait(false);
            // Drain every accepted reply. A streamed read may pause indefinitely while the
            // consumer remains responsible for disposing it or cancelling its lifetime token.
            await entry.RetireAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            try { core.Logger?.LogDebug(error, "Draining a removed read replica at {Endpoint} failed", entry.Endpoint); }
            catch (Exception) { }
        }
        // Router disposal owns entries that are still retiring.
        if (!_retiring.TryRemove(entry, out _)) return;
        try { await entry.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            try { core.Logger?.LogDebug(error, "Closing a removed read replica failed"); }
            catch (Exception) { }
        }
    }

    /// <summary>Selects a connection for a read under <paramref name="readFrom"/>.</summary>
    internal async ValueTask<RespireConnection> GetConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            var selection = await SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
            return selection.Connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Replica validation links this token with router lifetime. Preserve the caller's
            // identity at the acquisition boundary instead of leaking that internal token.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Selects a read endpoint, then rents a separate connection for a blocking read.</summary>
    internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection, bool IsReplica)> RentDedicatedConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken, string? preferredZone, bool? replicaOnly = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var selection = replicaOnly switch
            {
                true => await GetReplicaAsync(cancellationToken,
                    ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? RespireReadFrom.AzAffinity : RespireReadFrom.Replica).ConfigureAwait(false),
                false => default,
                _ => await SelectAsync(readFrom, cancellationToken).ConfigureAwait(false),
            };
            if (selection.Replica is not { } replica)
            {
                try
                {
                    var pool = await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
                    var lease = await core.RentDedicatedConnectionAsync(pool, cancellationToken, preferredZone: preferredZone).ConfigureAwait(false);
                    return (lease.Pool, lease.Connection, false);
                }
                catch (Exception error) when (replicaOnly is null && IsReadCandidateFailure(error, cancellationToken)
                    && (readFrom is RespireReadFrom.PrimaryPreferred or RespireReadFrom.Nearest
                        || ReadFallbackPolicy.UsesAvailabilityZone(readFrom)))
                {
                    if (readFrom == RespireReadFrom.Nearest)
                    {
                        if (attempt >= ClusterRouter.RedirectLimit) throw;
                        // Its shared socket can remain healthy while a dedicated handshake fails.
                        // Cool down this owner and rank the remaining candidates by latency again.
                        NearestLatency!.ConnectionFailed(selection.Primary!);
                        continue;
                    }
                    // The shared primary can be healthy while its dedicated handshake fails.
                    // Exclude that primary from the next selection instead of probing it again.
                    try
                    {
                        return await RentDedicatedConnectionAsync(readFrom, cancellationToken, preferredZone, replicaOnly: true)
                            .ConfigureAwait(false);
                    }
                    catch (Exception fallback) when (IsReadCandidateFailure(fallback, cancellationToken))
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                        throw;
                    }
                }
            }
            try
            {
                var lease = await replica.RentDedicatedConnectionAsync(cancellationToken, preferredZone).ConfigureAwait(false);
                return (lease.Pool, lease.Connection, true);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                replica.MarkFailed();
                if (attempt >= ClusterRouter.RedirectLimit) throw;
                // No application command was accepted. Reselect after a failed dedicated
                // handshake, failed ROLE check, or removal of this replica during acquisition.
            }
        }
    }

    // Before admission, every read policy can try its remaining eligible candidates after
    // a private transport deadline. Actual caller cancellation remains terminal.
    internal static bool IsReadCandidateFailure(Exception error, CancellationToken cancellationToken)
        => IsUnavailable(error, cancellationToken)
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Selects a connection for one page of a cursor read. With an <paramref name="affinity"/>, the
    /// first page selects normally and later pages return to the same server, or fail when it is no
    /// longer usable because its cursor cannot continue elsewhere. Without one, raw cursor commands
    /// share a pin per read policy. When that pin's server leaves the topology or fails, a fresh
    /// cursor reselects and a <paramref name="isContinuation"/> cursor fails.
    /// </summary>
    internal ValueTask<RespireConnection> GetCursorConnectionAsync(
        RespireReadFrom readFrom, ReadAffinity? affinity, bool isContinuation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Cursors.GetConnectionAsync(this, readFrom, affinity, isContinuation, cancellationToken);
    }

    internal ClientCore Core => core;

    internal bool IsCurrent(Entry entry)
        => _entries.TryGetValue(entry.Endpoint, out var current) && ReferenceEquals(current, entry);

    internal async ValueTask<Selection> SelectAsync(RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        switch (readFrom)
        {
            case RespireReadFrom.Nearest:
                return await GetNearestAsync(cancellationToken).ConfigureAwait(false);
            case RespireReadFrom.PrimaryPreferred:
                try { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, cancellationToken))
                { return await GetReplicaAsync(cancellationToken).ConfigureAwait(false); }
            case RespireReadFrom.Replica:
                return await GetReplicaAsync(cancellationToken).ConfigureAwait(false);
            case RespireReadFrom.ReplicaPreferred:
            case RespireReadFrom.AzAffinity:
            case RespireReadFrom.AzAffinityReplicasAndPrimary:
                try { return await GetReplicaAsync(cancellationToken, readFrom).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, cancellationToken))
                {
                    return await GetPrimaryAsync(cancellationToken,
                        ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null).ConfigureAwait(false);
                }
            default:
                return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Fall back only for availability failures; programming errors and disposal propagate.
    internal static bool IsUnavailable(Exception error, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && error is RespireConnectionException or RespireTimeoutException or IOException or SocketException;

    private async ValueTask<Selection> GetPrimaryAsync(CancellationToken cancellationToken, string? preferredZone = null)
    {
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var multiplexer = core.Multiplexer;
        return new Selection(preferredZone is null ? multiplexer.GetConnection() : multiplexer.GetConnectionForZone(preferredZone),
            null, multiplexer);
    }

    private async ValueTask<Selection> GetReplicaAsync(CancellationToken cancellationToken,
        RespireReadFrom readFrom = RespireReadFrom.Replica)
    {
        var endpoints = await GetReplicaEndpointsAsync(cancellationToken).ConfigureAwait(false);
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        try
        {
            return await GetReplicaFromEndpointsAsync(endpoints, cancellationToken, readFrom).ConfigureAwait(false);
        }
        catch (RespireConnectionException) when (core.Sentinel is { } refreshSentinel)
        {
            // A stale replica can fail while Sentinel already knows its replacement. Refresh once
            // before reporting failure or falling back to the primary.
            var refreshed = Volatile.Read(ref _replicas);
            if (SameEndpoints(refreshed, endpoints) && IsSentinelRefreshDue())
            {
                await RefreshSentinelReplicasAsync(refreshSentinel, cancellationToken).ConfigureAwait(false);
                refreshed = Volatile.Read(ref _replicas);
            }
            if (SameEndpoints(refreshed, endpoints)) throw;
            return await GetReplicaFromEndpointsAsync(refreshed, cancellationToken, readFrom).ConfigureAwait(false);
        }
    }

    internal async ValueTask<Selection> GetReplicaFromEndpointsAsync(
        RespireEndpoint[] endpoints, CancellationToken cancellationToken, RespireReadFrom readFrom = RespireReadFrom.Replica)
    {
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        var start = (uint)Interlocked.Increment(ref _nextReplica);
        Exception? lastError = null;
        var attempted = false;
        // A replica whose replication link is down still serves reads when nothing better exists
        // (the server's replica-serve-stale-data setting decides), but a linked replica wins.
        var candidates = new ReadFallbackPolicy.ReplicaCandidates<Selection>();
        try
        {
            for (var offset = 0; offset < endpoints.Length; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoint = endpoints[(int)((start + (uint)offset) % (uint)endpoints.Length)];
                var entry = await GetCurrentReplicaEntryAsync(endpoint).ConfigureAwait(false);
                if (entry is null) continue;
                // A failed replica remains in cooldown so it cannot add a timeout to every read.
                if (entry.IsCoolingDown) continue;
                attempted = true;
                try
                {
                    var selection = new Selection(await entry.GetConnectionAsync(cancellationToken,
                        ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null).ConfigureAwait(false), entry, null);
                    var local = ReadFallbackPolicy.IsSameZone(selection.Connection, core.Options.ClientAvailabilityZone);
                    if (candidates.Offer(selection, local, !entry.IsReplicationLinkDown, readFrom)) return selection;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ObjectDisposedException)
                {
                    lastError = error;
                    TryRecordFailure(entry, error, "Read replica unavailable at {Endpoint}");
                }
            }

            if (ReadFallbackPolicy.ShouldProbeLocalPrimary(readFrom, core.Multiplexer, core.Options.ClientAvailabilityZone))
            {
                try
                {
                    var primary = await GetPrimaryAsync(cancellationToken, core.Options.ClientAvailabilityZone).ConfigureAwait(false);
                    if (ReadFallbackPolicy.IsSameZone(primary.Connection, core.Options.ClientAvailabilityZone)) return primary;
                }
                catch (Exception error) when (IsUnavailable(error, cancellationToken)) { lastError = error; }
            }
            while (candidates.TryTake(out var fallback))
                if (fallback.Connection.IsAcceptingCommands
                    && fallback.Replica is { } fallbackEntry && IsCurrent(fallbackEntry)) return fallback;
            throw attempted
                ? new RespireConnectionException("No healthy, role-validated read replicas are available.",
                    lastError ?? new InvalidOperationException("No replica connection attempt was completed."))
                : new RespireConnectionException(
                    "Every read replica failed recently and is skipped until its ReplicaRefreshInterval cooldown ends.");
        }
        finally { candidates.Dispose(); }
    }

    private void TryRecordFailure(Entry entry, Exception error, string message)
    {
        entry.MarkFailed();
        try { core.Logger?.LogDebug(error, message, entry.Endpoint); }
        catch (Exception) { }
    }

    private bool IsSentinelRefreshDue()
    {
        var last = Volatile.Read(ref _lastSentinelRefresh);
        return last == 0
            || Stopwatch.GetElapsedTime(last) >= SentinelRetryDelay(SentinelRefreshInterval, Volatile.Read(ref _refreshFailures));
    }

    /// <summary>
    /// Delay before the next Sentinel discovery: one refresh interval normally, doubling for each
    /// consecutive failure up to 30 seconds (or the interval, when that is longer).
    /// </summary>
    internal static TimeSpan SentinelRetryDelay(TimeSpan interval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 1 || interval <= TimeSpan.Zero) return interval;
        var cap = interval > s_maxSentinelRetryDelay ? interval : s_maxSentinelRetryDelay;
        var shift = Math.Min(consecutiveFailures - 1, 16);
        var ticks = interval.Ticks < cap.Ticks >> shift ? interval.Ticks << shift : cap.Ticks;
        return TimeSpan.FromTicks(ticks);
    }

    private async Task RefreshSentinelReplicasInBackgroundAsync(SentinelRouter sentinel)
    {
        try { await RefreshSentinelReplicasAsync(sentinel, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error)
        {
            try { core.Logger?.LogDebug(error, "Background Sentinel replica refresh failed"); }
            catch (Exception) { }
        }
        finally { Volatile.Write(ref _backgroundRefresh, 0); }
    }

    private async ValueTask RefreshSentinelReplicasAsync(
        SentinelRouter sentinel, CancellationToken cancellationToken, bool force = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _sentinelRefreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!force && !IsSentinelRefreshDue()) return;
            RespireEndpoint[] endpoints;
            try { endpoints = await sentinel.DiscoverReplicaEndpointsAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Warn once per outage; later failures in the same outage stay at debug level and
                // back off, so a long outage does not query Sentinel every interval.
                var failures = Interlocked.Increment(ref _refreshFailures);
                try
                {
                    if (failures == 1)
                        core.Logger?.LogWarning(error,
                            "Sentinel replica discovery failed; serving the last known {Count} replica endpoint(s) until Sentinel answers",
                            Volatile.Read(ref _replicas).Length);
                    else
                        core.Logger?.LogDebug(error, "Sentinel replica refresh failed; retaining current endpoints");
                }
                catch (Exception) { }
                Volatile.Write(ref _lastSentinelRefresh, Stopwatch.GetTimestamp());
                return;
            }
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _refreshFailures, 0) != 0)
            {
                try { core.Logger?.LogInformation("Sentinel replica discovery recovered with {Count} replica endpoint(s)", endpoints.Length); }
                catch (Exception) { }
            }
            SetEndpoints(endpoints);
            Volatile.Write(ref _lastSentinelRefresh, Stopwatch.GetTimestamp());
        }
        finally { _sentinelRefreshGate.Release(); }
    }

    /// <summary>Refreshes the Sentinel replica set now, ignoring the refresh throttle.</summary>
    internal async ValueTask RefreshNowAsync(CancellationToken cancellationToken)
    {
        if (core.Sentinel is not { } sentinel) return;
        await RefreshSentinelReplicasAsync(sentinel, cancellationToken, force: true).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (Volatile.Read(ref NearestLatency) is { } latency) await latency.DisposeAsync().ConfigureAwait(false);
        var entries = _entries.Values.Concat(_retiring.Keys).Distinct().ToArray();
        _entries.Clear();
        _retiring.Clear();
        Cursors.Clear();
        await CleanupTasks.WhenAllAsync(entries.Select(entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    internal readonly record struct Selection(
        RespireConnection Connection, Entry? Replica, RespireConnectionMultiplexer? Primary);

    internal sealed class Entry(RespireEndpoint endpoint, ClientCore owner, ReadEndpointRouter router) : IAsyncDisposable
    {
        // Never disposed: a waiter racing with disposal must observe _closed, not a disposed gate.
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ReplicaHealth<RespireConnection> _health = new();
        private RespireConnectionMultiplexer? _multiplexer;
        private DedicatedConnectionPool? _dedicatedPool;
        private Action<RespireConnectionStateChange>? _stateChanged;
        private Action<int, RespireConnectionStateChange>? _slotStateChanged;
        // Set when the entry stops serving reads (removal or disposal).
        private volatile bool _closed;
        private bool _disposed;

        internal RespireEndpoint Endpoint => endpoint;
        internal bool IsOpen => Volatile.Read(ref _multiplexer) is not null;
        internal bool IsConnected
        {
            get
            {
                var multiplexer = Volatile.Read(ref _multiplexer);
                return !_closed && !IsCoolingDown && multiplexer?.HasConnection(connection =>
                    _health.WasValidated(connection)) == true;
            }
        }

        internal bool IsCoolingDown => _health.IsCoolingDown(router.FailedReplicaCooldown);

        internal bool IsRoleEligible(RespireConnection connection)
            => !_closed && !IsCoolingDown && _health.WasValidated(connection);

        /// <summary>True when the last ROLE check found the replica's link to its primary down.</summary>
        internal bool IsReplicationLinkDown => _health.IsReplicationLinkDown;

        internal void MarkFailed() => _health.MarkFailed();

        internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
            CancellationToken cancellationToken, string? preferredZone)
        {
            DedicatedConnectionPool pool;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(owner.Disposed, owner);
                if (_closed || !router.IsCurrent(this))
                    throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                // Standalone replicas use ROLE validation, not Cluster's READONLY handshake.
                pool = _dedicatedPool ??= new(endpoint.Host, endpoint.Port, owner.Options.ToConnectionOptions(), owner.Logger);
            }
            finally { _gate.Release(); }

            RespireConnection? connection = null;
            try
            {
                // Pool disposal aborts acquisition and ROLE I/O. Do not allocate a linked token
                // on every warm rental merely to duplicate that lifetime boundary.
                connection = await pool.RentAsync(cancellationToken, preferredZone: preferredZone).ConfigureAwait(false);
                if (_health.Check(connection, router.RoleRevalidationInterval) != ReplicaValidation.Fresh)
                {
                    var checkedAt = Stopwatch.GetTimestamp();
                    using var role = await connection.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
                    if (!_health.Record(connection, checkedAt, in role))
                        throw new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                }
                if (_closed || !router.IsCurrent(this))
                    throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                return (pool, connection);
            }
            catch (Exception error)
            {
                if (connection is not null) await pool.DiscardAsync(connection).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                if (router._lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(ReadEndpointRouter));
                if (pool.IsStopping && error is ObjectDisposedException or OperationCanceledException)
                    throw new RespireConnectionException($"Read replica {endpoint} retired during dedicated acquisition.", error);
                throw;
            }
        }

        /// <summary>Acquires a current connection after validating its replication role.</summary>
        internal async ValueTask<RespireConnection> GetConnectionAsync(CancellationToken cancellationToken, string? preferredZone = null)
        {
            // Fast path: a recently validated connection needs no lock and no extra round trip.
            RespireConnection? selected = null;
            RespireConnectionMultiplexer? selectedFrom = null;
            var interval = router.RoleRevalidationInterval;
            if (!_closed && Volatile.Read(ref _multiplexer) is { } current)
            {
                selected = preferredZone is null ? current.GetConnection() : current.GetConnectionForZone(preferredZone);
                selectedFrom = current;
                if (selected.IsAcceptingCommands && _health.Check(selected, interval) == ReplicaValidation.Fresh)
                    return selected;
            }

            // Single-flight revalidation: while another caller revalidates, keep serving a
            // connection whose previous check is still within a second interval.
            bool entered;
            if (selected is not null && selected.IsAcceptingCommands
                && _health.Check(selected, interval) != ReplicaValidation.Required)
            {
                entered = _gate.Wait(0);
                if (!entered) return selected;
            }
            else
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
            }

            // Router disposal cancels a connect or ROLE check in progress, so disposal never
            // waits behind a dead replica's connect timeout for this entry's gate.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, router._lifetime.Token);
            try
            {
                if (_closed) throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                ObjectDisposedException.ThrowIf(owner.Disposed, this);
                if (_multiplexer is null)
                {
                    var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
                        endpoint.Host, endpoint.Port, owner.Options.Connections,
                        owner.Options.ToConnectionOptions(), owner.Logger, linked.Token).ConfigureAwait(false);
                    try
                    {
                        Action<RespireConnectionStateChange> stateChanged = change =>
                        {
                            if (change.ConnectionSlot is null) owner.NotifyRecoveryStateChanged(change);
                        };
                        Action<int, RespireConnectionStateChange> slotStateChanged =
                            (slot, change) => owner.NotifyReadReplicaStateChanged(multiplexer, slot, change);
                        multiplexer.StateChanged += stateChanged;
                        multiplexer.SlotStateChanged += slotStateChanged;
                        _stateChanged = stateChanged;
                        _slotStateChanged = slotStateChanged;
                        Volatile.Write(ref _multiplexer, multiplexer);
                        owner.NotifyRecoveryStateChanged(new RespireConnectionStateChange(
                            endpoint, RespireConnectionState.Connected, null));
                    }
                    catch
                    {
                        DetachHandlers(multiplexer);
                        Volatile.Write(ref _multiplexer, null);
                        await multiplexer.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }

                if (!ReferenceEquals(selectedFrom, _multiplexer) || selected is null || !selected.IsAcceptingCommands)
                    selected = preferredZone is null ? _multiplexer.GetConnection() : _multiplexer.GetConnectionForZone(preferredZone);
                if (selected.IsAcceptingCommands && _health.Check(selected, interval) == ReplicaValidation.Fresh)
                    return selected;
                var checkedAt = Stopwatch.GetTimestamp();
                using var role = await selected.SendAsync(new Cmd(Verbs.Role), linked.Token).ConfigureAwait(false);
                if (!_health.Record(selected, checkedAt, in role))
                    throw new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                return selected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && router._lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ReadEndpointRouter));
            }
            finally { if (entered) _gate.Release(); }
        }

        /// <summary>Stops new reads, then drains accepted work before the entry is disposed.</summary>
        internal async Task RetireAsync(CancellationToken cancellationToken)
        {
            RespireConnectionMultiplexer? multiplexer;
            DedicatedConnectionPool? pool;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _closed = true;
                multiplexer = Volatile.Read(ref _multiplexer);
                pool = _dedicatedPool;
                // A removed replica's drain is not an outage: stop forwarding its state changes
                // and forget any slot health it reported.
                if (multiplexer is not null) DetachHandlers(multiplexer);
            }
            finally { _gate.Release(); }
            if (multiplexer is not null) owner.NotifyReadReplicaRetired(multiplexer);
            await CleanupTasks.WhenAllAsync([multiplexer?.RetireAsync() ?? Task.CompletedTask,
                pool?.RetireAsync().AsTask() ?? Task.CompletedTask]).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void DetachHandlers(RespireConnectionMultiplexer multiplexer)
        {
            if (_stateChanged is { } handler) multiplexer.StateChanged -= handler;
            _stateChanged = null;
            if (_slotStateChanged is { } slotHandler) multiplexer.SlotStateChanged -= slotHandler;
            _slotStateChanged = null;
        }

        public async ValueTask DisposeAsync()
        {
            RespireConnectionMultiplexer? multiplexer;
            DedicatedConnectionPool? pool;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                _closed = true;
                multiplexer = Volatile.Read(ref _multiplexer);
                pool = _dedicatedPool;
                Volatile.Write(ref _multiplexer, null);
            }
            finally { _gate.Release(); }
            if (multiplexer is not null) DetachHandlers(multiplexer);
            try
            {
                await CleanupTasks.WhenAllAsync([multiplexer?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                    pool?.DisposeAsync().AsTask() ?? Task.CompletedTask]).ConfigureAwait(false);
            }
            finally
            {
                // A later replica at the same address must not inherit this node's slot health.
                if (multiplexer is not null) owner.NotifyReadReplicaRetired(multiplexer);
            }
        }
    }
}
