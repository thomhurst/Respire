namespace Respire.Coordination;

/// <summary>Retries a coordination predicate after tracked-key invalidations, without polling.</summary>
internal static class RespireNotificationWaiter
{
    internal static readonly TimeSpan MaxExpiryWait = TimeSpan.FromMilliseconds(int.MaxValue);

    internal static async ValueTask<TResult> WaitAsync<TTarget, TResult>(
        IRespireClient client,
        RespireKey key,
        TTarget target,
        CancellationToken cancellationToken)
        where TTarget : struct, IRespireNotificationWaitTarget<TResult>
    {
        var cache = client.ClientSideCache
            ?? throw new RespireConfigurationException(
                "Notification-driven coordination waits require RESP3 client-side caching and tracking.");
        var physicalKey = client.ResolveKey(key).Snapshot();
        using var signal = new SemaphoreSlim(0, 1);
        using var subscription = cache.SubscribeInvalidations(physicalKey, invalidation =>
        {
            // Commands sent through this same client invalidate conservatively before Redis
            // confirms a write. Do not spin on our own failed acquisition attempt; server
            // invalidations and continuity loss still wake every waiter.
            if ((invalidation.Reasons & ~RespireClientCacheInvalidationReason.LocalMutation)
                == RespireClientCacheInvalidationReason.None) return;
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }, cancellationToken);
        if (!subscription.Stopped.CanBeCanceled)
            throw new NotSupportedException(
                "Coordination invalidation subscriptions must expose a cancellable Stopped token.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, subscription.Stopped);

        try
        {
            while (true)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                // Subscribe first, then perform a tracked read and the atomic ownership attempt.
                // An invalidation at any point before WaitAsync leaves one queued signal.
                await target.TrackAsync(linkedCancellation.Token).ConfigureAwait(false);
                var (succeeded, result) = await target.TryAsync(linkedCancellation.Token).ConfigureAwait(false);
                if (succeeded) return result;
                var ttl = await target.GetTimeToLiveAsync(linkedCancellation.Token).ConfigureAwait(false);
                if (!ttl.Exists) continue;
                if (ttl.TimeToLive is { } remaining)
                {
                    // Redis may defer expiry invalidations until it actively expires the key.
                    // Schedule one wake from PTTL; this is an expiry deadline, not polling.
                    // SemaphoreSlim rejects timeouts above Int32.MaxValue ms (~24.8 days). Waking at
                    // that bound only rechecks ownership and reschedules from the new PTTL.
                    var delay = remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1)
                        : remaining > MaxExpiryWait ? MaxExpiryWait : remaining;
                    _ = await signal.WaitAsync(delay, linkedCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    await signal.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && subscription.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(IRespireClientSideCache),
                "The client cache stopped its coordination invalidation subscription.");
        }
    }
}

internal interface IRespireNotificationWaitTarget<TResult>
{
    ValueTask TrackAsync(CancellationToken cancellationToken);
    ValueTask<RespireTtl> GetTimeToLiveAsync(CancellationToken cancellationToken);
    ValueTask<(bool Succeeded, TResult Result)> TryAsync(CancellationToken cancellationToken);
}
