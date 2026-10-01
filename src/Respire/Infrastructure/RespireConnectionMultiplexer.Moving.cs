using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Infrastructure;

/// <summary>
/// A MOVING push as seen by the socket that parsed it. Eligibility and receipt time are fixed at
/// parse time, because the multiplexer may handle the push later.
/// </summary>
/// <param name="Notification">The parsed push.</param>
/// <param name="PublicationGeneration">The socket's slot publication generation when the push was
/// parsed, or -1 when the socket was not published in its slot at that moment.</param>
/// <param name="HandoffEpoch">The number of MOVING handoffs the multiplexer had published when the
/// push was parsed.</param>
/// <param name="ReceivedAt">The <see cref="Environment.TickCount64"/> value at parse time. The
/// advertised grace period starts here.</param>
internal sealed record MovingAnnouncement(
    MaintenanceNotification Notification, long PublicationGeneration, long HandoffEpoch, long ReceivedAt);

/// <summary>
/// Proactive MOVING handoff: connect and validate every replacement, publish them together,
/// then drain the old sockets in the background within the advertised grace period.
/// </summary>
/// <remarks>
/// Lock order: <c>_lifecycleGate</c> before <c>_movingGate</c>. No path may acquire
/// <c>_lifecycleGate</c> while it holds <c>_movingGate</c>. Code under <c>_movingGate</c> only
/// updates handoff state and cancels superseded requests; it never awaits, never calls user code
/// and never takes another gate. (A cancelled request's worker unwinds without taking
/// <c>_lifecycleGate</c>, so even a continuation inlined by that cancellation keeps the order.)
/// </remarks>
internal sealed partial class RespireConnectionMultiplexer
{
    // The protocol allows any non-negative grace. This cap only keeps tick arithmetic finite.
    // A long grace keeps old sockets open only while accepted commands are still running; a
    // socket that drains cleanly closes at once, and command timeouts bound the rest.
    private const long MaxMovingGraceSeconds = 24 * 60 * 60;

    private readonly object _movingGate = new();
    // Highest MOVING sequence seen from each physical peer, and when that fence lapses. Sequence
    // IDs belong to the announcing server. A fence lapses at the end of the grace period it
    // announced, so a server that restarts at the same address and numbers from 1 again is
    // only ignored until then. Copies of one MOVING on sibling sockets arrive well before.
    private readonly Dictionary<(string Host, int Port), (long Sequence, long ExpiresAt)> _movingSequences = new();
    // Bumped under both gates whenever a handoff publishes replacements. A MOVING parsed two or
    // more handoff publications ago describes a server the client has already left.
    private long _movingHandoffEpoch;
    private MovingRequest? _pendingMoving;
    private MovingRequest? _activeMoving;
    // True while a worker owns the queue. It is set and cleared together with _movingCompletion.
    private bool _movingWorker;
    private TaskCompletionSource? _movingCompletion;
    // Old sockets drain off the handoff worker so a later MOVING can start immediately.
    private Task _movingDrains = Task.CompletedTask;

    private sealed record ActiveEndpoint(string Host, int Port);

    private sealed record MovingRequest(RespireEndpoint Endpoint, long Deadline, CancellationTokenSource Cancellation);

    internal MovingAnnouncement CaptureMovingAnnouncement(int slot, RespireConnection connection,
        MaintenanceNotification notification)
        => new(notification,
            ReferenceEquals(Volatile.Read(ref _connections[slot]), connection) ? connection.MovingPublicationGeneration : -1,
            Volatile.Read(ref _movingHandoffEpoch),
            Environment.TickCount64);

    private void QueueMovingHandoff(int slot, RespireConnection connection, MovingAnnouncement announcement)
    {
        bool startWorker;
        lock (_movingGate)
        {
            startWorker = QueueMovingHandoffUnderLock(slot, connection, announcement);
        }

        // Fire-and-forget is safe: ProcessMovingHandoffsAsync catches every exception, and
        // retirement and disposal wait for it through _movingCompletion.
        if (startWorker) _ = Task.Run(ProcessMovingHandoffsAsync);
    }

    private bool QueueMovingHandoffUnderLock(int slot, RespireConnection connection, MovingAnnouncement announcement)
    {
        var notification = announcement.Notification;
        // The socket must have been published when it parsed the push. A reconnect that replaced
        // it since then does not make the push stale: the announcing server is still the one the
        // client uses. One later handoff publication does not either, because the push may have
        // been parsed just before that swap and handled just after it, and the newer announcement
        // must still win. Two publications later the client has left that server behind.
        var current = announcement.PublicationGeneration >= 0
            && announcement.PublicationGeneration == connection.MovingPublicationGeneration
            && Volatile.Read(ref _movingHandoffEpoch) - announcement.HandoffEpoch <= 1;
        if (!IsOperational || !current || notification.SequenceId <= connection.LastQueuedMovingSequence)
            return false;
        // From here on this socket has handled the sequence, even when a sibling already queued
        // it; the pre-publication sweep in HandOffAsync must not queue it again later.
        connection.LastQueuedMovingSequence = notification.SequenceId;
        var peer = connection.PeerKey;
        if (_movingSequences.TryGetValue(peer, out var seen) && notification.SequenceId <= seen.Sequence
            && Environment.TickCount64 < seen.ExpiresAt)
            return false;

        // Honor the advertised grace from the moment the push was parsed, so slow target setup
        // and a replay delayed by a sibling handshake both consume drain time.
        var grace = TimeSpan.FromSeconds(Math.Min(notification.Seconds ?? 5, MaxMovingGraceSeconds));
        var deadline = announcement.ReceivedAt + (long)grace.TotalMilliseconds;
        _movingSequences[peer] = (notification.SequenceId, deadline);

        // The newest MOVING wins. Cancelling a request that has already published is harmless:
        // HandOffAsync makes no cancellation checks after publication, and its drain runs on its
        // own deadline.
        if (_activeMoving is { } active && !active.Cancellation.IsCancellationRequested)
        {
            _logger?.LogDebug("MOVING to {Host}:{Port} supersedes the handoff to {PreviousHost}:{PreviousPort}",
                notification.Target?.Host ?? Host, notification.Target?.Port ?? Port,
                active.Endpoint.Host, active.Endpoint.Port);
            active.Cancellation.Cancel();
        }
        if (_pendingMoving is { } pending)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopConnecting.Token);
        _pendingMoving = new MovingRequest(notification.Target ?? new RespireEndpoint(Host, Port),
            deadline, requestCancellation);
        if (_movingWorker) return false;
        _movingWorker = true;
        _movingCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    private async Task ProcessMovingHandoffsAsync()
    {
        while (true)
        {
            MovingRequest request;
            lock (_movingGate)
            {
                if (!IsOperational || _pendingMoving is null)
                {
                    if (_pendingMoving is { } pending)
                    {
                        pending.Cancellation.Cancel();
                        pending.Cancellation.Dispose();
                    }
                    _pendingMoving = null;
                    _activeMoving = null;
                    _movingWorker = false;
                    _movingCompletion?.TrySetResult();
                    _movingCompletion = null;
                    return;
                }
                request = _pendingMoving;
                _pendingMoving = null;
                _activeMoving = request;
            }
            try
            {
                await HandOffAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception error) when (IsOperational)
            {
                _logger?.LogWarning(error, "MOVING handoff to {Host}:{Port} failed",
                    request.Endpoint.Host, request.Endpoint.Port);
            }
            catch (Exception error)
            {
                _logger?.LogDebug(error, "MOVING handoff stopped by multiplexer retirement");
            }
            finally
            {
                lock (_movingGate)
                {
                    if (ReferenceEquals(_activeMoving, request)) _activeMoving = null;
                    request.Cancellation.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Connects every replacement before publishing any, retrying until the advertised grace
    /// period ends. A newer MOVING supersedes this one without publishing its sockets.
    /// </summary>
    /// <remarks>
    /// A newer MOVING that arrives after the replacements connect discards them and connects
    /// again for the newer target. That costs one extra handshake per superseded request, which
    /// is acceptable because MOVING is rare and a burst still ends with the newest target.
    /// </remarks>
    private async Task HandOffAsync(MovingRequest request)
    {
        var endpoint = request.Endpoint;
        RespireConnection[] replacements;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Replacements use the multiplexer's options. Under Sentinel that includes the
                // generation, so each target is still validated as the primary before publication;
                // a target that fails validation is retried like any other setup failure.
                replacements = await ConnectMovingReplacementsAsync(endpoint, request.Cancellation.Token).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsOperational)
            {
                lock (_movingGate)
                {
                    if (_pendingMoving is not null) return;
                }
                var remaining = request.Deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    // Nothing was published, so keep the current sockets. They stay usable until
                    // the source closes them, and normal reconnect then takes over.
                    _logger?.LogWarning(error,
                        "MOVING handoff to {Host}:{Port} failed within its grace period; keeping current connections",
                        endpoint.Host, endpoint.Port);
                    return;
                }
                _logger?.LogDebug(error, "MOVING handoff to {Host}:{Port} failed; retrying", endpoint.Host, endpoint.Port);
                var backoff = Math.Min(remaining, 50L << Math.Min(attempt, 4));
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), request.Cancellation.Token).ConfigureAwait(false);
            }
        }

        // A handshake that finishes after the grace period is still published: the source is
        // closing, and the announced target is the only endpoint left to serve new commands.
        // The drain below then has no time remaining and aborts the old sockets at once.
        // (Contrast the setup-failure path above, which has nothing to publish.)
        var old = new RespireConnection?[_connections.Length];
        var published = false;
        int? cacheEvictions = null;
        try
        {
            lock (_lifecycleGate)
            {
                lock (_movingGate)
                {
                    // A MOVING parsed by a current socket whose callback has not run yet would be
                    // rejected after this publication. Queue it now, so it supersedes this request.
                    for (var i = 0; i < _connections.Length; i++)
                    {
                        if (Volatile.Read(ref _connections[i]) is { LastMovingAnnouncement: { } pendingAnnouncement } announcing)
                            _ = QueueMovingHandoffUnderLock(i, announcing, pendingAnnouncement);
                    }
                    if (!IsOperational || _pendingMoving is not null)
                    {
                        _logger?.LogDebug("MOVING handoff to {Host}:{Port} superseded before publication",
                            endpoint.Host, endpoint.Port);
                        return;
                    }
                    // Not user code: the cache flush only updates state and queues events, and its
                    // metrics are published after the gates are released (see below).
                    cacheEvictions = _options.CredentialCacheInvalidation?.Invoke();
                    Volatile.Write(ref _activeEndpoint, new ActiveEndpoint(endpoint.Host, endpoint.Port));
                    // Bump the epoch before any slot changes, so a MOVING parsed by a replacement
                    // after its publication is captured as current.
                    Interlocked.Increment(ref _movingHandoffEpoch);
                    for (var i = 0; i < replacements.Length; i++)
                    {
                        var generation = Interlocked.Increment(ref _movingPublicationGenerations[i]);
                        replacements[i].MultiplexerSlot = i;
                        replacements[i].MovingPublicationGeneration = generation;
                        replacements[i].Multiplexer = this;
                        old[i] = Interlocked.Exchange(ref _connections[i], replacements[i]);
                        // Failure history belongs to the previous endpoint's sockets.
                        if (_reconnectAttempts is not null) _reconnectAttempts[i] = 0;
                    }
                    published = true;
                }
                for (var i = 0; i < replacements.Length; i++)
                    ObservePublishedConnection(i, replacements[i]);
            }
        }
        finally
        {
            if (!published)
            {
                foreach (var replacement in replacements)
                    await replacement.DisposeAsync().ConfigureAwait(false);
            }
        }

        var lateBy = Environment.TickCount64 - request.Deadline;
        if (lateBy > 0)
        {
            _logger?.LogWarning(
                "MOVING handoff to {Host}:{Port} published {LateMilliseconds} ms after its grace period; aborting old sockets, so commands they had accepted may fail",
                endpoint.Host, endpoint.Port, lateBy);
        }

        // Stop admission on the unpublished sockets before anything yields. RetireAsync takes
        // each socket's write gate, so it runs after the multiplexer locks are released.
        var retiredConnections = old.OfType<RespireConnection>().ToArray();
        var drains = retiredConnections.Select(connection => connection.RetireAsync()).ToArray();
        lock (_movingGate)
        {
            var drain = DrainMovedConnectionsInBackgroundAsync(old, drains, request.Deadline);
            _movingDrains = _movingDrains.IsCompleted ? drain : Task.WhenAll(_movingDrains, drain);
        }
        _options.CredentialCacheRetirementFence?.Invoke();

        // Metrics listeners can run user code, so publish outside the lifecycle locks.
        if (cacheEvictions is { } removed)
        {
            try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed); }
            catch (Exception error) { _logger?.LogDebug(error, "Continuity flush metrics observer failed"); }
        }
    }

    private async Task DrainMovedConnectionsInBackgroundAsync(RespireConnection?[] old, Task[] drains, long deadline)
    {
        await Task.Yield(); // Never run drain work under the handoff gate.
        try
        {
            await DrainMovedConnectionsAsync(old, drains, deadline).ConfigureAwait(false);
        }
        catch (Exception error) when (IsOperational)
        {
            _logger?.LogWarning(error, "Fencing old MOVING sockets failed");
        }
        catch (Exception error)
        {
            _logger?.LogDebug(error, "Old MOVING socket drain stopped by multiplexer retirement");
        }
        finally
        {
            ForgetMovingSequences(connectedOnly: false);
        }
    }

    private async Task<RespireConnection[]> ConnectMovingReplacementsAsync(
        RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        var connects = new Task<RespireConnection>[_connections.Length];
        for (var i = 0; i < connects.Length; i++)
            connects[i] = ConnectMovingReplacementAsync(endpoint, cancellationToken);
        try
        {
            return await Task.WhenAll(connects).ConfigureAwait(false);
        }
        catch
        {
            // Task.WhenAll completes only after every connect has finished, so each successful
            // socket is visible here and none is left to finish unobserved.
            foreach (var connect in connects)
            {
                if (connect.Status == TaskStatus.RanToCompletion)
                    await connect.Result.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<RespireConnection> ConnectMovingReplacementAsync(
        RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        var connection = await RespireConnection.ConnectAsync(endpoint.Host, endpoint.Port,
            _options, _logger, cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _trackServerClientIds) != 0)
                await connection.EnsureServerClientIdAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Drops sequence fences for peers that no published socket reaches. With
    /// <paramref name="connectedOnly"/>, a published socket that has already died does not count.
    /// </summary>
    private void ForgetMovingSequences(bool connectedOnly)
    {
        lock (_movingGate)
        {
            if (_movingSequences.Count == 0) return;
            var live = new HashSet<(string Host, int Port)>();
            for (var slot = 0; slot < _connections.Length; slot++)
            {
                if (Volatile.Read(ref _connections[slot]) is { } published && (!connectedOnly || published.IsConnected))
                    live.Add(published.PeerKey);
            }
            foreach (var peer in _movingSequences.Keys.ToArray())
            {
                if (!live.Contains(peer)) _movingSequences.Remove(peer);
            }
        }
    }

    /// <summary>
    /// Drains the unpublished sockets until the grace deadline and aborts any still busy. Every
    /// identity whose socket did not drain cleanly is fenced, whatever ended the drain.
    /// </summary>
    private async Task DrainMovedConnectionsAsync(RespireConnection?[] old, Task[] drains, long deadline)
    {
        try
        {
            var remainingMilliseconds = Math.Max(0, deadline - Environment.TickCount64);
            await Task.WhenAll(drains).WaitAsync(TimeSpan.FromMilliseconds(remainingMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Any unclean exit, not only the grace deadline, aborts and later fences the old
            // sockets that did not drain; a faulted drain must not leave them open.
            if (error is TimeoutException)
                _logger?.LogWarning("MOVING handoff drain exceeded its advertised grace period; aborting remaining old sockets");
            else
                _logger?.LogWarning(error, "MOVING handoff drain of old sockets failed; aborting remaining old sockets");
            foreach (var connection in old)
            {
                if (connection is null || connection.DrainedSuccessfully) continue;
                RetireConnection(connection);
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeError) { _logger?.LogDebug(disposeError, "Aborting an old MOVING socket failed"); }
            }
        }

        try { await Task.WhenAll(drains).ConfigureAwait(false); }
        catch (Exception error) { _logger?.LogDebug(error, "Old MOVING sockets completed after drain cleanup"); }
        foreach (var connection in old) RetireConnection(connection);
        if (HasPendingCorrectionFences)
            await FenceRetiredConnectionsAsync(_stopConnecting.Token).ConfigureAwait(false);
    }
}
