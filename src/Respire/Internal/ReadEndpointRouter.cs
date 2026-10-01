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

internal sealed class ReadEndpointRouter(ClientCore core) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<RespireEndpoint, Entry> _entries = new();
    // Entries removed from the topology stay open briefly so reads already using them can finish.
    private readonly ConcurrentDictionary<Entry, byte> _retiring = new();
    private readonly SemaphoreSlim _sentinelRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private RespireEndpoint[] _replicas = Order(core.Options.ReplicaEndpoints);
    private int _nextReplica;
    private int _disposed;
    private int _backgroundRefresh;
    private long _lastSentinelRefreshTicks;

    /// <summary>
    /// How long a ROLE check stays valid for one physical connection. Reads within this window skip
    /// the extra round trip; a later read revalidates, so a promoted node stops serving reads.
    /// </summary>
    internal TimeSpan RoleRevalidationInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Sorted so a stable selection, used by cursor reads, survives Sentinel reply reordering.
    private static RespireEndpoint[] Order(IEnumerable<RespireEndpoint> endpoints)
        => endpoints.Distinct()
            .OrderBy(static endpoint => endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static endpoint => endpoint.Port)
            .ToArray();

    private void SetEndpoints(RespireEndpoint primary, IEnumerable<RespireEndpoint> replicas)
    {
        var endpoints = Order(replicas.Where(endpoint => endpoint != primary));
        Volatile.Write(ref _replicas, endpoints);
        var retained = endpoints.ToHashSet();
        foreach (var pair in _entries)
        {
            if (retained.Contains(pair.Key) || !_entries.TryRemove(pair.Key, out var entry)) continue;
            _retiring.TryAdd(entry, 0);
            _ = RetireAsync(entry);
        }
    }

    private async Task RetireAsync(Entry entry)
    {
        try
        {
            // A read borrows a connection before sending, so give in-flight commands their timeout.
            await Task.Delay(core.Options.CommandTimeout ?? TimeSpan.FromSeconds(30), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        // Router disposal owns entries that are still retiring.
        if (!_retiring.TryRemove(entry, out _)) return;
        try { await entry.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            try { core.Logger?.LogDebug(error, "Closing a removed read replica failed"); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Selects a connection for a read. A <paramref name="stable"/> selection always prefers the same
    /// replica, so cursor pages issued by successive calls stay on the server that issued the cursor.
    /// </summary>
    internal async ValueTask<RespireConnection> GetConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken, bool stable = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        switch (readFrom)
        {
            case RespireReadFrom.PrimaryPreferred:
                try { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsUnavailable(error, cancellationToken))
                { return await GetReplicaAsync(stable, cancellationToken).ConfigureAwait(false); }
            case RespireReadFrom.Replica:
                return await GetReplicaAsync(stable, cancellationToken).ConfigureAwait(false);
            case RespireReadFrom.ReplicaPreferred:
                try { return await GetReplicaAsync(stable, cancellationToken).ConfigureAwait(false); }
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

    private async ValueTask<RespireConnection> GetPrimaryAsync(CancellationToken cancellationToken)
    {
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return core.Multiplexer.GetConnection();
    }

    private async ValueTask<RespireConnection> GetReplicaAsync(bool stable, CancellationToken cancellationToken)
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

        var start = stable ? 0u : (uint)Interlocked.Increment(ref _nextReplica);
        Exception? lastError = null;
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
            try { return await entry.GetConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = error;
                try { core.Logger?.LogDebug(error, "Read replica unavailable at {Endpoint}", endpoint); }
                catch (Exception) { }
            }
        }

        throw new RespireConnectionException("No healthy, role-validated read replicas are available.",
            lastError ?? new InvalidOperationException("No replica connection attempt was completed."));
    }

    private bool IsSentinelRefreshDue()
        => DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSentinelRefreshTicks) >= TimeSpan.TicksPerSecond;

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

    private async ValueTask RefreshSentinelReplicasAsync(SentinelRouter sentinel, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _sentinelRefreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!IsSentinelRefreshDue()) return;
            RespireEndpoint[] endpoints;
            try { endpoints = await sentinel.DiscoverReplicaEndpointsAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                try { core.Logger?.LogDebug(error, "Sentinel replica refresh failed; retaining current endpoints"); }
                catch (Exception) { }
                Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
                return;
            }
            if (Volatile.Read(ref _disposed) != 0) return;
            var primary = sentinel.Current?.Endpoint ?? core.Options.PrimaryEndpoint;
            SetEndpoints(primary, endpoints);
            Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
        }
        finally { _sentinelRefreshGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        var entries = _entries.Values.Concat(_retiring.Keys).Distinct().ToArray();
        _entries.Clear();
        _retiring.Clear();
        await Task.WhenAll(entries.Select(entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private sealed class Entry(RespireEndpoint endpoint, ClientCore owner, ReadEndpointRouter router) : IAsyncDisposable
    {
        // Never disposed: a waiter racing with disposal must observe _disposed, not a disposed gate.
        private readonly SemaphoreSlim _gate = new(1, 1);
        // When each physical connection last passed ROLE. Reconnects publish new connection objects,
        // which are validated before use; dead connections drop out with their weak keys.
        private readonly ConditionalWeakTable<RespireConnection, StrongBox<long>> _validated = new();
        private RespireConnectionMultiplexer? _multiplexer;
        private volatile bool _disposed;

        internal async ValueTask<RespireConnection> GetConnectionAsync(CancellationToken cancellationToken)
        {
            // Fast path: a recently validated connection needs no lock and no extra round trip.
            if (!_disposed && Volatile.Read(ref _multiplexer) is { } current)
            {
                var connection = current.GetConnection();
                if (IsFreshlyValidated(connection)) return connection;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new RespireConnectionException($"Read replica {endpoint} was removed from the topology.");
                ObjectDisposedException.ThrowIf(owner.Disposed, this);
                if (_multiplexer is null)
                {
                    var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
                        endpoint.Host, endpoint.Port, owner.Options.Connections,
                        owner.Options.ToConnectionOptions(), owner.Logger, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        multiplexer.StateChanged += owner.NotifyRecoveryStateChanged;
                        Volatile.Write(ref _multiplexer, multiplexer);
                        owner.NotifyRecoveryStateChanged(new RespireConnectionStateChange(
                            endpoint, RespireConnectionState.Connected, null));
                    }
                    catch
                    {
                        multiplexer.StateChanged -= owner.NotifyRecoveryStateChanged;
                        Volatile.Write(ref _multiplexer, null);
                        await multiplexer.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }

                var selected = _multiplexer.GetConnection();
                if (IsFreshlyValidated(selected)) return selected;
                var checkedAt = Stopwatch.GetTimestamp();
                using var role = await selected.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
                if (!IsReplica(in role))
                {
                    _validated.Remove(selected);
                    throw new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                }
                _validated.AddOrUpdate(selected, new StrongBox<long>(checkedAt));
                return selected;
            }
            finally { _gate.Release(); }
        }

        private bool IsFreshlyValidated(RespireConnection connection)
            => _validated.TryGetValue(connection, out var checkedAt)
                && Stopwatch.GetElapsedTime(Volatile.Read(ref checkedAt.Value)) < router.RoleRevalidationInterval;

        private static bool IsReplica(in RespValue role)
        {
            if (role.Type != RespDataType.Array) return false;
            var fields = role.AsArray();
            if (fields.IsEmpty || fields[0].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
            var name = fields[0].AsString();
            return name is "slave" or "replica";
        }

        public async ValueTask DisposeAsync()
        {
            RespireConnectionMultiplexer? multiplexer;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                multiplexer = Volatile.Read(ref _multiplexer);
                Volatile.Write(ref _multiplexer, null);
            }
            finally { _gate.Release(); }
            if (multiplexer is not null)
            {
                multiplexer.StateChanged -= owner.NotifyRecoveryStateChanged;
                await multiplexer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
