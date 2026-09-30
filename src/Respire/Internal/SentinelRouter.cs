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
    private readonly SentinelDiscoveryState _discovery = new(core.Options.Endpoints);
    private readonly HashSet<Generation> _owned = [];
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    internal Generation? Current => Volatile.Read(ref _current);
    internal bool IsConnected => Current is { IsRetired: false } generation && generation.Multiplexer.IsConnected;

    internal async ValueTask<Generation> GetGenerationAsync(CancellationToken cancellationToken)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (Current is { IsRetired: false } current && current.Multiplexer.IsConnected) return current;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _discoveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            if (Current is { IsRetired: false } existing && existing.Multiplexer.IsConnected) return existing;
            var replacement = await SentinelResolver.ResolveAndConnectPrimaryAsync(
                core.Options, ConnectGenerationAsync, linked.Token, _discovery).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (replacement.IsRetired)
                    throw new RespireConnectionException("Sentinel primary changed before its generation was published.");
                Volatile.Write(ref _current, replacement);
            }
            return replacement;
        }
        catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
            error, cancellationToken, linked.Token))
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
        finally { _discoveryGate.Release(); }
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
        if (!generation.TryRetire()) return;
        // The transport admission check sees retirement before any waiting caller resumes.
        // Cache invalidation is synchronous; metrics and health callbacks run elsewhere.
        var evictions = core.ClientCache?.FlushForContinuityLossWithoutMetrics();
        lock (_gate)
        {
            generation.Retirement = Task.Run(async () =>
            {
                if (evictions is { } count) ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count);
                await DrainAsync(generation).ConfigureAwait(false);
            });
        }
    }

    private async Task DrainAsync(Generation generation)
    {
        generation.StopConnections();
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
            lock (_gate) _owned.Remove(generation);
        }
        catch (Exception error)
        {
            try { await poolDrain.ConfigureAwait(false); }
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
            lock (_gate) owned = _owned.ToArray();
            await Task.WhenAll(owned.Select(generation => generation.DisposeAsync().AsTask())).ConfigureAwait(false);
            await Task.WhenAll(owned.Select(generation => generation.Retirement)).ConfigureAwait(false);
            lock (_gate) _owned.Clear();
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

        public void ConnectionFailed(RespireConnection connection)
        {
            lock (_connectionsGate) _connections.Remove(connection);
            _owner.Invalidate(this);
        }

        internal void StopConnections()
        {
            RespireConnection[] connections;
            lock (_connectionsGate) connections = _connections.ToArray();
            foreach (var connection in connections) _ = connection.RetireAsync();
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
            if (reply.IsError) return reply.GetErrorMessage().StartsWith("READONLY ", StringComparison.Ordinal);
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
            await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask())).ConfigureAwait(false);
            await Pool.DisposeAsync().ConfigureAwait(false);
            await Multiplexer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
