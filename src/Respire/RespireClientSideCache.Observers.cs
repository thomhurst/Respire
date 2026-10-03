namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    private readonly Lock _observerGate = new();
    private Dictionary<RespireKey, RespireClientCacheInvalidationSubscription[]>? _invalidationObservers;
    private bool _observersStopped;

    public IRespireClientCacheInvalidationSubscription SubscribeInvalidations(
        RespireKey key, Action<RespireClientCacheInvalidation> observer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        cancellationToken.ThrowIfCancellationRequested();
        key = key.Snapshot();
        if (!CanTrack(in key))
            throw new ArgumentException("The physical key is outside the configured client cache key prefixes.", nameof(key));
        var subscription = new RespireClientCacheInvalidationSubscription(this, key, observer);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(_observersStopped, this);
            // Published dictionaries and their arrays are immutable. Only registration
            // and removal copy them; invalidation never takes the registration gate.
            Dictionary<RespireKey, RespireClientCacheInvalidationSubscription[]> observers =
                _invalidationObservers is { } current ? new(current) : new();
            observers.TryGetValue(key, out var subscribers);
            observers[key] = subscribers is null ? [subscription] : [.. subscribers, subscription];
            Volatile.Write(ref _invalidationObservers, observers);
        }
        subscription.RegisterCancellation(cancellationToken);
        return subscription;
    }

    internal void RemoveInvalidationObserver(RespireClientCacheInvalidationSubscription subscription)
    {
        lock (_observerGate)
        {
            if (_invalidationObservers is not { } current
                || !current.TryGetValue(subscription.Key, out var subscribers)) return;
            var index = Array.IndexOf(subscribers, subscription);
            if (index < 0) return;
            if (current.Count == 1 && subscribers.Length == 1)
            {
                Volatile.Write(ref _invalidationObservers, null);
                return;
            }
            var observers = new Dictionary<RespireKey, RespireClientCacheInvalidationSubscription[]>(current);
            if (subscribers.Length == 1) observers.Remove(subscription.Key);
            else
            {
                var remaining = new RespireClientCacheInvalidationSubscription[subscribers.Length - 1];
                subscribers.AsSpan(0, index).CopyTo(remaining);
                subscribers.AsSpan(index + 1).CopyTo(remaining.AsSpan(index));
                observers[subscription.Key] = remaining;
            }
            Volatile.Write(ref _invalidationObservers, observers);
        }
    }

    private void PublishInvalidation(in RespireKey key, RespireClientCacheInvalidationReason reason)
    {
        // No locks, key copies, queue items, or delegates when no observer is registered.
        var observers = Volatile.Read(ref _invalidationObservers);
        if (observers is null || !observers.TryGetValue(key, out var subscribers)) return;
        foreach (var subscription in subscribers) subscription.Enqueue(reason);
    }

    private void PublishInvalidationForAll(RespireClientCacheInvalidationReason reason)
    {
        var observers = Volatile.Read(ref _invalidationObservers);
        if (observers is null) return;
        foreach (var entry in observers)
            foreach (var subscription in entry.Value) subscription.Enqueue(reason);
    }

    internal void StopInvalidationObservers()
    {
        Dictionary<RespireKey, RespireClientCacheInvalidationSubscription[]>? observers;
        lock (_observerGate)
        {
            _observersStopped = true;
            observers = _invalidationObservers;
            Volatile.Write(ref _invalidationObservers, null);
        }
        if (observers is null) return;
        foreach (var entry in observers)
            foreach (var subscription in entry.Value) subscription.DisposeFromOwner();
    }
}
