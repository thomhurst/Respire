using System.Runtime.CompilerServices;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

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
        var continuityFenced = false;
        try
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
                        evicted.BeginStop();
                        _entries.Remove(endpoint);
                        selected = null;
                    }
                    if (!_entries.TryGetValue(endpoint, out selected) && _entries.Count == MaximumTargets)
                    {
                        evicted = _entries.Values.Where(static entry => entry.Readers == 0)
                            .MinBy(static entry => entry.LastUse);
                        // A temporary, untracked send remains safe when every retained owner is busy.
                        if (evicted is null) return null;
                        evicted.BeginStop();
                        _entries.Remove(evicted.Endpoint);
                    }
                }
                // A newly seen destination can represent failover. Reusing a retained owner
                // keeps unrelated cached keys warm while its invalidation stream remains live.
                if (selected is null)
                {
                    cacheEvictions = _cache.FlushForContinuityLossWithoutMetrics();
                    continuityFenced = true;
                }
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
                    // Once selected, continuity belongs to the retained owner: admission
                    // fenced a new destination, and observed sockets fence established
                    // owners. Canceling or exhausting a waiter creates no new tracking gap.
                    continuityFenced = true;
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
        catch
        {
            // Failures before selecting an owner still need a fence. Selected-owner
            // waiters share its admission/socket fences, including during recovery.
            // Release selection before publishing metrics.
            if (!continuityFenced) _cache.FlushForContinuityLoss();
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
        private readonly ConditionalWeakTable<RespireConnection, Observation> _observations = new();
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

        internal void BeginStop()
        {
            // Join a running loss fence before the owner's retirement fence. Detaching
            // alone cannot stop a callback whose event invocation already captured it.
            lock (_observationGate)
            {
                _stopping = true;
                foreach (var observation in _observations)
                {
                    observation.Key.PendingCommandsFailing -= observation.Value.OnFailure;
                    observation.Value.Provenance.Forget();
                }
            }
        }

        internal async ValueTask DisposeAsync()
        {
            BeginStop();
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
                if (_stopping) return;
                if (!_observations.TryGetValue(connection, out var observation))
                {
                    // This synchronous fence is independent of queued lifecycle observers
                    // and reconnect policy exhaustion, and precedes pending reply completion.
                    observation = new Observation();
                    onFailure = () =>
                    {
                        // Lifetime/MOVING publication already fences the old route before
                        // retiring it, then fences overlapping fills after admission stops.
                        // Its eventual drain must not clear values from the new socket.
                        if (!connection.IsRetired) OnPendingCommandsFailing(observation);
                    };
                    // A paused acquisition can observe A again after replacement B.
                    // Keep A's original fence without retaining dead sockets indefinitely.
                    observation.OnFailure = onFailure;
                    _observations.Add(connection, observation);
                    connection.PendingCommandsFailing += onFailure;
                }
                else onFailure = observation.OnFailure;
            }
            // Subscription can race peer loss. The socket callback and this check share
            // one fence, including later observations of the same disconnected socket.
            if (!connection.IsConnected) onFailure();
        }

        private sealed class Observation
        {
            internal readonly ClientSideCacheCoordinator.TrackingProvenance Provenance = new();
            internal Action OnFailure = null!;
            internal bool Fenced;
        }

        internal void RegisterRead(RespireConnection connection, ClientSideCacheCoordinator.QueryRequest? read)
        {
            Observe(connection);
            lock (_observationGate)
            {
                // A reply cannot publish after the socket's one-shot fence. In particular,
                // an acquisition paused before observation cannot add keys after that fence.
                if (_stopping || !_observations.TryGetValue(connection, out var observation) || observation.Fenced)
                    throw new RespireConnectionException("Redirect tracking owner is no longer available.")
                    { Endpoint = Endpoint };
                observation.Provenance.Register(read);
            }
        }

        private void OnPendingCommandsFailing(Observation observation)
        {
            // Fence replies synchronously, but never invoke user instrumentation from a
            // socket/lifecycle callback: a listener can dispose and join that same socket.
            int removed;
            lock (_observationGate)
            {
                if (_stopping || observation.Fenced) return;
                observation.Fenced = true;
                removed = observation.Provenance.Fence(_cache);
            }
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
        internal void RegisterRead<TCommand>(string operation, in TCommand command)
            where TCommand : struct, IRespCommand
            => RegisterRead(ClientSideCacheCoordinator.TryCreateQuery(operation, in command, out var request)
                && request.Query.Operation == operation ? request : null);
        internal void RegisterRead(ClientSideCacheCoordinator.QueryRequest? read) => entry.RegisterRead(Connection, read);
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
                foreach (var entry in entries) entry.BeginStop();
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
