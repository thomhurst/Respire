using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal sealed class ReadEndpointRouter(ClientCore core) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<RespireEndpoint, Entry> _entries = new();
    private readonly SemaphoreSlim _sentinelRefreshGate = new(1, 1);
    private RespireEndpoint[] _replicas = core.Options.ReplicaEndpoints.ToArray();
    private int _nextReplica;
    private int _disposed;
    private long _lastSentinelRefreshTicks;

    private async ValueTask SetEndpointsAsync(RespireEndpoint primary, IEnumerable<RespireEndpoint> replicas)
    {
        var endpoints = replicas.Where(endpoint => endpoint != primary).Distinct().ToArray();
        Volatile.Write(ref _replicas, endpoints);
        var retained = endpoints.ToHashSet();
        foreach (var pair in _entries)
        {
            if (retained.Contains(pair.Key) || !_entries.TryRemove(pair.Key, out var entry)) continue;
            await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask<RespireConnection> GetConnectionAsync(
        RespireReadFrom readFrom, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        switch (readFrom)
        {
            case RespireReadFrom.PrimaryPreferred:
                try { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                { return await GetReplicaAsync(cancellationToken).ConfigureAwait(false); }
            case RespireReadFrom.Replica:
                return await GetReplicaAsync(cancellationToken).ConfigureAwait(false);
            case RespireReadFrom.ReplicaPreferred:
                try { return await GetReplicaAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                { return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false); }
            default:
                return await GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<RespireConnection> GetPrimaryAsync(CancellationToken cancellationToken)
    {
        await core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return core.Multiplexer.GetConnection();
    }

    private async ValueTask<RespireConnection> GetReplicaAsync(CancellationToken cancellationToken)
    {
        if (core.Sentinel is { } sentinel
            && DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSentinelRefreshTicks) >= TimeSpan.TicksPerSecond)
            await RefreshSentinelReplicasAsync(sentinel, cancellationToken).ConfigureAwait(false);
        var endpoints = Volatile.Read(ref _replicas);
        if (endpoints.Length == 0)
            throw new RespireConnectionException("No eligible read replicas are configured or known to Sentinel.");

        var start = (uint)Interlocked.Increment(ref _nextReplica);
        Exception? lastError = null;
        for (var offset = 0; offset < endpoints.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = endpoints[(int)((start + (uint)offset) % (uint)endpoints.Length)];
            var entry = _entries.GetOrAdd(endpoint, static (value, state) => new Entry(value, state), core);
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

    private async ValueTask RefreshSentinelReplicasAsync(SentinelRouter sentinel, CancellationToken cancellationToken)
    {
        await _sentinelRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSentinelRefreshTicks) < TimeSpan.TicksPerSecond) return;
            RespireEndpoint[] endpoints;
            try { endpoints = await sentinel.DiscoverReplicaEndpointsAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                try { core.Logger?.LogDebug(error, "Sentinel replica refresh failed; retaining current endpoints"); }
                catch (Exception) { }
                Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
                return;
            }
            var primary = sentinel.Current?.Endpoint ?? core.Options.PrimaryEndpoint;
            await SetEndpointsAsync(primary, endpoints).ConfigureAwait(false);
            Volatile.Write(ref _lastSentinelRefreshTicks, DateTime.UtcNow.Ticks);
        }
        finally { _sentinelRefreshGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var entries = _entries.Values.ToArray();
        _entries.Clear();
        await Task.WhenAll(entries.Select(entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private sealed class Entry(RespireEndpoint endpoint, ClientCore owner) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private RespireConnectionMultiplexer? _multiplexer;

        internal async ValueTask<RespireConnection> GetConnectionAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(owner.Disposed, this);
                if (_multiplexer is null)
                {
                    var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
                        endpoint.Host, endpoint.Port, owner.Options.Connections,
                        owner.Options.ToConnectionOptions(), owner.Logger, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        multiplexer.StateChanged += owner.NotifyRecoveryStateChanged;
                        _multiplexer = multiplexer;
                        owner.NotifyRecoveryStateChanged(new RespireConnectionStateChange(
                            endpoint, RespireConnectionState.Connected, null));
                    }
                    catch
                    {
                        multiplexer.StateChanged -= owner.NotifyRecoveryStateChanged;
                        _multiplexer = null;
                        await multiplexer.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }

                var connection = _multiplexer.GetConnection();
                using var role = await connection.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
                if (!IsReplica(in role))
                    throw new RespireConnectionException($"Configured read endpoint {endpoint} did not report a replica ROLE.");
                return connection;
            }
            finally { _gate.Release(); }
        }

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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_multiplexer is { } multiplexer)
                {
                    _multiplexer = null;
                    multiplexer.StateChanged -= owner.NotifyRecoveryStateChanged;
                    await multiplexer.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
