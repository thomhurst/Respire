namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    private readonly Lock _observerGate = new();
    private Dictionary<RespireKey, RespireClientCacheInvalidationSubscription[]>? _invalidationObservers;
    private RespireClientCacheInvalidationSubscription[] _observerSnapshot = [];
    private bool _observersStopped;

    public IRespireClientCacheInvalidationSubscription SubscribeInvalidations(
        RespireKey key, Action<RespireClientCacheInvalidation> observer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        cancellationToken.ThrowIfCancellationRequested();
        key = key.Snapshot();
        if (!CanTrack(in key))
            throw new ArgumentException("The physical key is outside the configured broadcast prefixes.", nameof(key));
        var subscription = new RespireClientCacheInvalidationSubscription(this, key, observer);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(_observersStopped, this);
            var observers = _invalidationObservers ??= new();
            observers.TryGetValue(key, out var subscribers);
            observers[key] = subscribers is null ? [subscription] : [.. subscribers, subscription];
            Volatile.Write(ref _observerSnapshot, [.. _observerSnapshot, subscription]);
        }
        subscription.RegisterCancellation(cancellationToken);
        return subscription;
    }

    internal void RemoveInvalidationObserver(RespireClientCacheInvalidationSubscription subscription)
    {
        lock (_observerGate)
        {
            if (_invalidationObservers is not { } observers
                || !observers.TryGetValue(subscription.Key, out var subscribers)
                || Array.IndexOf(subscribers, subscription) < 0) return;
            if (subscribers.Length == 1) observers.Remove(subscription.Key);
            else observers[subscription.Key] = subscribers.Where(item => item != subscription).ToArray();
            Volatile.Write(ref _observerSnapshot, _observerSnapshot.Where(item => item != subscription).ToArray());
            if (_observerSnapshot.Length == 0) _invalidationObservers = null;
        }
    }

    private void PublishInvalidation(in RespireKey key, RespireClientCacheInvalidationReason reason)
    {
        // No locks, key copies, queue items, or delegates when no observer is registered.
        if (Volatile.Read(ref _observerSnapshot).Length == 0) return;
        RespireClientCacheInvalidationSubscription[]? subscribers;
        lock (_observerGate)
        {
            if (_invalidationObservers is not { } observers || !observers.TryGetValue(key, out subscribers)) return;
        }
        // Registration publishes immutable arrays. Enqueueing never holds the owner gate.
        foreach (var subscription in subscribers) subscription.Enqueue(reason);
    }

    private void PublishInvalidationForAll(RespireClientCacheInvalidationReason reason)
    {
        foreach (var subscription in Volatile.Read(ref _observerSnapshot)) subscription.Enqueue(reason);
    }

    internal void StopInvalidationObservers()
    {
        RespireClientCacheInvalidationSubscription[] subscriptions;
        lock (_observerGate)
        {
            _observersStopped = true;
            subscriptions = _observerSnapshot;
            _invalidationObservers = null;
            Volatile.Write(ref _observerSnapshot, []);
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
