using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Remembers which server issued a cursor, so later pages of the same enumeration reach it.
/// Each typed scan enumeration owns one; raw cursor commands share one per read policy.
/// </summary>
internal sealed class ReadAffinity
{
    internal ReadEndpointRouter.Entry? Replica;
    internal RespireConnectionMultiplexer? Primary;

    internal bool IsPinned => Replica is not null || Primary is not null;
}

internal sealed class ReadEndpointRouter(ClientCore core) : IAsyncDisposable
{
    // Longest wait before a removed replica starts draining. Covers reads that borrowed a
    // connection just before removal but have not yet written their command.
    private static readonly TimeSpan s_retirementGrace = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<RespireEndpoint, Entry> _entries = new();
    // Entries removed from the topology drain before closing so reads already using them can finish.
    private readonly ConcurrentDictionary<Entry, byte> _retiring = new();
    private readonly SemaphoreSlim _sentinelRefreshGate = new(1, 1);
    // Taken only to publish a new shared cursor pin; reads through an existing pin never wait on it.
    private readonly SemaphoreSlim _sharedCursorGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<RespireReadFrom, ReadAffinity> _sharedCursors = new();
    private RespireEndpoint[] _replicas = Order(core.Options.ReplicaEndpoints);
    private int _nextReplica;
    private int _disposed;
    private int _backgroundRefresh;
    private int _refreshFailing;
    private long _lastSentinelRefreshTicks;

    /// <summary>
    /// Bounds topology staleness: how long a ROLE check stays valid for one physical connection,
    /// how often Sentinel replica discovery can run, and how long a failed replica is skipped.
    /// </summary>
    internal TimeSpan RefreshInterval { get; set; } = core.Options.ReplicaRefreshInterval;

    // Sorted so replica order survives Sentinel reply reordering.
    private static RespireEndpoint[] Order(IEnumerable<RespireEndpoint> endpoints)
        => endpoints.Distinct()
            .OrderBy(static endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static endpoint => endpoint.Port)
            .ToArray();

    /// <summary>Endpoints with open replica connections, for terminal state publication on disposal.</summary>
    internal RespireEndpoint[] GetOpenEndpoints()
        => _entries.Values.Concat(_retiring.Keys)
            .Where(static entry => entry.IsOpen)
            .Select(static entry => entry.Endpoint)
            .Distinct()
            .ToArray();

    private void SetEndpoints(IEnumerable<RespireEndpoint> replicas)
    {
        // SENTINEL REPLICAS never lists the current primary, and ROLE validation rejects a node
        // that is not a replica. Filtering by a cached primary endpoint could wrongly drop a former
        // primary that rejoined as a replica while the cached generation is stale.
        var endpoints = Order(replicas);
        Volatile.Write(ref _replicas, endpoints);
        var retained = endpoints.ToHashSet();
        foreach (var pair in _entries)
        {
            if (retained.Contains(pair.Key) || !_entries.TryRemove(pair)) continue;
            _retiring.TryAdd(pair.Value, 0);
            _ = RetireAsync(pair.Value);
        }
    }

    private async Task RetireAsync(Entry entry)
    {
        try
        {
            var timeout = core.Options.CommandTimeout;
            var grace = timeout is { } limit && limit < s_retirementGrace ? limit : s_retirementGrace;
            await Task.Delay(grace, _lifetime.Token).ConfigureAwait(false);
            // Drain accepted work. A configured command timeout bounds the drain; without one,
            // a long-running read is never cut off by retirement.
            await entry.RetireAsync(timeout, _lifetime.Token).ConfigureAwait(false);
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
        var selection = await SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
        return selection.Connection;
    }

    /// <summary>
    /// Selects a connection for one page of a cursor read. With an <paramref name="affinity"/>, the
    /// first page selects normally and later pages return to the same server, or fail when it is no
    /// longer usable because its cursor cannot continue elsewhere. Without one, raw cursor commands
    /// share a pin per read policy that is dropped when its server leaves the topology or fails.
    /// </summary>
    internal async ValueTask<RespireConnection> GetCursorConnectionAsync(
        RespireReadFrom readFrom, ReadAffinity? affinity, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (affinity is not null)
        {
            if (affinity.IsPinned) return await GetPinnedConnectionAsync(affinity, cancellationToken).ConfigureAwait(false);
            var first = await SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
            affinity.Replica = first.Replica;
            affinity.Primary = first.Primary;
            return first.Connection;
        }

        if (_sharedCursors.TryGetValue(readFrom, out var shared))
        {
            if (await IsPinCurrentAsync(shared, cancellationToken).ConfigureAwait(false))
                return await GetSharedPinnedConnectionAsync(readFrom, shared, cancellationToken).ConfigureAwait(false);
            _sharedCursors.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
        }

        await _sharedCursorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sharedCursors.TryGetValue(readFrom, out shared))
            {
                if (await IsPinCurrentAsync(shared, cancellationToken).ConfigureAwait(false))
                    return await GetSharedPinnedConnectionAsync(readFrom, shared, cancellationToken).ConfigureAwait(false);
                _sharedCursors.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
            }

            var selection = await SelectAsync(readFrom, cancellationToken).ConfigureAwait(false);
            _sharedCursors[readFrom] = new ReadAffinity { Replica = selection.Replica, Primary = selection.Primary };
            return selection.Connection;
        }
        finally { _sharedCursorGate.Release(); }
    }

    private async ValueTask<RespireConnection> GetSharedPinnedConnectionAsync(
        RespireReadFrom readFrom, ReadAffinity shared, CancellationToken cancellationToken)
    {
        try { return await GetPinnedConnectionAsync(shared, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsUnavailable(error, cancellationToken))
        {
            // The issuing server failed, so its cursors are lost. Let the next cursor read reselect.
            _sharedCursors.TryRemove(new KeyValuePair<RespireReadFrom, ReadAffinity>(readFrom, shared));
            throw;
        }
    }

    private async ValueTask<bool> IsPinCurrentAsync(ReadAffinity affinity, CancellationToken cancellationToken)
    {
        if (affinity.Replica is { } replica) return IsCurrent(replica);
        // An unreachable primary invalidates the pin so the policy can select a replica instead.
        try { await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsUnavailable(error, cancellationToken)) { return false; }
        return ReferenceEquals(core.Multiplexer, affinity.Primary);
    }

    private bool IsCurrent(Entry entry)
        => _entries.TryGetValue(entry.Endpoint, out var current) && ReferenceEquals(current, entry);

    private async ValueTask<RespireConnection> GetPinnedConnectionAsync(
        ReadAffinity affinity, CancellationToken cancellationToken)
    {
        if (affinity.Replica is { } replica)
        {
            // Never recreate an entry for a replica removed from the topology.
            if (!IsCurrent(replica))
                throw new RespireConnectionException(
                    $"Read replica {replica.Endpoint} that issued the cursor was removed from the topology.");
            return await replica.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var multiplexer = core.Multiplexer;
        if (!ReferenceEquals(multiplexer, affinity.Primary))
            throw new RespireConnectionException("The primary that issued the cursor was replaced.");
        return multiplexer.GetConnection();
    }

    private async ValueTask<Selection> SelectAsync(RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        switch (readFrom)
        {
            case RespireReadFrom.PrimaryPreferred:
                try { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, cancellationToken))
                { return await GetReplicaAsync(cancellationToken).ConfigureAwait(false); }
            case RespireReadFrom.Replica:
                return await GetReplicaAsync(cancellationToken).ConfigureAwait(false);
            case RespireReadFrom.ReplicaPreferred:
                try { return await GetReplicaAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, cancellationToken))
                { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
            default:
                return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Fall back only for availability failures; programming errors and disposal propagate.
    private static bool IsUnavailable(Exception error, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && error is RespireConnectionException or RespireTimeoutException or IOException or SocketException;

    private async ValueTask<Selection> GetPrimaryAsync(CancellationToken cancellationToken)
    {
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var multiplexer = core.Multiplexer;
        return new Selection(multiplexer.GetConnection(), null, multiplexer);
    }

    private async ValueTask<Selection> GetReplicaAsync(CancellationToken cancellationToken)
    {
        var endpoints = Volatile.Read(ref _replicas);
        if (core.Sentinel is { } sentinel && IsSentinelRefreshDue())
        {
            // Serve known replicas while refreshing in the background; wait only when none are known.
            if (endpoints.Length == 0)
            {
                await RefreshSentinelReplicasAsync(sentinel, cancellationToken).ConfigureAwait(false);
                endpoints = Volatile.Read(ref _replicas);
            }
            else if (Interlocked.CompareExchange(ref _backgroundRefresh, 1, 0) == 0)
            {
                _ = RefreshSentinelReplicasInBackgroundAsync(sentinel);
            }
        }
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        try
        {
            return await GetReplicaFromEndpointsAsync(endpoints, cancellationToken).ConfigureAwait(false);
        }
        catch (RespireConnectionException) when (core.Sentinel is { } refreshSentinel)
        {
            // A stale replica can fail while Sentinel already knows its replacement. Refresh once
            // before reporting failure or falling back to the primary.
            var refreshed = Volatile.Read(ref _replicas);
            if (refreshed.AsSpan().SequenceEqual(endpoints) && IsSentinelRefreshDue())
            {
                await RefreshSentinelReplicasAsync(refreshSentinel, cancellationToken).ConfigureAwait(false);
                refreshed = Volatile.Read(ref _replicas);
            }
            if (refreshed.AsSpan().SequenceEqual(endpoints)) throw;
            return await GetReplicaFromEndpointsAsync(refreshed, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Selection> GetReplicaFromEndpointsAsync(
        RespireEndpoint[] endpoints, CancellationToken cancellationToken)
    {
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        var start = (uint)Interlocked.Increment(ref _nextReplica);
        Exception? lastError = null;
        var attempted = false;
        // A replica whose replication link is down still serves reads when nothing better exists
        // (the server's replica-serve-stale-data setting decides), but a linked replica wins.
        Selection? unlinked = null;
        for (var offset = 0; offset < endpoints.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = endpoints[(int)((start + (uint)offset) % (uint)endpoints.Length)];
            var entry = _entries.GetOrAdd(endpoint, static (value, state) => new Entry(value, state.core, state.router),
                (core, router: this));
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Disposal may already have drained _entries; never leave a late entry open.
                if (_entries.TryRemove(new KeyValuePair<RespireEndpoint, Entry>(endpoint, entry)))
                    await entry.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ReadEndpointRouter));
            }
            // A replica that recently failed is skipped until its cooldown ends, so a dead node does
            // not add a connect timeout to every read.
            if (entry.IsCoolingDown) continue;
            attempted = true;
            try
            {
                var selection = new Selection(await entry.GetConnectionAsync(cancellationToken).ConfigureAwait(false), entry, null);
                if (!entry.IsReplicationLinkDown) return selection;
                unlinked ??= selection;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ObjectDisposedException)
            {
                lastError = error;
                entry.MarkFailed();
                try { core.Logger?.LogDebug(error, "Read replica unavailable at {Endpoint}", endpoint); }
                catch (Exception) { }
            }
        }

        if (unlinked is { } stale) return stale;
        throw attempted
            ? new RespireConnectionException("No healthy, role-validated read replicas are available.",
                lastError ?? new InvalidOperationException("No replica connection attempt was completed."))
            : new RespireConnectionException(
                "Every read replica failed recently and is skipped until its ReplicaRefreshInterval cooldown ends.");
    }

    private bool IsSentinelRefreshDue()
        => DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSentinelRefreshTicks) >= RefreshInterval.Ticks;

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
                // Warn once per outage; later failures in the same outage stay at debug level.
                try
                {
                    if (Interlocked.Exchange(ref _refreshFailing, 1) == 0)
                        core.Logger?.LogWarning(error,
                            "Sentinel replica discovery failed; serving the last known {Count} replica endpoint(s) until Sentinel answers",
                            Volatile.Read(ref _replicas).Length);
                    else
                        core.Logger?.LogDebug(error, "Sentinel replica refresh failed; retaining current endpoints");
                }
                catch (Exception) { }
                Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
                return;
            }
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _refreshFailing, 0) != 0)
            {
                try { core.Logger?.LogInformation("Sentinel replica discovery recovered with {Count} replica endpoint(s)", endpoints.Length); }
                catch (Exception) { }
            }
            SetEndpoints(endpoints);
            Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
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
        var entries = _entries.Values.Concat(_retiring.Keys).Distinct().ToArray();
        _entries.Clear();
        _retiring.Clear();
        _sharedCursors.Clear();
        await Task.WhenAll(entries.Select(entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private readonly record struct Selection(
        RespireConnection Connection, Entry? Replica, RespireConnectionMultiplexer? Primary);

    internal sealed class Entry(RespireEndpoint endpoint, ClientCore owner, ReadEndpointRouter router) : IAsyncDisposable
    {
        // Never disposed: a waiter racing with disposal must observe _closed, not a disposed gate.
        private readonly SemaphoreSlim _gate = new(1, 1);
        // When each physical connection last passed ROLE. Reconnects publish new connection objects,
        // which are validated before use; dead connections drop out with their weak keys.
        private readonly ConditionalWeakTable<RespireConnection, StrongBox<long>> _validated = new();
        private RespireConnectionMultiplexer? _multiplexer;
        private Action<RespireConnectionStateChange>? _stateChanged;
        private Action<int, RespireConnectionStateChange>? _slotStateChanged;
        // Set when the entry stops serving reads (removal or disposal).
        private volatile bool _closed;
        private bool _disposed;
        private long _failedAt;
        private volatile bool _replicationLinkDown;

        internal RespireEndpoint Endpoint => endpoint;
        internal bool IsOpen => Volatile.Read(ref _multiplexer) is not null;

        internal bool IsCoolingDown
        {
            get
            {
                var failedAt = Volatile.Read(ref _failedAt);
                return failedAt != 0 && Stopwatch.GetElapsedTime(failedAt) < router.RefreshInterval;
            }
        }

        /// <summary>True when the last ROLE check found the replica's link to its primary down.</summary>
        internal bool IsReplicationLinkDown => _replicationLinkDown;

        internal void MarkFailed() => Volatile.Write(ref _failedAt, Stopwatch.GetTimestamp());

        internal async ValueTask<RespireConnection> GetConnectionAsync(CancellationToken cancellationToken)
        {
            // Fast path: a recently validated connection needs no lock and no extra round trip.
            RespireConnection? selected = null;
            RespireConnectionMultiplexer? selectedFrom = null;
            var interval = router.RefreshInterval;
            if (!_closed && Volatile.Read(ref _multiplexer) is { } current)
            {
                selected = current.GetConnection();
                selectedFrom = current;
                if (selected.IsAcceptingCommands && IsValidatedWithin(selected, interval)) return selected;
            }

            // Single-flight revalidation: while another caller revalidates, keep serving a
            // connection whose previous check is still within a second interval.
            bool entered;
            if (selected is not null && selected.IsAcceptingCommands && IsValidatedWithin(selected, interval + interval))
            {
                entered = _gate.Wait(0);
                if (!entered) return selected;
            }
            else
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
            }

            try
            {
                if (_closed) throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                ObjectDisposedException.ThrowIf(owner.Disposed, this);
                if (_multiplexer is null)
                {
                    var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
                        endpoint.Host, endpoint.Port, owner.Options.Connections,
                        owner.Options.ToConnectionOptions(), owner.Logger, cancellationToken).ConfigureAwait(false);
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
                    selected = _multiplexer.GetConnection();
                if (selected.IsAcceptingCommands && IsValidatedWithin(selected, interval)) return selected;
                var checkedAt = Stopwatch.GetTimestamp();
                using var role = await selected.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
                if (!IsReplica(in role, out var linkUp))
                {
                    _validated.Remove(selected);
                    throw new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                }
                _replicationLinkDown = !linkUp;
                _validated.AddOrUpdate(selected, new StrongBox<long>(checkedAt));
                Volatile.Write(ref _failedAt, 0);
                return selected;
            }
            finally { if (entered) _gate.Release(); }
        }

        private bool IsValidatedWithin(RespireConnection connection, TimeSpan window)
            => _validated.TryGetValue(connection, out var checkedAt)
                && Stopwatch.GetElapsedTime(Volatile.Read(ref checkedAt.Value)) < window;

        // ROLE on a replica: ["slave" | "replica", primary-host, primary-port, link-state, offset].
        // A link state other than "connected" means the replica is connecting or syncing to its
        // primary and can serve arbitrarily stale data.
        internal static bool IsReplica(in RespValue role, out bool linkUp)
        {
            linkUp = false;
            if (role.Type != RespDataType.Array) return false;
            var fields = role.AsArray();
            if (fields.IsEmpty || fields[0].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
            if (fields[0].AsString() is not ("slave" or "replica")) return false;
            linkUp = fields.Length >= 4
                && fields[3].Type is (RespDataType.BulkString or RespDataType.SimpleString)
                && fields[3].AsString() == "connected";
            return true;
        }

        /// <summary>Stops new reads, then drains accepted work before the entry is disposed.</summary>
        internal async Task RetireAsync(TimeSpan? drainTimeout, CancellationToken cancellationToken)
        {
            RespireConnectionMultiplexer? multiplexer;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _closed = true;
                multiplexer = Volatile.Read(ref _multiplexer);
                // A removed replica's drain is not an outage: stop forwarding its state changes
                // and forget any slot health it reported.
                if (multiplexer is not null) DetachHandlers(multiplexer);
            }
            finally { _gate.Release(); }
            if (multiplexer is null) return;
            owner.NotifyReadReplicaRetired(multiplexer);
            await multiplexer.RetireAsync()
                .WaitAsync(drainTimeout ?? Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                _closed = true;
                multiplexer = Volatile.Read(ref _multiplexer);
                Volatile.Write(ref _multiplexer, null);
            }
            finally { _gate.Release(); }
            if (multiplexer is not null)
            {
                DetachHandlers(multiplexer);
                await multiplexer.DisposeAsync().ConfigureAwait(false);
                // A later replica at the same address must not inherit this node's slot health.
                owner.NotifyReadReplicaRetired(multiplexer);
            }
        }
    }
}
