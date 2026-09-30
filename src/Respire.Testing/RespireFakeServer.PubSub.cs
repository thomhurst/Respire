namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    // Command dispatch invokes these handlers synchronously while holding _gate.
    private readonly Dictionary<byte[], HashSet<Connection>> _subscribers = new(BinaryKeyComparer.Instance);

    private static FakeReply Ping(Connection connection, byte[][] args)
    {
        // Redis RESP2 subscribed mode uses the two-element pong frame, even without a payload.
        if (!connection.Resp3 && connection.Channels.Count != 0)
            return FakeReply.Array([FakeReply.Text("pong"), FakeReply.Bulk(args.Length == 1 ? [] : args[1])]);
        return args.Length == 1 ? FakeReply.Simple("PONG") : FakeReply.Bulk(args[1]);
    }

    private FakeReply Subscribe(Connection connection, byte[][] args)
    {
        var replies = new FakeReply[args.Length - 1];
        for (var index = 1; index < args.Length; index++)
        {
            var channel = args[index];
            if (connection.Channels.Add(channel))
            {
                if (!_subscribers.TryGetValue(channel, out var listeners))
                    _subscribers[channel] = listeners = [];
                listeners.Add(connection);
            }
            replies[index - 1] = SubscriptionReply("subscribe", channel, connection.Channels.Count);
        }
        return FakeReply.Sequence(replies);
    }

    private FakeReply Unsubscribe(Connection connection, byte[][] args)
    {
        var channels = args.Length == 1 ? connection.Channels.ToArray() : args[1..];
        if (channels.Length == 0) return SubscriptionReply("unsubscribe", null, 0);
        var replies = new FakeReply[channels.Length];
        for (var index = 0; index < channels.Length; index++)
        {
            RemoveSubscriptionLocked(connection, channels[index]);
            replies[index] = SubscriptionReply("unsubscribe", channels[index], connection.Channels.Count);
        }
        return FakeReply.Sequence(replies);
    }

    private static FakeReply SubscriptionReply(string kind, byte[]? channel, int count)
        => FakeReply.Push([FakeReply.Text(kind), FakeReply.Bulk(channel), FakeReply.Integer(count)]);

    private FakeReply Publish(byte[][] args)
    {
        if (!_subscribers.TryGetValue(args[1], out var listeners)) return FakeReply.Integer(0);
        byte[]? resp2 = null, resp3 = null;
        var message = FakeReply.Push([FakeReply.Text("message"), FakeReply.Bulk(args[1]), FakeReply.Bulk(args[2])]);
        var receivers = 0;
        // Overflow may remove a slow connection from this route while we publish.
        foreach (var listener in listeners.ToArray())
        {
            if (listener.Closed || listener.Lifetime.IsCancellationRequested) continue;
            // Count the active route even if this publication triggers its output limit.
            // As with Redis, a receiver count is not an acknowledgement of delivery.
            receivers++;
            var bytes = listener.Resp3 ? resp3 ??= message.Encode(true) : resp2 ??= message.Encode(false);
            QueueOutputLocked(listener, bytes, push: true)?.Ready.TrySetResult();
        }
        return FakeReply.Integer(receivers);
    }

    private void RemoveSubscriptionLocked(Connection connection, byte[] channel)
    {
        if (!connection.Channels.Remove(channel) || !_subscribers.TryGetValue(channel, out var listeners)) return;
        listeners.Remove(connection);
        if (listeners.Count == 0) _subscribers.Remove(channel);
    }

    private void RemoveSubscriptionsLocked(Connection connection)
    {
        foreach (var channel in connection.Channels.ToArray()) RemoveSubscriptionLocked(connection, channel);
    }
}
