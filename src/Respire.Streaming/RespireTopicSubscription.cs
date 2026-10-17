using System.Runtime.CompilerServices;

namespace Respire.Streaming;

/// <summary>A leased delivery from a durable topic subscription.</summary>
public sealed class RespireTopicMessage<T>
{
    internal RespireTopicMessage(RespireStreamId id, T payload, int deliveryCount, string receipt)
        => (Id, Payload, DeliveryCount, Receipt) = (id, payload, deliveryCount, receipt);

    /// <summary>The retained source entry ID.</summary>
    public RespireStreamId Id { get; }
    /// <summary>The deserialized body.</summary>
    public T Payload { get; }
    /// <summary>The number of deliveries to this subscription, including this one.</summary>
    public int DeliveryCount { get; }
    /// <summary>An opaque token valid only for this delivery and subscription.</summary>
    public string Receipt { get; }
}

/// <summary>A named consumer group with visibility leases, delayed rejection, replay, and dead letters.</summary>
/// <remarks>Poll ReceiveAsync or ConsumeAsync to drive retries and dead letters. Delivery is at least once;
/// processing must tolerate duplicates. Do not mutate the group's pending list using raw stream commands.</remarks>
public sealed class RespireTopicSubscription<T>
{
    private readonly IRespireClient _client;
    private readonly Func<byte[], T> _deserialize;
    private readonly int _deliveryLimit;
    private readonly RespireKey[] _keys;

    internal RespireTopicSubscription(IRespireClient client, string streamKey, string name, string stateKey,
        Func<byte[], T> deserialize, int deliveryLimit)
    {
        (_client, StreamKey, Name, _deserialize, _deliveryLimit) = (client, streamKey, name, deserialize, deliveryLimit);
        DeadLetterStreamKey = stateKey + ":dead";
        string[] keys = [streamKey, stateKey + ":due", stateKey + ":receipts", stateKey + ":attempts", DeadLetterStreamKey];
        JobQueueScheduling.ValidateKeys(client, keys);
        _keys = [.. keys];
    }

    /// <summary>The server consumer group name.</summary>
    public string Name { get; }
    /// <summary>The retained source stream, before any client key prefix.</summary>
    public string StreamKey { get; }
    /// <summary>This subscription's dead-letter stream, with payload, source_id, delivery_count and subscription fields.</summary>
    public string DeadLetterStreamKey { get; }

    /// <summary>Receives up to max messages, recovering expired deliveries before reading new entries.</summary>
    /// <remarks>Max is 1 through 1000. A codec error or lost response leaves messages leased until expiry.
    /// An empty result may mean this call moved exhausted messages to dead letters or cleaned trimmed entries.</remarks>
    public async ValueTask<RespireTopicMessage<T>[]> ReceiveAsync(TimeSpan visibilityTimeout, int max = 1,
        CancellationToken cancellationToken = default)
    {
        JobQueueScheduling.ValidateDuration(visibilityTimeout, nameof(visibilityTimeout));
        if (max is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(max));
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = Guid.NewGuid().ToString("N");
        using var reply = await _client.Scripts.ExecuteAsync(ReliableTopicScripts.Receive, _keys,
            [Name, (long)Math.Ceiling(visibilityTimeout.TotalMilliseconds), max, _deliveryLimit, receipt],
            cancellationToken).ConfigureAwait(false);
        var messages = new RespireTopicMessage<T>[reply.Count];
        for (var i = 0; i < messages.Length; i++)
            messages[i] = new(new(reply[i][0].AsString()), _deserialize(reply[i][1].AsBytes()),
                checked((int)reply[i][2].AsInteger()), receipt);
        return messages;
    }

    /// <summary>Acknowledges a live receipt in this subscription without deleting retained history.</summary>
    public ValueTask<bool> AckAsync(RespireTopicMessage<T> message, CancellationToken cancellationToken = default)
        => CompleteAsync(message, null, cancellationToken);

    /// <summary>Revokes a live receipt and schedules redelivery after delay. Stale receipts return false.</summary>
    public ValueTask<bool> NackAsync(RespireTopicMessage<T> message, TimeSpan delay = default,
        CancellationToken cancellationToken = default)
    {
        JobQueueScheduling.ValidateDuration(delay, nameof(delay), allowZero: true);
        return CompleteAsync(message, delay, cancellationToken);
    }

    /// <summary>Moves the subscription cursor after position. Beginning replays retained history; New skips to now.</summary>
    /// <remarks>Fails while any delivery is pending, including delayed rejections. Stop consumers and acknowledge
    /// or exhaust pending deliveries before seeking. Seeking affects every consumer of this subscription.</remarks>
    public async ValueTask SeekAsync(RespireStreamId position, CancellationToken cancellationToken = default)
    {
        using var reply = await _client.Scripts.ExecuteAsync(ReliableTopicScripts.Seek, [StreamKey],
            [Name, position.ToString()], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays retained entries at or after timestamp, subject to the same pending-delivery guard as SeekAsync.</summary>
    public ValueTask SeekAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        => SeekAsync(new RespireStreamId(RespireReliableTopic<T>.TimestampCursor(timestamp)), cancellationToken);

    /// <summary>Polls for deliveries until cancellation. The caller must explicitly acknowledge or reject each message.</summary>
    /// <remarks>Poll interval defaults to 100 milliseconds. No background task or client ownership is created.</remarks>
    public async IAsyncEnumerable<RespireTopicMessage<T>> ConsumeAsync(TimeSpan visibilityTimeout, int batchSize = 1,
        TimeSpan? pollInterval = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        JobQueueScheduling.ValidateDuration(interval, nameof(pollInterval));
        interval = TimeSpan.FromMilliseconds(Math.Ceiling(interval.TotalMilliseconds));
        while (true)
        {
            var messages = await ReceiveAsync(visibilityTimeout, batchSize, cancellationToken).ConfigureAwait(false);
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }
            if (messages.Length == 0) await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> CompleteAsync(RespireTopicMessage<T> message, TimeSpan? delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var reply = await _client.Scripts.ExecuteAsync(ReliableTopicScripts.Complete, _keys,
            [Name, message.Id.ToString(), message.Receipt, delay is null ? -1 : (long)Math.Ceiling(delay.Value.TotalMilliseconds)],
            cancellationToken).ConfigureAwait(false);
        return reply.AsInteger() == 1;
    }
}
