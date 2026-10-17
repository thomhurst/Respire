using Respire.Internal;
using Respire.Networking;

namespace Respire.Infrastructure;

internal sealed partial class RespireConnectionMultiplexer
{
    // Lifecycle-gated; allocated only after the first lifetime publication.
    private HashSet<RespireConnection>? _lifetimeDrains;

    private void ReleaseLifetimeCompletionRunners()
    {
        RespireConnection[]? draining;
        lock (_lifecycleGate) draining = _lifetimeDrains?.ToArray();
        if (draining is null) return;
        // Re-enter retirement on the caller's stack to release an inline reply runner
        // before owner retirement/disposal joins the worker that drains that reply.
        foreach (var connection in draining) _ = connection.RetireAsync();
    }

    private void ScheduleConnectionLifetime(int slot, RespireConnection connection)
    {
        if (connection.LifetimeExpiresAt is null) return;
        lock (_lifecycleGate)
        {
            if (!IsOperational || !ReferenceEquals(connection, _connections[slot])) return;
            // Share reconnect ownership so retirement/disposal also joins unpublished sockets
            // and old lifetime sockets that are no longer in the slot array.
            _activeReconnects++;
        }
        // Publication can hold lifecycle gates. Never run connection hooks on that stack.
        _ = Task.Run(() => RecycleConnectionAsync(slot, connection));
    }

    private async Task RecycleConnectionAsync(int slot, RespireConnection original)
    {
        RespireConnection? replacement = null;
        var published = false;
        Task? drain = null;
        // A newly published connection gets a new worker and starts at this cap again.
        var retryCap = TimeSpan.FromSeconds(1);
        try
        {
            while (IsOperational && original.IsAcceptingCommands
                && ReferenceEquals(original, Volatile.Read(ref _connections[slot])))
            {
                var remaining = original.RemainingConnectionLifetime!.Value;
                if (remaining > TimeSpan.Zero)
                {
                    if (!await WaitForLifetimeDelayAsync(original,
                        remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining).ConfigureAwait(false)) return;
                    continue;
                }
                var endpoint = Volatile.Read(ref _activeEndpoint);
                try
                {
                    // Fresh ConnectAsync resolves DNS and repeats TLS, credentials, generation
                    // validation and tracking setup before any command can select this socket.
                    replacement = await ConnectMovingReplacementAsync(
                        new(endpoint.Host, endpoint.Port), _stopConnecting.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!_stopConnecting.IsCancellationRequested)
                {
                    // Planned recycling never removes a healthy socket on setup failure.
                    retryCap = await DelayLifetimeRetryAsync(original, retryCap).ConfigureAwait(false);
                    continue;
                }
                int? cacheEvictions = null;
                lock (_lifecycleGate)
                {
                    if (!IsOperational || !original.IsAcceptingCommands
                        || !ReferenceEquals(original, _connections[slot])
                        || !ReferenceEquals(endpoint, _activeEndpoint)) return;
                    if (replacement.IsAcceptingCommands)
                    {
                        lock (_moving.Gate)
                        {
                            cacheEvictions = _options.CredentialCacheInvalidation?.Invoke();
                            replacement.MultiplexerSlot = slot;
                            replacement.MovingPublicationGeneration = Interlocked.Increment(ref _movingPublicationGenerations[slot]);
                            replacement.Multiplexer = this;
                            (_lifetimeDrains ??= []).Add(original);
                            Volatile.Write(ref _connections[slot], replacement);
                            published = true;
                        }
                    }
                }
                if (!published)
                {
                    // Setup can succeed just before the peer closes. Preserve the healthy
                    // original's worker and retry rather than leaving it pinned indefinitely.
                    await replacement.DisposeAsync().ConfigureAwait(false);
                    replacement = null;
                    retryCap = await DelayLifetimeRetryAsync(original, retryCap).ConfigureAwait(false);
                    continue;
                }
                // Retire uses the write gate: a caller holding the old selection either has
                // already accepted its frame or is rerouted by the existing retired-socket path.
                original.RecordConnectionRecycle();
                drain = original.RetireAsync();
                ObservePublishedConnection(slot, replacement);
                replacement = null;
                // Fence cache fills that overlapped publication, as with maintenance handoffs.
                _options.CredentialCacheRetirementFence?.Invoke();
                if (cacheEvictions is { } removed)
                    ClientSideCacheCoordinator.PublishContinuityFlushMetrics(removed);
                return;
            }
        }
        catch (OperationCanceledException) when (_stopConnecting.IsCancellationRequested || _abortCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Never leak a fire-and-forget failure or make diagnostic observers part of ownership.
            RespireTelemetry.RecordError(error, internallyHandled: true);
        }
        finally
        {
            try
            {
                if (!published && replacement is not null)
                    await replacement.DisposeAsync().ConfigureAwait(false);
                if (published)
                {
                    await DrainRecycledConnectionAsync(original, drain ?? original.RetireAsync()).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                RespireTelemetry.RecordError(error, internallyHandled: true);
            }
            finally
            {
                lock (_lifecycleGate)
                {
                    _lifetimeDrains?.Remove(original);
                    if (--_activeReconnects == 0) _reconnectsDrained?.TrySetResult();
                }
            }
        }
    }

    private async Task<TimeSpan> DelayLifetimeRetryAsync(RespireConnection original, TimeSpan cap)
    {
        // Equal jitter keeps retries spread out without permitting an immediate retry loop.
        await WaitForLifetimeDelayAsync(original, cap * (0.5 + Random.Shared.NextDouble() * 0.5)).ConfigureAwait(false);
        return TimeSpan.FromSeconds(Math.Min(30, cap.TotalSeconds * 2));
    }

    private async Task<bool> WaitForLifetimeDelayAsync(RespireConnection connection, TimeSpan duration)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopConnecting.Token);
        try
        {
            var delay = Task.Delay(duration, _options.ConnectionLifetimeTimeProvider, cancellation.Token);
            // A closed or superseded socket must not retain a deadline or backoff timer.
            if (await Task.WhenAny(delay, connection.Closed).ConfigureAwait(false) == connection.Closed) return false;
            await delay.ConfigureAwait(false);
            return true;
        }
        finally { await cancellation.CancelAsync().ConfigureAwait(false); }
    }

    private async Task DrainRecycledConnectionAsync(RespireConnection connection, Task drain)
    {
        try
        {
            // No lifetime deadline aborts accepted work. Explicit disposal can.
            try { await drain.WaitAsync(_abortCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_abortCancellation.IsCancellationRequested) { }
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            finally { RecordRetiredConnectionIdentity(connection); }
        }
        await drain.ConfigureAwait(false);
    }
}
