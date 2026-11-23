using StackExchange.Redis;

#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

internal sealed partial class CompatSubscriber(RespireConnectionMultiplexer owner) : ISubscriber
{
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly Dictionary<RedisChannel, Registration> _subscriptions = [];

    private sealed record Registration(RespireSubscription Subscription, List<Action<RedisChannel, RedisValue>> Handlers)
    {
        public void ClearHandlers()
        {
            lock (Handlers)
            {
                Handlers.Clear();
            }
        }
    }

    public IConnectionMultiplexer Multiplexer => owner;

    private static RedisChannel SnapshotChannel(RedisChannel channel, CommandFlags flags)
    {
        byte[]? bytes = channel;

        if (bytes is null)
        {
            throw new ArgumentException("Channel cannot be null.", nameof(channel));
        }

        if (channel.IsPattern)
        {
            throw Compatibility.Unsupported("pattern subscriptions; only literal channels are supported");
        }

        if (flags != CommandFlags.None)
        {
            throw Compatibility.Unsupported($"ISubscriber CommandFlags {flags}; use None");
        }

        return new RedisChannel(bytes.ToArray(), RedisChannel.PatternMode.Literal);
    }

    public Task SubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None)
    {
        channel = SnapshotChannel(channel, flags);
        ArgumentNullException.ThrowIfNull(handler);
        var client = owner.DefaultDatabase.Client;
        return owner.Run(async token =>
        {
            await _dispatch.WaitAsync(token).ConfigureAwait(false);

            try
            {
                if (_subscriptions.TryGetValue(channel, out var existing))
                {
                    if (!existing.Subscription.IsDisposed)
                    {
                        lock (existing.Handlers)
                        {
                            if (!existing.Handlers.Contains(handler))
                            {
                                existing.Handlers.Add(handler);
                            }
                        }

                        return true;
                    }
                    // Terminal native subscriptions cannot deliver; retry native admission so its failure is not hidden.
                    _subscriptions.Remove(channel);

                    existing.ClearHandlers();
                }

                byte[]? bytes = channel;
                var subscription = await client.SubscribeAsync(new RespireChannel(bytes!.AsMemory()), cancellationToken: token).ConfigureAwait(false);
                var registration = new Registration(subscription, [handler]);
                _subscriptions.Add(channel, registration);
                // Never invoke a callback inline while subscription admission holds the dispatch gate.
                _ = Task.Run(() => DeliverAsync(channel, registration));
                return true;
            }
            finally { _dispatch.Release(); }
        });
    }

    private static async Task DeliverAsync(RedisChannel channel, Registration registration)
    {
        try
        {
            await foreach (var message in ((IAsyncEnumerable<RespireMessage>)registration.Subscription).ConfigureAwait(false))
            {
                if (message.Kind == RespireMessageKind.Gap)
                {
                    continue;
                }

                Action<RedisChannel, RedisValue>[] handlers;

                lock (registration.Handlers)
                {
                    handlers = registration.Handlers.ToArray();
                }

                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(channel, message.Payload.ToArray());
                    }
                    catch { /* Like StackExchange.Redis, callback exceptions do not end delivery. */ }
                }
            }
        }
        catch (RespireException) { /* Native reconnect exhaustion ends the stream. */ }
        catch (ObjectDisposedException) { /* Adapter shutdown disposes the subscription. */ }
    }

    public void Subscribe(RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags flags = CommandFlags.None)
        => owner.Wait(SubscribeAsync(channel, handler, flags));

    public ChannelMessageQueue Subscribe(RedisChannel channel, CommandFlags flags = CommandFlags.None)
        => throw Compatibility.Unsupported("ChannelMessageQueue; use callback Subscribe");

    public Task<ChannelMessageQueue> SubscribeAsync(RedisChannel channel, CommandFlags flags = CommandFlags.None)
        => throw Compatibility.Unsupported("ChannelMessageQueue; use callback SubscribeAsync");

    public Task UnsubscribeAsync(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None)
    {
        channel = SnapshotChannel(channel, flags);
        return owner.Run(async token =>
        {
            await _dispatch.WaitAsync(token).ConfigureAwait(false);

            try
            {
                if (!_subscriptions.TryGetValue(channel, out var registration))
                {
                    return true;
                }

                if (handler is not null)
                {
                    lock (registration.Handlers)
                    {
                        if (registration.Handlers.Count > 1)
                        {
                            registration.Handlers.Remove(handler);
                            return true;
                        }

                        if (!registration.Handlers.Contains(handler))
                        {
                            return true;
                        }
                    }
                }

                await DisposeRegistrationAsync(channel, registration).ConfigureAwait(false);
                return true;
            }
            finally { _dispatch.Release(); }
        });
    }

    public void Unsubscribe(RedisChannel channel, Action<RedisChannel, RedisValue>? handler = null, CommandFlags flags = CommandFlags.None)
        => owner.Wait(UnsubscribeAsync(channel, handler, flags));

    public Task UnsubscribeAllAsync(CommandFlags flags = CommandFlags.None)
    {
        if (flags != CommandFlags.None)
        {
            throw Compatibility.Unsupported($"ISubscriber CommandFlags {flags}");
        }

        return owner.Run(async token =>
        {
            await DisposeSubscriptionsAsync(closing: false, token).ConfigureAwait(false);
            return true;
        });
    }

    public void UnsubscribeAll(CommandFlags flags = CommandFlags.None) => owner.Wait(UnsubscribeAllAsync(flags));

    internal async Task DisposeSubscriptionsAsync(bool closing, CancellationToken token = default)
    {
        await _dispatch.WaitAsync(token).ConfigureAwait(false);

        try
        {
            foreach (var (channel, registration) in _subscriptions.ToArray())
            {
                await DisposeRegistrationAsync(channel, registration).ConfigureAwait(false);
            }
        }
        finally
        {
            if (closing)
            {
                // A closed adapter cannot retry. Detach callbacks from any subscriptions still live on a borrowed client.
                foreach (var registration in _subscriptions.Values)
                {
                    registration.ClearHandlers();
                }

                _subscriptions.Clear();
            }

            _dispatch.Release();
        }
    }

    private async ValueTask DisposeRegistrationAsync(RedisChannel channel, Registration registration)
    {
        try
        {
            await registration.Subscription.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // A command hook can reject disposal before the native subscription ends.
            if (registration.Subscription.IsDisposed)
            {
                _subscriptions.Remove(channel);
                registration.ClearHandlers();
            }
        }
    }

    public Task<long> PublishAsync(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        => owner.DefaultDatabase.PublishAsync(channel, message, flags);

    public long Publish(RedisChannel channel, RedisValue message, CommandFlags flags = CommandFlags.None)
        => owner.Wait(PublishAsync(channel, message, flags));

    public void Wait(Task task) => owner.Wait(task);

    public T Wait<T>(Task<T> task) => owner.Wait(task);

    public void WaitAll(params Task[] tasks) => owner.WaitAll(tasks);

    public bool TryWait(Task task)
    {
        try
        {
            Wait(task);
            return true;
        }
        catch (TimeoutException) { return false; }
    }
}
