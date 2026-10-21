using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>Owns tracking sockets beyond a redirected reply's lifetime.</summary>
internal sealed class ReplicaRedirectTargets(ClientCore core) : IAsyncDisposable
{
    internal const int MaximumTargets = 8;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _selection = new(1, 1);
    private readonly Dictionary<RespireEndpoint, Entry> _entries = new(RespireEndpointComparer.Instance);
    private readonly ClientSideCacheCoordinator _cache = core.ClientCache!;
    private bool _disposed;
    private long _nextUse;

    internal int Count { get { lock (_gate) return _entries.Count; } }
    internal RespireEndpoint[] GetOpenEndpoints()
    {
        lock (_gate) return _entries.Keys.ToArray();
    }

    // Serialize new destinations and eviction, including socket cleanup, so closing owners
    // cannot grow without bound. Established owners still multiplex concurrent reads.
    internal async ValueTask<Lease?> AcquireAsync(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        await _selection.WaitAsync(cancellationToken).ConfigureAwait(false);
        Entry? selected = null;
        int? cacheEvictions = null;
        try
        {
            Entry? evicted = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || core.Disposed, this);
                if (_entries.TryGetValue(endpoint, out selected) && selected.Readers == 0
                    && selected.RecoveryExhausted)
                {
                    evicted = selected;
                    _entries.Remove(endpoint);
                    selected = null;
                }
                if (!_entries.TryGetValue(endpoint, out selected) && _entries.Count == MaximumTargets)
                {
                    evicted = _entries.Values.Where(static entry => entry.Readers == 0)
                        .MinBy(static entry => entry.LastUse);
                    // A temporary, untracked send remains safe when every retained owner is busy.
                    if (evicted is null) return null;
                    _entries.Remove(evicted.Endpoint);
                }
            }
            // A newly seen destination can represent failover. Reusing a retained owner
            // keeps unrelated cached keys warm while its invalidation stream remains live.
            if (selected is null) cacheEvictions = core.ClientCache!.FlushForContinuityLossWithoutMetrics();
            if (evicted is not null)
            {
                // Fence both published values and fills before giving up invalidation delivery.
                await evicted.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed || core.Disposed, this);
                if (selected is null)
                {
                    selected = new Entry(core, endpoint);
                    _entries.Add(endpoint, selected);
                }
                selected.Readers++;
                selected.LastUse = ++_nextUse;
            }
        }
        finally { _selection.Release(); }

        var lease = new Lease(this, selected);
        try
        {
            // Metrics listeners may synchronously dispose the client; selection owns no
            // gate here, and the lease already guarantees rollback if an observer throws.
            if (cacheEvictions is { } removed) ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed);
            await selected.EnsureConnectedAsync(core, cancellationToken).ConfigureAwait(false);
            lease.UseConnection(await selected.Node.GetHealthyConnectionAsync(cancellationToken).ConfigureAwait(false));
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal sealed class Entry
    {
        internal readonly RespireEndpoint Endpoint;
        internal readonly RespireConnectionMultiplexer Node;
        internal int Readers;
        internal long LastUse;
        private readonly Lock _observationGate = new();
        private readonly SemaphoreSlim _initialization = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private static long _nextRecoveryEpisode;
        private RespireConnection? _observed;
        private Action? _observedFailure;
        private readonly ClientSideCacheCoordinator _cache;
        private volatile bool _stopping;
        internal bool RecoveryExhausted
        {
            get
            {
                try { Node.ThrowIfRecoveryExhausted(); return false; }
                catch (RespireReconnectLimitException) { return true; }
            }
        }

        internal async ValueTask EnsureConnectedAsync(ClientCore core, CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            try
            {
                await _initialization.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    var attempt = 0;
                    var episode = 0L;
                    for (;;)
                    {
                        try
                        {
                            await Node.EnsureConnectedAsync(lifetime.Token, armHandshakeDeadline: false).ConfigureAwait(false);
                            if (attempt != 0) Notify(RespireConnectionState.Connected, null);
                            return;
                        }
                        catch (Exception error) when (core.Options.ReconnectPolicy is { } policy
                            && !lifetime.IsCancellationRequested && DedicatedConnectionPool.IsRetryableAcquisitionFailure(error))
                        {
                            // These are acquisition retries, before application dispatch, just
                            // like the temporary redirect pool. They do not spend RetryPolicy.
                            if (episode == 0) episode = Interlocked.Increment(ref _nextRecoveryEpisode);
                            if (policy.IsExhausted(attempt))
                            {
                                Notify(RespireConnectionState.Disconnected, error, exhausted: true);
                                throw new RespireReconnectLimitException(
                                    $"Redirect target acquisition to {Endpoint} exhausted {attempt} replacement attempts.", error)
                                { Endpoint = Endpoint };
                            }
                            if (attempt < int.MaxValue) attempt++;
                            var delay = policy.GetDelay(attempt);
                            Notify(RespireConnectionState.Reconnecting, error, delay);
                            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                        }
                    }

                    void Notify(RespireConnectionState state, Exception? error, TimeSpan? delay = null, bool exhausted = false)
                        => core.QueueRecoveryStateChanged(new(Endpoint, state, error)
                        {
                            ReconnectSource = RespireReconnectSource.Dedicated, SourceState = state,
                            ReconnectAttempt = attempt, ReconnectEpisodeId = episode,
                            NextReconnectDelay = delay, ReconnectExhausted = exhausted,
                        });
                }
                finally { _initialization.Release(); }
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, lifetime.Token))
            {
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
        }

        internal async ValueTask DisposeAsync()
        {
            // Planned closes are already fenced by the owner, with metrics deferred until
            // selection is released. Do not run user instrumentation from socket disposal.
            _stopping = true;
            lock (_observationGate)
            {
                if (_observed is { } connection) connection.PendingCommandsFailing -= _observedFailure;
            }
            await _stop.CancelAsync().ConfigureAwait(false);
            await Node.DisposeAsync().ConfigureAwait(false);
            // Join any backoff or unpublished initial handshake, not only published sockets.
            await _initialization.WaitAsync().ConfigureAwait(false);
            _initialization.Release();
        }

        internal void Observe(RespireConnection connection)
        {
            Action onFailure;
            lock (_observationGate)
            {
                if (!ReferenceEquals(_observed, connection))
                {
                    // This synchronous fence is independent of queued lifecycle observers
                    // and reconnect policy exhaustion, and precedes pending reply completion.
                    var fenced = 0;
                    _observedFailure = () =>
                    {
                        if (Interlocked.Exchange(ref fenced, 1) == 0) OnPendingCommandsFailing();
                    };
                    connection.PendingCommandsFailing += _observedFailure;
                    _observed = connection;
                }
                onFailure = _observedFailure!;
            }
            // Subscription can race peer loss. The socket callback and this check share
            // one fence, including later observations of the same disconnected socket.
            if (!connection.IsConnected) onFailure();
        }

        private void OnPendingCommandsFailing()
        {
            if (_stopping) return;
            // Fence replies synchronously, but never invoke user instrumentation from a
            // socket/lifecycle callback: a listener can dispose and join that same socket.
            var removed = _cache.FlushForContinuityLossWithoutMetrics();
            ThreadPool.UnsafeQueueUserWorkItem(static count =>
            {
                try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(count); }
                catch (Exception) { /* Listener failures must not escape the diagnostic worker. */ }
            }, removed, preferLocal: false);
        }

        internal Entry(ClientCore core, RespireEndpoint endpoint)
        {
            Endpoint = endpoint;
            var cache = core.ClientCache!;
            _cache = cache;
            var options = core.CreateConnectionOptions(cache.HandlePush, enableClientTracking: true,
                enableMaintenanceNotifications: !endpoint.IsUnixSocket, enableReplicaRedirects: true) with
            {
                CredentialCacheInvalidation = cache.FlushForContinuityLossWithoutMetrics,
                CredentialCacheRetirementFence = cache.FlushForMovingRetirementFence,
            };
            // Create is lazy: ownership must be registered before asynchronous connection setup.
            Node = RespireConnectionMultiplexer.Create(endpoint.Host, endpoint.Port, 1, options, core.Logger);
            Node.SlotStateChanged += (_, change) =>
            {
                if (_stopping) return;
                // PendingCommandsFailing owns the synchronous fence. Slot transitions
                // can repeat for that same socket as recovery progresses or exhausts.
                // Optional destinations report their own recovery without participating
                // in aggregate command connection health.
                core.QueueRecoveryStateChanged(change with { ReconnectSource = RespireReconnectSource.Dedicated });
            };
        }
    }

    internal sealed class Lease(ReplicaRedirectTargets owner, Entry entry) : IDisposable
    {
        private int _returned;
        internal RespireConnection Connection { get; set; } = null!;
        internal void UseConnection(RespireConnection connection)
        {
            Connection = connection;
            entry.Observe(connection);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0) return;
            lock (owner._gate) entry.Readers--;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        // Join an eviction already in progress, and prevent any later owner publication.
        await _selection.WaitAsync().ConfigureAwait(false);
        Entry[] entries;
        try
        {
            lock (_gate)
            {
                entries = _entries.Values.ToArray();
                _entries.Clear();
            }
        }
        finally { _selection.Release(); }
        var removed = _cache.FlushForContinuityLossWithoutMetrics();
        try
        {
            await CleanupTasks.WhenAllAsync(entries.Select(static entry => entry.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally
        {
            try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed); }
            catch (Exception) { /* Instrumentation must not replace cleanup failures. */ }
        }
    }
}
