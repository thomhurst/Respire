using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Respire.Streaming;

/// <summary>A retained, typed stream with independent durable subscriptions.</summary>
/// <remarks>Requires Redis 7+ or Valkey with scripting. The client is caller-owned. Reserve the generated
/// key namespace for this topic. Codecs must be thread-safe and identical across producers and consumers.
/// Retention is a hard bound: it can remove messages before an offline subscription receives them.</remarks>
public sealed class RespireReliableTopic<T>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IRespireClient _client;
    private readonly Func<T, byte[]> _serialize;
    private readonly Func<byte[], T> _deserialize;
    private readonly int _deliveryLimit;
    private readonly int? _maximumLength;
    private readonly TimeSpan? _maximumAge;

    /// <summary>Creates a topic. Choose at most one retention bound; null means unlimited retention.</summary>
    public RespireReliableTopic(IRespireClient client, string topicName, Func<T, byte[]> serialize,
        Func<byte[], T> deserialize, int deliveryLimit = 5, int? maximumLength = null, TimeSpan? maximumAge = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        if (topicName.Contains('{') || topicName.Contains('}'))
            throw new ArgumentException("Topic names cannot contain braces.", nameof(topicName));
        _ = StrictUtf8.GetByteCount(topicName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deliveryLimit);
        if (maximumLength is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (maximumAge is { } age) JobQueueScheduling.ValidateDuration(age, nameof(maximumAge));
        if (maximumLength.HasValue && maximumAge.HasValue)
            throw new ArgumentException("Choose length or age retention, not both.");
        StreamKey = "{respire-topic:" + topicName + "}:messages";
        (_client, _serialize, _deserialize, _deliveryLimit, _maximumLength, _maximumAge) =
            (client, serialize, deserialize, deliveryLimit, maximumLength, maximumAge);
    }

    /// <summary>The retained source stream, before any client key prefix.</summary>
    public string StreamKey { get; }

    /// <summary>Appends a message and applies exact length or age retention using server time.</summary>
    /// <remarks>Age retention runs on publication, not on a background timer. A retry after a lost reply
    /// can publish a duplicate. Acknowledgement never deletes retained source entries.</remarks>
    public async ValueTask<RespireStreamId> PublishAsync(T payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The message serializer returned null.");
        using var reply = await _client.Scripts.ExecuteAsync(ReliableTopicScripts.Publish, [StreamKey],
            [bytes, _maximumLength ?? 0, _maximumAge is { } age ? (long)Math.Ceiling(age.TotalMilliseconds) : 0],
            cancellationToken).ConfigureAwait(false);
        return new(reply.AsString());
    }

    /// <summary>Opens a named durable subscription. Existing subscriptions keep their cursor.</summary>
    /// <remarks>The default start is the beginning of retained history. New means future publications only.
    /// Instances with the same subscription name compete for messages and must use the same delivery limit.
    /// Creating the subscription before disconnecting preserves its position on the server.</remarks>
    public async ValueTask<RespireTopicSubscription<T>> SubscribeAsync(string subscriptionName,
        RespireStreamId? start = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        cancellationToken.ThrowIfCancellationRequested();
        var suffix = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(subscriptionName)));
        var subscription = new RespireTopicSubscription<T>(_client, StreamKey, subscriptionName,
            StreamKey + ":subscription:" + suffix, _deserialize, _deliveryLimit);
        await _client.Streams.CreateGroupAsync(StreamKey, subscriptionName, start ?? RespireStreamId.Beginning,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return subscription;
    }

    internal static string TimestampCursor(DateTimeOffset timestamp)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds, nameof(timestamp));
        // Group reads are exclusive. The previous millisecond includes every entry at the requested time.
        return milliseconds == 0 ? "0-0" : (milliseconds - 1).ToString(CultureInfo.InvariantCulture) + "-18446744073709551615";
    }
}
