namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    private readonly Lock _observerGate = new();
    private Dictionary<RespireKey, List<RespireClientCacheInvalidationSubscription>>? _invalidationObservers;
    private int _observerCount;
    private bool _observersStopped;

    public RespireClientCacheInvalidationSubscription SubscribeInvalidations(
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
            if (!observers.TryGetValue(key, out var subscribers)) observers.Add(key, subscribers = []);
            subscribers.Add(subscription);
            Volatile.Write(ref _observerCount, _observerCount + 1);
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
                || !subscribers.Remove(subscription)) return;
            if (subscribers.Count == 0) observers.Remove(subscription.Key);
            Volatile.Write(ref _observerCount, _observerCount - 1);
            if (_observerCount == 0) _invalidationObservers = null;
        }
    }

    private void PublishInvalidation(in RespireKey key, RespireClientCacheInvalidationReason reason)
    {
        // No locks, key copies, queue items, or delegates when no observer is registered.
        if (Volatile.Read(ref _observerCount) == 0) return;
        lock (_observerGate)
        {
            if (_invalidationObservers is { } observers && observers.TryGetValue(key, out var subscribers))
                foreach (var subscription in subscribers) subscription.Enqueue(reason);
        }
    }

    private void PublishInvalidationForAll(RespireClientCacheInvalidationReason reason)
    {
        if (Volatile.Read(ref _observerCount) == 0) return;
        lock (_observerGate)
        {
            if (_invalidationObservers is not { } observers) return;
            foreach (var subscribers in observers.Values)
                foreach (var subscription in subscribers) subscription.Enqueue(reason);
        }
    }

    internal void StopInvalidationObservers()
    {
        RespireClientCacheInvalidationSubscription[] subscriptions;
        lock (_observerGate)
        {
            _observersStopped = true;
            if (_invalidationObservers is not { } observers) return;
            subscriptions = observers.Values.SelectMany(static subscribers => subscribers).ToArray();
            _invalidationObservers = null;
            Volatile.Write(ref _observerCount, 0);
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
