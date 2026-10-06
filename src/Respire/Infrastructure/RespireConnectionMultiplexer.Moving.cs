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
/// Lock order: <c>_lifecycleGate</c> before the coordinator gate. No path may acquire
/// <c>_lifecycleGate</c> while it holds that gate. Code under the gate only
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

    private readonly MovingHandoffCoordinator _moving = new();

    private sealed record ActiveEndpoint(string Host, int Port);

    internal RespireEndpoint ActiveConnectionEndpoint
    {
        get
        {
            var endpoint = Volatile.Read(ref _activeEndpoint);
            return new(endpoint.Host, endpoint.Port);
        }
    }

    internal object MovingPublication => Volatile.Read(ref _activeEndpoint);

    internal (RespireEndpoint Endpoint, object Publication) CaptureMovingPublication()
    {
        var endpoint = Volatile.Read(ref _activeEndpoint);
        return (new(endpoint.Host, endpoint.Port), endpoint);
    }

    internal event Action? MovingHandoffPublished;

    internal MovingAnnouncement CaptureMovingAnnouncement(int slot, RespireConnection connection,
        MaintenanceNotification notification)
        => new(notification,
            ReferenceEquals(Volatile.Read(ref _connections[slot]), connection) ? connection.MovingPublicationGeneration : -1,
            _moving.HandoffEpoch,
            Environment.TickCount64);

    private void QueueMovingHandoff(int slot, RespireConnection connection, MovingAnnouncement announcement)
    {
        bool startWorker;
        lock (_moving.Gate)
        {
            startWorker = QueueMovingHandoffUnderLock(slot, connection, announcement);
        }

        // Fire-and-forget is safe: ProcessMovingHandoffsAsync catches every exception, and
        // retirement and disposal wait for it through the coordinator completion task.
        if (startWorker) _ = Task.Run(ProcessMovingHandoffsAsync);
    }

    internal void QueueDedicatedMovingHandoff(RespireConnection connection, MovingAnnouncement announcement,
        Func<bool> isCurrent)
    {
        // Dedicated uploads can be the only sockets a lazy client has opened. Their push
        // must drive the same handoff coordinator without joining the multiplexed write path.
        var epoch = _moving.HandoffEpoch;
        var current = isCurrent();
        bool startWorker;
        lock (_moving.Gate)
        {
            startWorker = QueueMovingHandoffUnderLock(-1, connection, announcement,
                current && _moving.HandoffEpoch - epoch <= 1);
        }
        if (startWorker) _ = Task.Run(ProcessMovingHandoffsAsync);
    }

    private bool QueueMovingHandoffUnderLock(int slot, RespireConnection connection, MovingAnnouncement announcement,
        bool? dedicatedCurrent = null)
    {
        var notification = announcement.Notification;
        // The socket must have been published when it parsed the push. A reconnect that replaced
        // it since then does not make the push stale: the announcing server is still the one the
        // client uses. One later handoff publication does not either, because the push may have
        // been parsed just before that swap and handled just after it, and the newer announcement
        // must still win. Two publications later the client has left that server behind.
        // Honor the advertised grace from the moment the push was parsed, so slow target setup
        // and a replay delayed by a sibling handshake both consume drain time.
        var grace = TimeSpan.FromSeconds(Math.Min(notification.Seconds ?? 5, MaxMovingGraceSeconds));
        var deadline = announcement.ReceivedAt + (long)grace.TotalMilliseconds;
        var result = _moving.Queue(IsOperational,
            dedicatedCurrent ?? _moving.IsCurrent(announcement.PublicationGeneration, connection.MovingPublicationGeneration, announcement.HandoffEpoch),
            connection.LastQueuedMovingSequence, connection.PeerKey, notification.SequenceId,
            notification.Target ?? new RespireEndpoint(Host, Port), deadline, Environment.TickCount64,
            _stopConnecting.Token);
        if (!result.MarkConnectionSequence) return false;
        // Mark every socket that handled this sequence. Publication sweep must not queue it later.
        connection.LastQueuedMovingSequence = notification.SequenceId;
        if (result.HasSupersededEndpoint)
        {
            var previous = result.SupersededEndpoint;
            _logger?.MovingSuperseded(notification.Target?.Host ?? Host, notification.Target?.Port ?? Port, previous.Host, previous.Port);
        }
        return result.StartWorker;
    }

    private async Task ProcessMovingHandoffsAsync()
    {
        while (true)
        {
            MovingHandoffCoordinator.Request? request;
            lock (_moving.Gate)
            {
                request = _moving.TakeNext(IsOperational);
            }
            if (request is null) return;
            try
            {
                await HandOffAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception error) when (IsOperational)
            {
                _logger?.MovingFailed(request.Endpoint.Host, request.Endpoint.Port, error);
            }
            catch (Exception error)
            {
                _logger?.MovingRetirementStopped(error);
            }
            finally
            {
                lock (_moving.Gate)
                {
                    _moving.Complete(request);
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
    private async Task HandOffAsync(MovingHandoffCoordinator.Request request)
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
                lock (_moving.Gate)
                {
                    if (_moving.HasPending) return;
                }
                var remaining = request.Deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    // Nothing was published, so keep the current sockets. They stay usable until
                    // the source closes them, and normal reconnect then takes over.
                    _logger?.MovingGraceAttemptFailed(endpoint.Host, endpoint.Port, error);
                    return;
                }
                _logger?.MovingRetry(endpoint.Host, endpoint.Port, error);
                var backoff = Math.Min(remaining, 50L << Math.Min(attempt, 4));
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), request.Cancellation.Token).ConfigureAwait(false);
            }
        }

        // A handshake that finishes after the grace period is still published: the source is
        // closing, and the announced target is the only endpoint left to serve new commands.
        // The drain below then has no time remaining and aborts the old sockets at once.
        // (Contrast the setup-failure path above, which has nothing to publish.)
        var old = new RespireConnection?[_connections.Length];
        List<RespireConnection>? handedOff = null;
        var published = false;
        int? cacheEvictions = null;
        try
        {
            lock (_lifecycleGate)
            {
                lock (_moving.Gate)
                {
                    // A MOVING parsed by a current socket whose callback has not run yet would be
                    // rejected after this publication. Queue it now, so it supersedes this request.
                    for (var i = 0; i < _connections.Length; i++)
                    {
                        if (Volatile.Read(ref _connections[i]) is { LastMovingAnnouncement: { } pendingAnnouncement } announcing)
                            _ = QueueMovingHandoffUnderLock(i, announcing, pendingAnnouncement);
                    }
                    if (!IsOperational || _moving.HasPending)
                    {
                        _logger?.MovingPublicationSuperseded(endpoint.Host, endpoint.Port);
                        return;
                    }
                    // Not user code: the cache flush only updates state and queues events, and its
                    // metrics are published after the gates are released (see below).
                    cacheEvictions = _options.CredentialCacheInvalidation?.Invoke();
                    Volatile.Write(ref _activeEndpoint, new ActiveEndpoint(endpoint.Host, endpoint.Port));
                    // Bump the epoch before any slot changes, so a MOVING parsed by a replacement
                    // after its publication is captured as current.
                    _moving.PublishHandoffEpoch();
                    for (var i = 0; i < replacements.Length; i++)
                    {
                        var generation = Interlocked.Increment(ref _movingPublicationGenerations[i]);
                        replacements[i].MultiplexerSlot = i;
                        replacements[i].MovingPublicationGeneration = generation;
                        replacements[i].Multiplexer = this;
                        old[i] = Interlocked.Exchange(ref _connections[i], replacements[i]);
                        // Snapshot liveness at publication. Retirement may close a live idle
                        // socket before metrics run, but already-dead slots are not handoffs.
                        if (old[i] is { IsConnected: true } live)
                            (handedOff ??= []).Add(live);
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
            _logger?.MovingPublishedLate(endpoint.Host, endpoint.Port, lateBy);
        }

        // Stop admission on the unpublished sockets before anything yields. RetireAsync takes
        // each socket's write gate, so it runs after the multiplexer locks are released.
        var retiredConnections = old.OfType<RespireConnection>().ToArray();
        var drains = retiredConnections.Select(connection => connection.RetireAsync()).ToArray();
        lock (_moving.Gate)
        {
            _moving.BeginDrain();
            _ = DrainMovedConnectionsInBackgroundAsync(old, drains, request.Deadline);
        }
        // The handoff has published, so neither the second cache fence nor a metrics observer
        // can fail it. The fence's metrics reach MeterListener callbacks synchronously.
        try { _options.CredentialCacheRetirementFence?.Invoke(); }
        catch (Exception error) { _logger?.MovingCacheFenceObserverFailed(error); }

        if (handedOff is not null)
            foreach (var connection in handedOff) connection.RecordConnectionHandoff();

        // Notify other connection owners and metrics listeners outside the lifecycle locks.
        MovingHandoffPublished?.Invoke();
        if (cacheEvictions is { } removed)
        {
            try { ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed); }
            catch (Exception error) { _logger?.ContinuityFlushObserverFailed(error); }
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
            _logger?.MovingSocketFenceFailed(error);
        }
        catch (Exception error)
        {
            _logger?.MovingSocketDrainStopped(error);
        }
        finally
        {
            ForgetMovingSequences(connectedOnly: false);
            lock (_moving.Gate)
            {
                _moving.EndDrain();
            }
        }
    }

    /// <summary>Completes when no old MOVING socket is still draining.</summary>
    private Task WaitForMovingDrainsAsync()
    {
        lock (_moving.Gate)
        {
            return _moving.WaitForDrains();
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
                {
                    var connection = await connect.ConfigureAwait(false);
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
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
        lock (_moving.Gate)
        {
            if (!_moving.HasSequenceFences) return;
            var live = new HashSet<(string Host, int Port)>();
            for (var slot = 0; slot < _connections.Length; slot++)
            {
                if (Volatile.Read(ref _connections[slot]) is { } published && (!connectedOnly || published.IsConnected))
                    live.Add(published.PeerKey);
            }
            _moving.ForgetSequences(live);
        }
    }

    /// <summary>
    /// Drains the unpublished sockets until the grace deadline and aborts any still busy. Every
    /// identity whose socket did not drain cleanly is fenced, whatever ended the drain.
    /// Disposal ends the drain at once, because the old sockets are no longer in
    /// <c>_connections</c> for the disposal loop to close.
    /// </summary>
    private async Task DrainMovedConnectionsAsync(RespireConnection?[] old, Task[] drains, long deadline)
    {
        try
        {
            var remainingMilliseconds = Math.Max(0, deadline - Environment.TickCount64);
            await Task.WhenAll(drains)
                .WaitAsync(TimeSpan.FromMilliseconds(remainingMilliseconds), _abortCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Any unclean exit, not only the grace deadline, aborts and later fences the old
            // sockets that did not drain; a faulted drain must not leave them open.
            if (error is TimeoutException)
                _logger?.MovingDrainGraceExceeded();
            else if (error is OperationCanceledException && _abortCancellation.IsCancellationRequested)
                _logger?.MovingDrainDisposalStopped();
            else
                _logger?.MovingDrainFailed(error);
            foreach (var connection in old)
            {
                if (connection is null || connection.DrainedSuccessfully) continue;
                RecordRetiredConnectionIdentity(connection);
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeError) { _logger?.MovingSocketAbortFailed(disposeError); }
            }
        }

        try { await Task.WhenAll(drains).ConfigureAwait(false); }
        catch (Exception error) { _logger?.MovingSocketDrainCleanupCompleted(error); }
        foreach (var connection in old) RecordRetiredConnectionIdentity(connection);
        if (HasPendingCorrectionFences)
            await FenceRetiredConnectionsAsync(_stopConnecting.Token).ConfigureAwait(false);
    }
}
