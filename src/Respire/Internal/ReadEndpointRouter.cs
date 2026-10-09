using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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

    private readonly Lock _entriesGate = new();
    private readonly ConcurrentDictionary<RespireEndpoint, Entry> _entries = new(RespireEndpointComparer.Instance);
    // Entries removed from the topology drain before closing so reads already using them can finish.
    private readonly ConcurrentDictionary<Entry, byte> _retiring = new();
    // Release completed owners immediately. Bound retained exception identities and report
    // overflow counts at disposal so topology churn cannot accumulate unlimited failure history.
    private ReadRetirementFailures? _retirementFailures;
    private readonly SemaphoreSlim _sentinelRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RespireEndpoint[] _replicas = string.IsNullOrWhiteSpace(core.Options.SentinelPrimaryName)
        ? Order(core.Options.ReplicaEndpoints)
        : [];
    private int _nextReplica;
    private ReadyReplica? _readyReplica;
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

    internal bool IsCurrentReplicaEndpoint(RespireEndpoint endpoint)
        => ContainsEndpoint(Volatile.Read(ref _replicas), endpoint);

    internal (RespireEndpoint Endpoint, RespireConnection? Connection)[] CaptureHealthConnections()
        => Volatile.Read(ref _replicas).Select(endpoint =>
            (endpoint, _entries.TryGetValue(endpoint, out var entry) ? entry.GetExistingHealthConnection() : null)).ToArray();

    private void SetEndpoints(IEnumerable<RespireEndpoint> replicas)
    {
        // SENTINEL REPLICAS never lists the current primary, and ROLE validation rejects a node
        // that is not a replica. Filtering by a cached primary endpoint could wrongly drop a former
        // primary that rejoined as a replica while the cached generation is stale.
        var endpoints = Order(replicas);
        // Full fence: the sweep below must not read _entries before the new topology is visible.
        // A reader that inserts an entry the sweep misses then sees this array on its recheck.
        lock (_entriesGate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _readyReplica, null);
            Interlocked.Exchange(ref _replicas, endpoints);
            core.Circuits?.InvalidateMembership();
        }
        var retained = endpoints.ToHashSet(RespireEndpointComparer.Instance);
        foreach (var pair in _entries)
        {
            if (!retained.Contains(pair.Key)) RetireEntry(pair);
        }
    }

    private void RetireEntry(KeyValuePair<RespireEndpoint, Entry> pair)
    {
        lock (_entriesGate)
        {
            // Removal and retirement ownership must be atomic with the disposal snapshot.
            if (ReferenceEquals(Volatile.Read(ref _readyReplica)?.Entry, pair.Value))
                Volatile.Write(ref _readyReplica, null);
            if (_disposed != 0 || !_entries.TryRemove(pair)) return;
            _retiring.TryAdd(pair.Value, 0);
        }
        _ = RetireAsync(pair.Value);
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
            try { core.Logger?.ReadReplicaDrainFailed(entry.Endpoint, error); }
            catch (Exception) { }
        }
        // Keep the entry discoverable until cleanup finishes. Router disposal joins the same
        // completion, including a failure that the background retirement already observed.
        try { await entry.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (_entriesGate)
            {
                // If disposal already captured the entry, its shared completion owns this error.
                if (_retiring.TryRemove(entry, out _))
                    (_retirementFailures ??= new()).Add(error);
            }
            try { core.Logger?.ReadReplicaCloseFailed(error); }
            catch (Exception) { }
            return;
        }
        _retiring.TryRemove(entry, out _);
    }

    /// <summary>Selects a connection for a read under <paramref name="readFrom"/>.</summary>
    /// <remarks>The pooled ValueTask must be consumed exactly once, including asynchronous fallback.</remarks>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<RespireConnection> GetConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            if (readFrom is RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred
                && TryAcquireReadyConnection(readFrom, cancellationToken) is { } ready) return ready;
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

    /// <summary>Observes a fresh, published single-replica route without starting asynchronous selection.</summary>
    internal RespireConnection? TryAcquireReadyConnection(RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(core.Disposed, core);
        // GetConnectionAsync and direct callers also enter here. Check before
        // an ineligible snapshot can return null and bypass caller cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        // Multi-endpoint/socket rotation, zone ranking and Nearest keep their existing selectors.
        // Sentinel discovery must still run on schedule, even when this connection is healthy.
        if (readFrom is not (RespireReadFrom.Replica or RespireReadFrom.ReplicaPreferred)
            || core.Options.Connections != 1 || core.Sentinel is not null && IsSentinelRefreshDue()) return null;
        var publication = Volatile.Read(ref _readyReplica);
        if (publication is null || !IsReadyReplicaCurrent(publication))
            publication = CaptureReadyReplica();
        if (publication is null || publication.Entry.IsCoolingDown || publication.Entry.IsReplicationLinkDown) return null;
        var connection = publication.Entry.TryGetReadyConnection(publication);
        // An optimistic snapshot miss always falls back to the full asynchronous selector.
        if (connection is null || !IsReadyReplicaCurrent(publication)) return null;
        ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(core.Disposed, core);
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve the selector's shared cursor when a later publication adds replicas.
        Interlocked.Increment(ref _nextReplica);
        return connection;
    }

    // The entry dictionary changes only under _entriesGate. Invalidate this identity before
    // publication/removal, so a warm read can prove membership without hashing the endpoint twice.
    internal sealed class ReadyReplica(RespireEndpoint[] endpoints, Entry entry)
    {
        internal RespireEndpoint[] Endpoints { get; } = endpoints;
        internal Entry Entry { get; } = entry;
    }

    private ReadyReplica? CaptureReadyReplica()
    {
        // Ineligible multi-replica reads keep their lock-free miss before normal selection.
        if (Volatile.Read(ref _replicas).Length != 1) return null;
        lock (_entriesGate)
        {
            if (_disposed != 0) return null;
            var endpoints = Volatile.Read(ref _replicas);
            if (endpoints.Length != 1 || !_entries.TryGetValue(endpoints[0], out var entry)) return null;
            var current = Volatile.Read(ref _readyReplica);
            if (current is not null && ReferenceEquals(current.Endpoints, endpoints) && ReferenceEquals(current.Entry, entry))
                return current;
            var publication = new ReadyReplica(endpoints, entry);
            Volatile.Write(ref _readyReplica, publication);
            return publication;
        }
    }

    private bool IsReadyReplicaCurrent(ReadyReplica publication)
        => ReferenceEquals(publication, Volatile.Read(ref _readyReplica))
            && ReferenceEquals(publication.Endpoints, Volatile.Read(ref _replicas));

    /// <summary>Selects a read endpoint, then rents a separate connection for a blocking read.</summary>
    internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection, bool IsReplica)> RentDedicatedConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken, string? preferredZone = null, bool? replicaOnly = null,
        ReadAttempt? attempt = null)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(core.Disposed, core);
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var selection = replicaOnly switch
            {
                true => await GetReplicaAsync(cancellationToken,
                    ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? RespireReadFrom.AzAffinity : RespireReadFrom.Replica,
                    attempt).ConfigureAwait(false),
                false => default,
                _ => await SelectAsync(readFrom, cancellationToken, attempt).ConfigureAwait(false),
            };
            if (selection.Replica is not { } replica)
            {
                var lease = await TryRentPrimaryReadAsync(cancellationToken, attempt,
                    allowFallback: replicaOnly is null && (readFrom is RespireReadFrom.PrimaryPreferred or RespireReadFrom.Nearest
                        || ReadFallbackPolicy.UsesAvailabilityZone(readFrom)), preferredZone,
                    nearestPrimary: readFrom == RespireReadFrom.Nearest ? selection.Primary : null)
                    .ConfigureAwait(false);
                if (lease.Retired)
                {
                    if ((attempt ??= new()).TryRetryRetirement()) continue;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lease.Failure!).Throw();
                }
                if (lease.Failure is not { } error) return (lease.Pool!, lease.Connection!, false);
                RespireEndpoint? primaryAlias = null;
                if (lease.Pool?.MovingOwner is { } owner)
                {
                    var publication = owner.CaptureMovingPublication();
                    // The pool's publication proves these aliases name the same failed
                    // candidate. Do not exclude an owner that has since moved elsewhere.
                    if (ReferenceEquals(publication.Publication, lease.Pool.MovingPublication))
                        primaryAlias = publication.Endpoint;
                }
                if (!(attempt ??= new()).TryAdd(lease.Pool?.Endpoint
                        ?? new RespireEndpoint(selection.Connection.Host, selection.Connection.Port), error, primaryAlias))
                    attempt.ThrowFirstFailure();
                if (readFrom == RespireReadFrom.Nearest)
                {
                    continue;
                }
                // The shared primary can be healthy while its dedicated handshake fails.
                // Exclude that primary from the next selection instead of probing it again.
                try
                {
                    return await RentDedicatedConnectionAsync(readFrom, cancellationToken, preferredZone, replicaOnly: true, attempt)
                        .ConfigureAwait(false);
                }
                catch (Exception fallback) when (IsReadCandidateFailure(fallback, cancellationToken))
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    throw;
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
                // This operation remembers failures even when the shared cooldown is zero or
                // expires during another handshake. Allocate tracking only on the failure path.
                if (!(attempt ??= new()).TryAdd(replica.Endpoint, error))
                    attempt.ThrowFirstFailure();
                // No application command was accepted. Reselect after a failed dedicated
                // handshake, failed ROLE check, or removal of this replica during acquisition.
            }
        }
    }

    internal readonly record struct PrimaryReadLease(
        DedicatedConnectionPool? Pool, RespireConnection? Connection, Exception? Failure, bool Retired = false);

    internal async ValueTask<PrimaryReadLease> TryRentPrimaryReadAsync(
        CancellationToken cancellationToken, ReadAttempt? attempt, bool allowFallback, string? preferredZone,
        RespireConnectionMultiplexer? nearestPrimary = null)
    {
        DedicatedConnectionPool? pool = null;
        try
        {
            pool = await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(core.Disposed, core);
            cancellationToken.ThrowIfCancellationRequested();
            attempt?.ThrowIfFailed(pool.Endpoint);
            var connection = await pool.RentAsync(cancellationToken, preferredZone: preferredZone).ConfigureAwait(false);
            return new(pool, connection, null);
        }
        catch (Exception error) when (!core.Disposed && !cancellationToken.IsCancellationRequested && pool is { IsStopping: true }
            && DedicatedConnectionPool.IsRetirementFailure(error))
        {
            // Reselect the endpoint as well as the pool. Retirement alone does not exclude
            // a healthy replacement at the same address; it is not a candidate failure.
            return new(pool, null, error, Retired: true);
        }
        catch (Exception error) when (allowFallback && IsReadCandidateFailure(error, cancellationToken))
        {
            // A returned pool identifies the actual owner after failover. Discovery can fail
            // before returning any pool; then cool the primary whose selection triggered it.
            if (nearestPrimary is not null)
                NearestLatency!.ConnectionFailed(pool?.MovingOwner ?? nearestPrimary);
            return new(pool, null, error);
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

    internal async ValueTask<Selection> SelectAsync(RespireReadFrom readFrom, CancellationToken cancellationToken,
        ReadAttempt? attempt = null)
    {
        switch (readFrom)
        {
            case RespireReadFrom.Nearest:
                return await GetNearestAsync(cancellationToken, attempt: attempt).ConfigureAwait(false);
            case RespireReadFrom.PrimaryPreferred:
                try { return await GetPrimaryAsync(cancellationToken, attempt: attempt).ConfigureAwait(false); }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                { return await GetReplicaAsync(cancellationToken, attempt: attempt).ConfigureAwait(false); }
            case RespireReadFrom.Replica:
                return await GetReplicaAsync(cancellationToken, attempt: attempt).ConfigureAwait(false);
            case RespireReadFrom.ReplicaPreferred:
            case RespireReadFrom.AzAffinity:
            case RespireReadFrom.AzAffinityReplicasAndPrimary:
                try { return await GetReplicaAsync(cancellationToken, readFrom, attempt: attempt).ConfigureAwait(false); }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    return await GetPrimaryAsync(cancellationToken,
                        ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null, attempt).ConfigureAwait(false);
                }
            default:
                return await GetPrimaryAsync(cancellationToken, attempt: attempt).ConfigureAwait(false);
        }
    }

    // Fall back only for availability failures; programming errors and disposal propagate.
    internal static bool IsUnavailable(Exception error, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && error is RespireConnectionException or RespireTimeoutException or IOException or SocketException;

    private async ValueTask<Selection> GetPrimaryAsync(CancellationToken cancellationToken,
        string? preferredZone = null, ReadAttempt? attempt = null)
    {
        attempt?.ThrowIfFailed(core.Multiplexer.ActiveConnectionEndpoint);
        await core.EnsureConnectedAsync(cancellationToken, observeEstablishmentErrors: true).ConfigureAwait(false);
        var multiplexer = core.Multiplexer;
        attempt?.ThrowIfFailed(multiplexer.ActiveConnectionEndpoint);
        return new Selection(preferredZone is null ? multiplexer.GetConnection() : multiplexer.GetConnectionForZone(preferredZone),
            null, multiplexer);
    }

    private async ValueTask<Selection> GetReplicaAsync(CancellationToken cancellationToken,
        RespireReadFrom readFrom = RespireReadFrom.Replica, ReadAttempt? attempt = null)
    {
        var endpoints = await GetReplicaEndpointsAsync(cancellationToken).ConfigureAwait(false);
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        try
        {
            return await GetReplicaFromEndpointsAsync(endpoints, cancellationToken, readFrom, attempt: attempt).ConfigureAwait(false);
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
            return await GetReplicaFromEndpointsAsync(refreshed, cancellationToken, readFrom, attempt: attempt).ConfigureAwait(false);
        }
    }

    internal async ValueTask<Selection> GetReplicaFromEndpointsAsync(
        RespireEndpoint[] endpoints, CancellationToken cancellationToken, RespireReadFrom readFrom = RespireReadFrom.Replica,
        RespireConnection? excluded = null, ReadAttempt? attempt = null)
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
                if (attempt?.ContainsFailure(endpoint) == true) continue;
                if (excluded is not null && HedgedReadPolicy.IsOriginalEndpoint(endpoint, excluded)) continue;
                var entry = await GetCurrentReplicaEntryAsync(endpoint).ConfigureAwait(false);
                if (entry is null) continue;
                // A failed replica remains in cooldown so it cannot add a timeout to every read.
                if (entry.IsCoolingDown) continue;
                attempted = true;
                try
                {
                    var selection = new Selection(await entry.GetConnectionAsync(cancellationToken,
                        ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null).ConfigureAwait(false), entry, null);
                    if (excluded is not null && !HedgedReadPolicy.IsDifferentPeer(excluded, selection.Connection)) continue;
                    var local = ReadFallbackPolicy.IsSameZone(selection.Connection, core.Options.ClientAvailabilityZone);
                    if (candidates.Offer(selection, local, !entry.IsReplicationLinkDown, readFrom)) return selection;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ObjectDisposedException)
                {
                    lastError = error;
                    TryRecordFailure(entry, error);
                }
            }

            if (ReadFallbackPolicy.ShouldProbeLocalPrimary(readFrom, core.Multiplexer, core.Options.ClientAvailabilityZone))
            {
                try
                {
                    var primary = await GetPrimaryAsync(cancellationToken, core.Options.ClientAvailabilityZone, attempt).ConfigureAwait(false);
                    if (ReadFallbackPolicy.IsSameZone(primary.Connection, core.Options.ClientAvailabilityZone)
                        && (excluded is null || HedgedReadPolicy.IsDifferentPeer(excluded, primary.Connection))) return primary;
                }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken)) { lastError = error; }
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

    private void TryRecordFailure(Entry entry, Exception error, bool nearest = false)
    {
        entry.MarkFailed();
        try
        {
            if (nearest) core.Logger?.NearestReadCandidateUnavailable(entry.Endpoint, error);
            else core.Logger?.ReadReplicaUnavailable(entry.Endpoint, error);
        }
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
            try { core.Logger?.SentinelReplicaBackgroundRefreshFailed(error); }
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
                        core.Logger?.SentinelReplicaDiscoveryFailed(Volatile.Read(ref _replicas).Length, error);
                    else
                        core.Logger?.SentinelReplicaRefreshFailed(error);
                }
                catch (Exception) { }
                Volatile.Write(ref _lastSentinelRefresh, Stopwatch.GetTimestamp());
                return;
            }
            if (Volatile.Read(ref _disposed) != 0) return;
            if (Interlocked.Exchange(ref _refreshFailures, 0) != 0)
            {
                try { core.Logger?.SentinelReplicaDiscoveryRecovered(endpoints.Length); }
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
        Entry[] entries;
        List<Exception>? failures;
        lock (_entriesGate)
        {
            Volatile.Write(ref _readyReplica, null);
            entries = _entries.Values.Concat(_retiring.Keys).Distinct().ToArray();
            failures = _retirementFailures?.Snapshot();
            _retirementFailures = null;
            _entries.Clear();
            _retiring.Clear();
        }
        Cursors.Clear();
        try { await CleanupTasks.WhenAllAsync(entries.Select(entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        CleanupTasks.Rethrow(failures);
    }

    internal readonly record struct Selection(
        RespireConnection Connection, Entry? Replica, RespireConnectionMultiplexer? Primary);

    // Construction runs under the router lifecycle gate. Keep it side-effect-free; connection
    // creation, callbacks, and asynchronous work belong in the acquisition methods.
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
        private TaskCompletionSource? _disposeCompletion;

        internal RespireEndpoint Endpoint => endpoint;
        internal RespireConnection? GetExistingHealthConnection()
            => IsConnected ? Volatile.Read(ref _multiplexer)?.GetExistingHealthConnection() : null;
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

        internal RespireConnection? TryGetReadyConnection(ReadyReplica? publication = null)
        {
            if (_closed || Volatile.Read(ref _multiplexer) is not { IsInitialized: true, IsRetired: false } current) return null;
            // Observation must not start recovery or throw before the normal selector can fall back.
            var connection = current.GetExistingHealthConnection();
            return connection is not null && !_closed && ReferenceEquals(current, Volatile.Read(ref _multiplexer))
                && (publication is null
                    ? router.IsCurrent(this) && ContainsEndpoint(Volatile.Read(ref router._replicas), endpoint)
                    : ReferenceEquals(publication.Entry, this) && router.IsReadyReplicaCurrent(publication))
                && connection.IsAcceptingCommands
                && _health.Check(connection, router.RoleRevalidationInterval) == ReplicaValidation.Fresh
                ? connection : null;
        }

        internal void MarkFailed() => _health.MarkFailed();

        internal async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentDedicatedConnectionAsync(
            CancellationToken cancellationToken, string? preferredZone = null)
        {
            DedicatedConnectionPool pool;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(owner.Disposed, owner);
                if (_closed || !router.IsCurrent(this))
                    throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                // Standalone replicas use ROLE validation, not Cluster's READONLY handshake.
                pool = _dedicatedPool ??= new(endpoint.Host, endpoint.Port,
                    owner.CreateConnectionOptions() with { ObserveEstablishmentErrors = true }, owner.Logger);
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
                    try
                    {
                        var checkedAt = Stopwatch.GetTimestamp();
                        using var role = await connection.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
                        if (!_health.Record(connection, checkedAt, in role))
                        {
                            var message = $"Configured read endpoint {endpoint} did not report a replica ROLE.";
                            throw role.IsError ? new RespireConnectionException(message, ResponseReader.ServerError(in role, "ROLE"))
                                : new RespireConnectionException(message);
                        }
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested
                        && !router._lifetime.IsCancellationRequested && !pool.IsStopping && error is not ObjectDisposedException)
                    {
                        // Connect failures are owned by establishment; only ROLE belongs here.
                        RespireTelemetry.RecordError(error, internallyHandled: true);
                        throw;
                    }
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
                if (pool.IsStopping && DedicatedConnectionPool.IsRetirementFailure(error))
                    throw new RespireConnectionException($"Read replica {endpoint} retired during dedicated acquisition.", error);
                throw;
            }
        }

        internal ValueTask<RespireConnection?> GetNearestConnectionAsync(
            ReadLatencySampler<RespireConnection> sampler, CancellationToken cancellationToken)
            => GetConnectionCoreAsync(cancellationToken, preferredZone: null, sampler);

        /// <summary>Acquires a current connection after validating its replication role.</summary>
        internal async ValueTask<RespireConnection> GetConnectionAsync(CancellationToken cancellationToken, string? preferredZone = null)
            => (await GetConnectionCoreAsync(cancellationToken, preferredZone, sampler: null).ConfigureAwait(false))!;

        private async ValueTask<RespireConnection?> GetConnectionCoreAsync(CancellationToken cancellationToken,
            string? preferredZone, ReadLatencySampler<RespireConnection>? sampler)
        {
            // Fast path: a recently validated connection needs no lock and no extra round trip.
            RespireConnection? selected = null;
            RespireConnectionMultiplexer? selectedFrom = null;
            var interval = router.RoleRevalidationInterval;
            if (!_closed && Volatile.Read(ref _multiplexer) is { } current)
            {
                selectedFrom = current;
                // Check and return the same physical socket. Exclusion lasts for this selection
                // even if the pending probe completes next.
                selected = SelectSocket(current, preferredZone, sampler);
                if (selected is null) return null;
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
                        owner.CreateConnectionOptions() with { ObserveEstablishmentErrors = true },
                        owner.Logger, linked.Token).ConfigureAwait(false);
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

                // The socket or its probe state may have changed while acquiring the gate.
                if (!ReferenceEquals(selectedFrom, _multiplexer) || selected is null || !selected.IsAcceptingCommands
                    || sampler?.IsOccupied(selected) == true)
                    selected = SelectSocket(_multiplexer, preferredZone, sampler);
                if (selected is null) return null;
                if (selected.IsAcceptingCommands && _health.Check(selected, interval) == ReplicaValidation.Fresh)
                    return selected;
                ReadLatencySampler<RespireConnection>.ValidationReservation reservation = default;
                if (sampler is not null && !sampler.TryReserveForValidation(selected, out reservation)) return null;
                using (reservation)
                {
                    var checkedAt = Stopwatch.GetTimestamp();
                    // The reservation belongs to this physical socket. A MOVING handoff
                    // must reject validation here, not reroute ROLE onto an unreserved socket.
                    try
                    {
                        using var role = await selected.SendAsync(new Cmd(Verbs.Role), linked.Token,
                            pinToConnection: true).ConfigureAwait(false);
                        if (!_health.Record(selected, checkedAt, in role))
                            throw role.IsError
                                ? new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.",
                                    ResponseReader.ServerError(in role, "ROLE"))
                                : new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                        return selected;
                    }
                    catch (Exception error) when (!linked.IsCancellationRequested && error is not ObjectDisposedException)
                    {
                        // Establishment already observes connect failures. This boundary owns
                        // only the subsequent ROLE failure, which selection can hide by falling back.
                        RespireTelemetry.RecordError(error, internallyHandled: true);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && router._lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ReadEndpointRouter));
            }
            finally { if (entered) _gate.Release(); }
        }

        private static RespireConnection? SelectSocket(RespireConnectionMultiplexer multiplexer, string? preferredZone,
            ReadLatencySampler<RespireConnection>? sampler)
            => NearestReadSelection.AvoidPendingProbe(multiplexer,
                preferredZone is null ? multiplexer.GetConnection() : multiplexer.GetConnectionForZone(preferredZone),
                sampler, preferredZone);

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
            RespireConnectionMultiplexer? multiplexer = null;
            DedicatedConnectionPool? pool = null;
            TaskCompletionSource completion;
            var ownsCleanup = false;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposeCompletion is null)
                {
                    _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ownsCleanup = true;
                    _closed = true;
                    multiplexer = Volatile.Read(ref _multiplexer);
                    pool = _dedicatedPool;
                    Volatile.Write(ref _multiplexer, null);
                }
                completion = _disposeCompletion;
            }
            finally { _gate.Release(); }
            if (ownsCleanup)
            {
                try
                {
                    await DisposeResourcesAsync(multiplexer, pool).ConfigureAwait(false);
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
            }
            await completion.Task.ConfigureAwait(false);
        }

        private async Task DisposeResourcesAsync(RespireConnectionMultiplexer? multiplexer, DedicatedConnectionPool? pool)
        {
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
