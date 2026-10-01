using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Discovery publishes an entire validated generation. Old generations remain owned until
// their accepted commands, borrowed leases, and correction fences finish or disposal aborts them.
// Lifecycle: discovery owns an unpublished candidate; publication makes it Current;
// Invalidate retires admission and starts draining; RemoveOwnedLocked ends ownership and
// balances the retired-generation gauge. Failed candidates skip publication and drain counting.
// Router disposal can end any phase. A replacement may be current while older generations drain,
// so these transitions belong to each generation rather than one router-wide state enum.
internal sealed class SentinelRouter(ClientCore core) : IAsyncDisposable
{
    private static long _retiredGenerationCount;
    internal static long RetiredGenerationCount => Interlocked.Read(ref _retiredGenerationCount);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SentinelDiscoveryState _discovery = new(core.Options.Endpoints.Count == 0
        ? [new RespireEndpoint("localhost", 26379)] : core.Options.Endpoints);
    private readonly HashSet<Generation> _owned = [];
    private readonly HashSet<DedicatedConnectionPool> _correctionPools = [];
    private readonly HashSet<RespireEndpoint> _monitoredSentinels = [];
    private readonly List<Task> _sentinelMonitors = [];
    private Generation? _current;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private Task _notifications = Task.CompletedTask;

    internal Generation? Current => Volatile.Read(ref _current);
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
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
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
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
        // An unexpected close retires a Sentinel generation, even when that multiplexer
        // could reconnect. Reconnecting the former primary alone cannot establish that it
        // is still the elected primary; discovery and ROLE validation select a new generation.
        if (Current is { IsRetired: false } current && current.Multiplexer.IsConnected) return current;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var acquired = false;
        Generation? unpublished = null;
        try
        {
            await _discoveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            // Another discovery owner may have published while this caller awaited the gate.
            var previous = Current;
            if (previous is { IsRetired: false } && previous.Multiplexer.IsConnected) return previous;
            if (previous is not null) Invalidate(previous);
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
                // Publication owns this measurement even if disposal suppresses later health
                // callbacks. Keep meter listeners outside discovery to permit observer disposal.
                if (old is not null && old.Endpoint != replacement.Endpoint)
                    QueueNotificationLocked(() => RespireTelemetry.SentinelFailovers.Add(1,
                        new KeyValuePair<string, object?>("server.address", replacement.Endpoint.Host),
                        new KeyValuePair<string, object?>("server.port", replacement.Endpoint.Port)), suppressAfterDisposal: false);
                QueueNotificationLocked(() => core.NotifySentinelPrimaryChanged(old?.Multiplexer, replacement.Multiplexer));
            }
            StartSentinelMonitors();
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
                    lock (_gate) RemoveOwnedLocked(unpublished);
                }
            }
            finally { if (acquired) _discoveryGate.Release(); }
        }
    }

    private void StartSentinelMonitors()
    {
        if (core.Options.DisableSentinelEventMonitoring) return;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var endpoint in _discovery.Snapshot())
                if (_monitoredSentinels.Add(endpoint))
                    _sentinelMonitors.Add(Task.Run(() => MonitorSentinelAsync(endpoint)));
        }
    }

    private async Task MonitorSentinelAsync(RespireEndpoint endpoint)
    {
        try
        {
            await MonitorSentinelCoreAsync(endpoint).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _monitoredSentinels.Remove(endpoint);
        }
    }

    private async Task MonitorSentinelCoreAsync(RespireEndpoint endpoint)
    {
        var attempts = 0;
        var options = core.Options with
        {
            Endpoints = new List<RespireEndpoint> { endpoint },
            UseCluster = false,
            SentinelPrimaryName = null,
            Username = core.Options.SentinelPassword is { Length: 0 } ? null
                : core.Options.SentinelUsername ?? core.Options.Username,
            Password = core.Options.SentinelPassword is { Length: 0 } ? null
                : core.Options.SentinelPassword ?? core.Options.Password,
            UseTls = core.Options.SentinelUseTls ?? core.Options.UseTls,
            TlsOptions = core.Options.SentinelTlsOptions ?? core.Options.TlsOptions,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ClientName = null,
            Database = 0,
            ClientSideCache = null,
        };

        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var client = await RespireClient.ConnectAsync(options, _lifetime.Token).ConfigureAwait(false);
                await using var subscription = await client.SubscribeAsync(
                    ["+switch-master", "+sdown", "+odown"], _lifetime.Token).ConfigureAwait(false);
                attempts = 0;
                await foreach (var message in subscription.WithCancellation(_lifetime.Token).ConfigureAwait(false))
                {
                    if (message.Kind == RespireMessageKind.Gap) continue;
                    if (message.Channel.ToString() == "+switch-master")
                    {
                        if (TryParseSwitchMasterEvent(message.Text, core.Options.SentinelPrimaryName!, out var newPrimary))
                            OnSentinelPrimaryChanged(endpoint, newPrimary);
                    }
                    else
                    {
                        var eventName = message.Channel.ToString();
                        var details = message.Text;
                        QueueSentinelDiagnostic(() => core.Logger?.LogDebug(
                            "Redis Sentinel {Event} at {Host}:{Port}: {Details}",
                            eventName, endpoint.Host, endpoint.Port, details));
                    }
                }
                if (!_lifetime.IsCancellationRequested)
                    throw new RespireConnectionException($"Sentinel event subscription ended at {endpoint}.");
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (_lifetime.IsCancellationRequested) return;
                attempts++;
                QueueSentinelDiagnostic(() => core.Logger?.LogWarning(error,
                    "Sentinel event monitor failed at {Endpoint}", endpoint));
                var policy = core.Options.ReconnectPolicy;
                if (policy?.IsExhausted(attempts) == true) return;
                var delay = policy?.GetDelay(attempts) ?? TimeSpan.FromSeconds(Math.Min(attempts, 30));
                try { await Task.Delay(delay, Clock, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            }
        }
    }

    private static bool TryParseSwitchMasterEvent(string? details, string expectedMaster, out RespireEndpoint newPrimary)
    {
        newPrimary = default;
        if (details is null) return false;
        var parts = details.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !string.Equals(parts[0], expectedMaster, StringComparison.Ordinal)
            || !int.TryParse(parts[4], out var port) || port is < 1 or > 65535) return false;
        newPrimary = new RespireEndpoint(parts[3], port);
        return true;
    }

    private void OnSentinelPrimaryChanged(RespireEndpoint sentinel, RespireEndpoint newPrimary)
    {
        Generation? current;
        lock (_gate)
        {
            current = Current;
            if (current is null || current.IsRetired) return;
            if (string.Equals(current.Endpoint.Host, newPrimary.Host, StringComparison.OrdinalIgnoreCase)
                && current.Endpoint.Port == newPrimary.Port) return;
            Invalidate(current);
        }
        _ = RefreshAfterSentinelEventAsync(sentinel);
    }

    private async Task RefreshAfterSentinelEventAsync(RespireEndpoint sentinel)
    {
        try { await GetGenerationAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            QueueSentinelDiagnostic(() => core.Logger?.LogDebug(error,
                "Sentinel primary refresh failed after an event from {Endpoint}", sentinel));
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
            lock (_gate) RemoveOwnedLocked(generation);
            throw;
        }
    }

    private void Invalidate(Generation generation, Exception? error = null)
    {
        lock (_gate)
        {
            // Retirement and its cleanup task become visible together to disposal. Once
            // disposal owns the router, it aborts every generation itself.
            if (!generation.TryRetire() || _disposed || !ReferenceEquals(Current, generation)) return;
            // Unpublished candidates are disposed by their discovery owner. Only the current
            // published generation can lose client continuity or need background draining.
            // The transport admission check sees retirement before any waiting caller resumes.
            // Cache invalidation is synchronous; metrics and health callbacks run elsewhere.
            generation.CountedAsRetired = true;
            Interlocked.Increment(ref _retiredGenerationCount);
            var evictions = core.ClientCache?.FlushForContinuityLossWithoutMetrics();
            if (evictions is { } count)
                QueueNotificationLocked(() => ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count));
            QueueNotificationLocked(() => core.NotifySentinelDisconnected(generation.Multiplexer, error));
            generation.Retirement = Task.Run(() => DrainAsync(generation));
        }
    }

    private void RemoveOwnedLocked(Generation generation)
    {
        if (!_owned.Remove(generation) || !generation.CountedAsRetired) return;
        generation.CountedAsRetired = false;
        Interlocked.Decrement(ref _retiredGenerationCount);
    }

    // Do not join this chain during disposal: an observer may synchronously dispose the
    // client itself. Queued callbacks are suppressed; an active callback may finish later.
    // Task.Run must keep user callbacks asynchronous: callers hold the publication gate.
    private void QueueNotificationLocked(Action notification, bool suppressAfterDisposal = true)
    {
        var previous = _notifications;
        _notifications = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (!suppressAfterDisposal || !core.Disposed) notification();
            }
            catch (Exception error)
            {
                try { core.Logger?.LogWarning(error, "Sentinel state observer failed"); }
                catch (Exception) { /* Keep later notifications independent of a user logger failure. */ }
            }
        });
    }

    private void QueueSentinelDiagnostic(Action diagnostic)
    {
        lock (_gate)
            if (!_disposed) QueueNotificationLocked(diagnostic);
    }

    private async Task DrainAsync(Generation generation)
    {
        var connectionsDrained = generation.StopConnections();
        var poolDrain = generation.Pool.RetireAsync().AsTask();
        try
        {
            try { await generation.Multiplexer.RetireAsync().ConfigureAwait(false); }
            catch (Exception error) when (generation.Multiplexer.RetirementDrained && !_lifetime.IsCancellationRequested)
            {
                try { core.Logger?.LogDebug(error, "Sentinel transport retirement reported an error after draining at {Endpoint}", generation.Endpoint); }
                catch (Exception) { /* Diagnostics must not abandon correction-fence cleanup. */ }
            }
            var delay = 1;
            long? lastWarning = null;
            while (generation.Multiplexer.HasPendingCorrectionFences && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await generation.Multiplexer.FenceRetiredConnectionsAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_lifetime.IsCancellationRequested)
                {
                    var now = Clock.GetTimestamp();
                    if (lastWarning is null || Clock.GetElapsedTime(lastWarning.Value, now) >= TimeSpan.FromMinutes(5))
                    {
                        lastWarning = now;
                        try { core.Logger?.LogWarning(error, "Sentinel generation at {Endpoint} retains an unacknowledged correction fence", generation.Endpoint); }
                        catch (Exception) { /* Logging must not abandon an owed fence. */ }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), Clock, _lifetime.Token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 30);
                }
            }
            await poolDrain.ConfigureAwait(false);
            await connectionsDrained.ConfigureAwait(false);
            lock (_gate) RemoveOwnedLocked(generation);
        }
        catch (Exception error)
        {
            try { await Task.WhenAll(poolDrain, connectionsDrained).ConfigureAwait(false); }
            catch (Exception poolError) { error = new AggregateException(error, poolError); }
            if (!_lifetime.IsCancellationRequested)
            {
                try { core.Logger?.LogWarning(error, "Sentinel generation cleanup failed at {Endpoint}; retained until client disposal", generation.Endpoint); }
                catch (Exception) { /* Ownership remains available to disposal even when logging fails. */ }
            }
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
            Task[] monitors;
            lock (_gate) monitors = _sentinelMonitors.ToArray();
            await Task.WhenAll(monitors).ConfigureAwait(false);
            await _discoveryGate.WaitAsync().ConfigureAwait(false);
            _discoveryGate.Release();
            Generation[] owned;
            DedicatedConnectionPool[] corrections;
            // Safe after releasing the discovery gate: ConnectGenerationAsync rechecks _disposed
            // under _gate before adding, so no generation can join _owned after this snapshot.
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
            catch (Exception error)
            {
                disposeError = disposeError is null ? error : new AggregateException(disposeError, error);
            }
            finally
            {
                // Disposal ends ownership even when transport cleanup faults. Clear the
                // retained-generation gauge before propagating any cleanup error.
                lock (_gate)
                {
                    foreach (var generation in owned) RemoveOwnedLocked(generation);
                    _correctionPools.Clear();
                }
            }
            if (disposeError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeError).Throw();
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
        internal bool CountedAsRetired; // Accessed only under the router gate.

        internal Generation(SentinelRouter owner, ClientCore core, RespireOptions options)
        {
            _owner = owner;
            Endpoint = options.PrimaryEndpoint;
            ConnectionOptions = options.ToConnectionOptions() with { Generation = this };
            RespirePushHandler? pushHandler = core.ClientCache is { } cache ? cache.HandlePush : null;
            var commandOptions = options.ToConnectionOptions(pushHandler,
                enableClientTracking: core.ClientCache is not null, enableMaintenanceNotifications: true) with
            {
                Generation = this,
                CredentialCacheInvalidation = core.ClientCache is { } clientCache
                    ? clientCache.FlushForContinuityLossWithoutMetrics
                    : null,
            };
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
                throw new RespireConnectionException($"Sentinel candidate at {Endpoint} did not confirm a valid primary ROLE.");
            }
            lock (_connectionsGate)
            {
                if (IsRetired || !connection.IsConnected)
                    throw new RespireConnectionException($"Sentinel candidate at {Endpoint} closed before validation completed.");
                _connections.Add(connection);
            }
        }

        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response)
        {
            // Ordinary collection reads cannot carry per-command errors. Avoid walking
            // their arrays again on the receive loop; only heterogeneous aggregates need it.
            var readOnly = response.IsError && ContainsReadOnly(in response);
            if (!readOnly && operation is "MULTI/EXEC" or "EXEC" or "EVAL" or "EVALSHA" or "EVAL_RO" or "EVALSHA_RO" or "FCALL" or "FCALL_RO")
                readOnly = ContainsReadOnly(in response);
            if (readOnly || operation == "ROLE" && !response.IsError && !IsPrimary(in response))
                _owner.Invalidate(this);
        }

        public void ConnectionClosed(RespireConnection connection, bool unexpected)
        {
            lock (_connectionsGate) _connections.Remove(connection);
            if (unexpected) _owner.Invalidate(this, connection.CloseError);
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
            if (reply.IsError) return IsReadOnlyError(in reply);
            if (reply.AsArray().IsEmpty) return false;
            // The parser accepts arbitrary nesting. Flat replies need no allocation;
            // Every aggregate exposes elements through AsArray; maps include keys and values.
            // Nested aggregates retain only the parents with unvisited siblings.
            Stack<(RespValue Parent, int NextIndex)>? parents = null;
            var current = reply;
            var index = 0;
            while (true)
            {
                var elements = current.AsArray();
                if (index == elements.Length)
                {
                    if (parents is null || !parents.TryPop(out var parent)) return false;
                    (current, index) = parent;
                    continue;
                }
                var element = elements[index++];
                if (element.IsError && IsReadOnlyError(in element)) return true;
                if (element.AsArray().IsEmpty) continue;
                if (index < elements.Length)
                    (parents ??= new()).Push((current, index));
                current = element;
                index = 0;
            }
        }

        private static bool IsReadOnlyError(in RespValue reply)
        {
            var error = reply.GetErrorMessage();
            return error == "READONLY" || error.StartsWith("READONLY ", StringComparison.Ordinal);
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
