using System.Security.Cryptography;
using System.Text;

namespace Respire.Streaming;

/// <summary>A message delivered by a reliable queue. Retain the receipt when acknowledging or rejecting it.</summary>
public sealed class RespireQueueMessage<T>
{
    internal RespireQueueMessage(RespireStreamId id, T payload, int deliveryCount, string receipt)
        => (Id, Payload, DeliveryCount, Receipt) = (id, payload, deliveryCount, receipt);

    /// <summary>The stable stream entry ID across deliveries.</summary>
    public RespireStreamId Id { get; }
    /// <summary>The deserialized message body.</summary>
    public T Payload { get; }
    /// <summary>The number of deliveries, including this one.</summary>
    public int DeliveryCount { get; }
    /// <summary>An opaque token valid only for this delivery and queue.</summary>
    public string Receipt { get; }
}

/// <summary>A typed, at-least-once message queue with visibility leases, delayed rejection and dead letters.</summary>
/// <remarks>Requires Redis 7+ or Valkey with scripting. The client is caller-owned, codecs must be thread-safe,
/// and all instances sharing a queue must use the same delivery limit and codecs. Reserve the generated key
/// namespace exclusively for this queue. Poll ReceiveAsync to drive redelivery and dead-letter movement.</remarks>
public sealed class RespireReliableQueue<T>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IRespireClient _client;
    private readonly Func<T, byte[]> _serialize;
    private readonly Func<byte[], T> _deserialize;
    private readonly int _deliveryLimit;
    private readonly string[] _keys;

    /// <summary>Creates a queue. Names must be nonblank and contain no braces; the delivery limit must be positive.</summary>
    public RespireReliableQueue(IRespireClient client, string queueName, Func<T, byte[]> serialize,
        Func<byte[], T> deserialize, int deliveryLimit = 5)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (queueName.Contains('{') || queueName.Contains('}'))
            throw new ArgumentException("Queue names cannot contain braces.", nameof(queueName));
        _ = StrictUtf8.GetByteCount(queueName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryLimit);
        StreamKey = "{respire-queue:" + queueName + "}:ready";
        DeadLetterStreamKey = StreamKey + ":dead";
        _keys = [StreamKey, StreamKey + ":due", StreamKey + ":receipts", StreamKey + ":attempts", DeadLetterStreamKey];
        JobQueueScheduling.ValidateKeys(client, _keys);
        (_client, _serialize, _deserialize, _deliveryLimit) = (client, serialize, deserialize, deliveryLimit);
    }

    /// <summary>The queue's stream key, before any client key prefix.</summary>
    public string StreamKey { get; }
    /// <summary>The retained dead-letter stream. Entries contain payload, source_id and delivery_count fields.</summary>
    public string DeadLetterStreamKey { get; }

    /// <summary>Sends a message, optionally deduplicating by application ID for a window (default one day).</summary>
    /// <remarks>Duplicates return the first entry ID without replacing its payload or extending the window,
    /// even after acknowledgement. Reuse the ID after ambiguous network outcomes. Without an ID each call
    /// appends a new message. Windows must be positive and at most Int32.MaxValue milliseconds.</remarks>
    public async ValueTask<RespireStreamId> SendAsync(T payload, string? deduplicationId = null,
        TimeSpan? deduplicationWindow = null, CancellationToken cancellationToken = default)
    {
        if (deduplicationId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationId);
        var window = deduplicationWindow ?? TimeSpan.FromDays(1);
        JobQueueScheduling.ValidateDuration(window, nameof(deduplicationWindow));
        cancellationToken.ThrowIfCancellationRequested();
        var dedupKey = StreamKey + ":dedup:" + (deduplicationId is null ? "unused"
            : Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(deduplicationId))));
        string[] keys = [StreamKey, dedupKey];
        JobQueueScheduling.ValidateKeys(_client, keys);
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The message serializer returned null.");
        using var reply = await _client.Scripts.ExecuteAsync(ReliableQueueScripts.Send, [.. keys],
            [bytes, deduplicationId is null ? 0 : 1, (long)Math.Ceiling(window.TotalMilliseconds)], cancellationToken).ConfigureAwait(false);
        return new(reply.AsString());
    }

    /// <summary>Receives up to max messages without blocking, recovering due messages before reading new ones.</summary>
    /// <remarks>Max is 1 through 1000. Visibility must be positive and at most Int32.MaxValue milliseconds.
    /// Uses server time. An empty batch can also mean this call moved exhausted messages to dead letters.
    /// A codec failure or lost response leaves the batch leased until expiry; processing must tolerate duplicates.</remarks>
    public async ValueTask<RespireQueueMessage<T>[]> ReceiveAsync(TimeSpan visibilityTimeout, int max = 1,
        CancellationToken cancellationToken = default)
    {
        JobQueueScheduling.ValidateDuration(visibilityTimeout, nameof(visibilityTimeout));
        if (max is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(max));
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = Guid.NewGuid().ToString("N");
        using var reply = await _client.Scripts.ExecuteAsync(ReliableQueueScripts.Receive, [.. _keys],
            [(long)Math.Ceiling(visibilityTimeout.TotalMilliseconds), max, _deliveryLimit, receipt], cancellationToken).ConfigureAwait(false);
        var messages = new RespireQueueMessage<T>[reply.Count];
        for (var i = 0; i < messages.Length; i++)
            messages[i] = new(new(reply[i][0].AsString()), _deserialize(reply[i][1].AsBytes()),
                checked((int)reply[i][2].AsInteger()), receipt);
        return messages;
    }

    /// <summary>Deletes a currently leased delivery. Returns false for expired, superseded or already completed receipts.</summary>
    public ValueTask<bool> AckAsync(RespireQueueMessage<T> message, CancellationToken cancellationToken = default)
        => CompleteAsync(message, null, cancellationToken);

    /// <summary>Revokes a delivery receipt and makes the message eligible after delay (zero means immediately).</summary>
    /// <remarks>Returns false for stale receipts. Delay must be nonnegative and at most Int32.MaxValue milliseconds.
    /// Exhausted messages move to dead letters when a receiver next processes their due time.</remarks>
    public ValueTask<bool> NackAsync(RespireQueueMessage<T> message, TimeSpan delay = default,
        CancellationToken cancellationToken = default)
    {
        JobQueueScheduling.ValidateDuration(delay, nameof(delay), allowZero: true);
        return CompleteAsync(message, delay, cancellationToken);
    }

    private async ValueTask<bool> CompleteAsync(RespireQueueMessage<T> message, TimeSpan? delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var reply = await _client.Scripts.ExecuteAsync(ReliableQueueScripts.Complete, [.. _keys],
            [message.Id.ToString(), message.Receipt, delay is null ? -1 : (long)Math.Ceiling(delay.Value.TotalMilliseconds)],
            cancellationToken).ConfigureAwait(false);
        return reply.AsInteger() == 1;
    }
}
