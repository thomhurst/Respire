using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Discovery publishes an entire validated generation. Old generations remain owned until
// their accepted commands, borrowed leases, and correction fences finish or disposal aborts them.
internal sealed class SentinelRouter(ClientCore core) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SentinelDiscoveryState _discovery = new(core.Options.Endpoints.Count == 0
        ? [new RespireEndpoint("localhost", 26379)] : core.Options.Endpoints);
    private readonly HashSet<Generation> _owned = [];
    private readonly HashSet<DedicatedConnectionPool> _correctionPools = [];
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private Task _notifications = Task.CompletedTask;

    internal Generation? Current => Volatile.Read(ref _current);
    internal bool IsConnected => Current is { IsRetired: false } generation && generation.Multiplexer.IsConnected;

    internal sealed class CorrectionLease(SentinelRouter owner, DedicatedConnectionPool pool) : IAsyncDisposable
    {
        private int _disposed;
        internal DedicatedConnectionPool Pool => pool;
        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseCorrectionAsync(pool) : default;
    }

    internal CorrectionLease GetCorrectionLease(RespireConnection original)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = (original.Multiplexer?.Options ?? core.Options.ToConnectionOptions()) with
            {
                Generation = null, EnableClientTracking = false, PushHandler = null, SubscriptionConfirmationHandler = null,
            };
            if (options.UseTls)
                options = options with { TlsOptions = RespireConnection.CreateTlsOptions(options.TlsOptions, original.Host) };
            // Corrections remain on the original physical peer, even after DNS or Sentinel moves.
            var pool = new DedicatedConnectionPool(original.NetworkPeerAddress ?? original.Host,
                original.NetworkPeerPort ?? original.Port, options, core.Logger);
            _correctionPools.Add(pool);
            return new(this, pool);
        }
    }

    private async ValueTask ReleaseCorrectionAsync(DedicatedConnectionPool pool)
    {
        await pool.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _correctionPools.Remove(pool);
    }

    internal async ValueTask<Generation> GetGenerationAsync(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is { IsRetired: false } current && current.Multiplexer.IsConnected) return current;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        Generation? unpublished = null;
        try
        {
            await _discoveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            if (Current is { IsRetired: false } existing && existing.Multiplexer.IsConnected) return existing;
            if (Current is { } previous) Invalidate(previous);
            var replacement = await SentinelResolver.ResolveAndConnectPrimaryAsync(
                core.Options, ConnectGenerationAsync, linked.Token, _discovery).ConfigureAwait(false);
            unpublished = replacement;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                linked.Token.ThrowIfCancellationRequested();
                if (replacement.IsRetired)
                    throw new RespireConnectionException("Sentinel primary changed before its generation was published.");
                var old = Current;
                Volatile.Write(ref _current, replacement);
                unpublished = null;
                QueueNotificationLocked(() =>
                {
                    core.NotifySentinelPrimaryChanged(old?.Multiplexer, replacement.Multiplexer);
                    if (old is not null && old.Endpoint != replacement.Endpoint)
                        RespireTelemetry.SentinelFailovers.Add(1,
                            new KeyValuePair<string, object?>("server.address", replacement.Endpoint.Host),
                            new KeyValuePair<string, object?>("server.port", replacement.Endpoint.Port));
                });
            }
            return replacement;
        }
        catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
            error, cancellationToken, linked.Token))
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        finally
        {
            try
            {
                if (unpublished is not null)
                {
                    await unpublished.DisposeAsync().ConfigureAwait(false);
                    lock (_gate) _owned.Remove(unpublished);
                }
            }
            finally { if (acquired) _discoveryGate.Release(); }
        }
    }

    private async ValueTask<Generation> ConnectGenerationAsync(RespireOptions options, CancellationToken cancellationToken)
    {
        var generation = new Generation(this, core, options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _owned.Add(generation);
        }
        try
        {
            await generation.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            return generation;
        }
        catch
        {
            await generation.DisposeAsync().ConfigureAwait(false);
            lock (_gate) _owned.Remove(generation);
            throw;
        }
    }

    private void Invalidate(Generation generation)
    {
        lock (_gate)
        {
            // Retirement and its cleanup task become visible together to disposal. Once
            // disposal owns the router, it aborts every generation itself.
            if (!generation.TryRetire() || _disposed) return;
            // The transport admission check sees retirement before any waiting caller resumes.
            // Cache invalidation is synchronous; metrics and health callbacks run elsewhere.
            var evictions = core.ClientCache?.FlushForContinuityLossWithoutMetrics();
            if (evictions is { } count)
                QueueNotificationLocked(() => ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count));
            if (ReferenceEquals(Current, generation))
                QueueNotificationLocked(() => core.NotifySentinelDisconnected(generation.Multiplexer));
            generation.Retirement = Task.Run(() => DrainAsync(generation));
        }
    }

    private void QueueNotificationLocked(Action notification)
    {
        var previous = _notifications;
        _notifications = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (!core.Disposed) notification();
            }
            catch (Exception error)
            {
                try { core.Logger?.LogWarning(error, "Sentinel state observer failed"); }
                catch (Exception) { /* Keep later notifications independent of a user logger failure. */ }
            }
        });
    }

    private async Task DrainAsync(Generation generation)
    {
        var connectionsDrained = generation.StopConnections();
        var poolDrain = generation.Pool.RetireAsync().AsTask();
        try
        {
            try { await generation.Multiplexer.RetireAsync().ConfigureAwait(false); }
            catch (Exception) when (generation.Multiplexer.RetirementDrained && !_lifetime.IsCancellationRequested) { }
            var delay = 1;
            while (generation.Multiplexer.HasPendingCorrectionFences && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await generation.Multiplexer.FenceRetiredConnectionsAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    core.Logger?.LogWarning(error, "Sentinel generation at {Endpoint} retains an unacknowledged correction fence", generation.Endpoint);
                    await Task.Delay(TimeSpan.FromSeconds(delay), _lifetime.Token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 30);
                }
            }
            await poolDrain.ConfigureAwait(false);
            await connectionsDrained.ConfigureAwait(false);
            lock (_gate) _owned.Remove(generation);
        }
        catch (Exception error)
        {
            try { await Task.WhenAll(poolDrain, connectionsDrained).ConfigureAwait(false); }
            catch (Exception poolError) { error = new AggregateException(error, poolError); }
            if (!_lifetime.IsCancellationRequested)
                core.Logger?.LogWarning(error, "Sentinel generation cleanup remains owned at {Endpoint}", generation.Endpoint);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _disposed = true;
            _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = DisposeCoreAsync(_disposeCompletion);
            return new(_disposeCompletion.Task);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _discoveryGate.WaitAsync().ConfigureAwait(false);
            _discoveryGate.Release();
            Generation[] owned;
            DedicatedConnectionPool[] corrections;
            lock (_gate)
            {
                owned = _owned.ToArray();
                corrections = _correctionPools.ToArray();
            }
            // Start every owned cleanup before observing failures, then join retirement too.
            // A failing correction or connection must not strand another generation.
            Exception? disposeError = null;
            try
            {
                await Task.WhenAll(corrections.Select(pool => pool.DisposeAsync().AsTask())
                    .Concat(owned.Select(generation => generation.DisposeAsync().AsTask()))).ConfigureAwait(false);
            }
            catch (Exception error) { disposeError = error; }
            try { await Task.WhenAll(owned.Select(generation => generation.Retirement)).ConfigureAwait(false); }
            catch (Exception error) when (disposeError is not null) { throw new AggregateException(disposeError, error); }
            if (disposeError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeError).Throw();
            lock (_gate)
            {
                _owned.Clear();
                _correctionPools.Clear();
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal sealed class Generation : IConnectionGeneration, IAsyncDisposable
    {
        private readonly SentinelRouter _owner;
        private readonly object _connectionsGate = new();
        private readonly HashSet<RespireConnection> _connections = [];
        private int _retired;
        internal readonly RespireEndpoint Endpoint;
        internal readonly RespireConnectionMultiplexer Multiplexer;
        internal readonly DedicatedConnectionPool Pool;
        internal readonly RespireConnectionOptions ConnectionOptions;
        internal Task Retirement = Task.CompletedTask;

        internal Generation(SentinelRouter owner, ClientCore core, RespireOptions options)
        {
            _owner = owner;
            Endpoint = options.PrimaryEndpoint;
            ConnectionOptions = options.ToConnectionOptions() with { Generation = this };
            RespirePushHandler? pushHandler = core.ClientCache is { } cache ? cache.HandlePush : null;
            var commandOptions = options.ToConnectionOptions(pushHandler,
                enableClientTracking: core.ClientCache is not null) with { Generation = this };
            Multiplexer = RespireConnectionMultiplexer.Create(Endpoint.Host, Endpoint.Port, options.Connections, commandOptions, core.Logger);
            Pool = new(Endpoint.Host, Endpoint.Port, ConnectionOptions, core.Logger, core.NotifyRecoveryStateChanged);
        }

        public bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal bool TryRetire() => Interlocked.Exchange(ref _retired, 1) == 0;

        public async ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
        {
            using var reply = await connection.SendAsync(new Cmd(Verbs.Role), cancellationToken).ConfigureAwait(false);
            if (!IsPrimary(in reply))
            {
                _owner.Invalidate(this);
                throw new RespireConnectionException("Sentinel candidate did not confirm a valid primary ROLE.");
            }
            lock (_connectionsGate)
            {
                if (IsRetired || !connection.IsConnected)
                    throw new RespireConnectionException("Sentinel candidate closed before validation completed.");
                _connections.Add(connection);
            }
        }

        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response)
        {
            if (ContainsReadOnly(in response) || operation == "ROLE" && !response.IsError && !IsPrimary(in response))
                _owner.Invalidate(this);
        }

        public void ConnectionClosed(RespireConnection connection, bool unexpected)
        {
            lock (_connectionsGate) _connections.Remove(connection);
            if (unexpected) _owner.Invalidate(this);
        }

        internal Task StopConnections()
        {
            RespireConnection[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            return Task.WhenAll(connections.Select(connection => connection.RetireAsync()));
        }

        private static bool IsPrimary(in RespValue reply)
        {
            if (reply.Type != RespDataType.Array) return false;
            var role = reply.AsArray();
            return role.Length >= 3 && role[0].Type is RespDataType.BulkString or RespDataType.SimpleString
                && role[0].AsString() == "master" && role[1].Type == RespDataType.Integer && role[2].Type == RespDataType.Array;
        }

        private static bool ContainsReadOnly(in RespValue reply)
        {
            if (reply.IsError)
            {
                var error = reply.GetErrorMessage();
                return error == "READONLY" || error.StartsWith("READONLY ", StringComparison.Ordinal);
            }
            if (reply.Type != RespDataType.Array) return false;
            foreach (var element in reply.AsArray())
                if (ContainsReadOnly(in element)) return true;
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            TryRetire();
            RespireConnection[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask())
                .Append(Pool.DisposeAsync().AsTask()).Append(Multiplexer.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
    }
}
