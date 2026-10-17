using Respire.Internal;
using Respire.Networking;

namespace Respire.Infrastructure;

internal sealed partial class RespireConnectionMultiplexer
{
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
        try
        {
            while (IsOperational && ReferenceEquals(original, Volatile.Read(ref _connections[slot])))
            {
                var remaining = original.RemainingConnectionLifetime!.Value;
                if (remaining > TimeSpan.Zero)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopConnecting.Token);
                    try
                    {
                        var delay = Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining,
                            _options.ConnectionLifetimeTimeProvider, delayCancellation.Token);
                        // Failed or superseded sockets must not retain a timer/worker until their
                        // old deadline, especially during repeated reconnects or maintenance moves.
                        if (await Task.WhenAny(delay, original.Closed).ConfigureAwait(false) == original.Closed) return;
                        await delay.ConfigureAwait(false);
                    }
                    finally { await delayCancellation.CancelAsync().ConfigureAwait(false); }
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
                    await Task.Delay(TimeSpan.FromSeconds(1), _options.ConnectionLifetimeTimeProvider,
                        _stopConnecting.Token).ConfigureAwait(false);
                    continue;
                }
                int? cacheEvictions;
                lock (_lifecycleGate)
                {
                    if (!IsOperational || !original.IsAcceptingCommands || !replacement.IsAcceptingCommands
                        || !ReferenceEquals(original, _connections[slot])
                        || !ReferenceEquals(endpoint, _activeEndpoint)) return;
                    lock (_moving.Gate)
                    {
                        cacheEvictions = _options.CredentialCacheInvalidation?.Invoke();
                        replacement.MultiplexerSlot = slot;
                        replacement.MovingPublicationGeneration = Interlocked.Increment(ref _movingPublicationGenerations[slot]);
                        replacement.Multiplexer = this;
                        Volatile.Write(ref _connections[slot], replacement);
                        published = true;
                    }
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
                    if (--_activeReconnects == 0) _reconnectsDrained?.TrySetResult();
                }
            }
        }
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
