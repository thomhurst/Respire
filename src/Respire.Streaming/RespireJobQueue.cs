using System.Security.Cryptography;
using System.Text;
using Respire.Internal;

namespace Respire.Streaming;

/// <summary>A typed, immediate job producer backed by a Redis stream and expiring deduplication keys.</summary>
/// <remarks>The client is caller-owned. Enqueue deduplication does not make processing exactly once;
/// handlers must tolerate redelivery. Reserve this queue's generated key namespace for its exclusive use.</remarks>
public sealed class RespireJobQueue<T>
{
    /// <summary>The consumer group shared by all hosted workers for a queue.</summary>
    public const string ConsumerGroup = "respire-jobs";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly RespireScript Enqueue = RespireScript.Create("""
        local existing = redis.call('GET', KEYS[2])
        if existing then return {existing, 0} end
        local id = redis.call('XADD', KEYS[1], '*', '_respire.job.id', ARGV[1], '_respire.job.payload', ARGV[2])
        redis.call('SET', KEYS[2], id, 'PX', ARGV[3])
        return {id, 1}
        """);
    private readonly IRespireClient _client;
    private readonly Func<T, byte[]> _serialize;

    /// <summary>Creates a producer with an explicit, thread-safe serializer, including source-generated JSON codecs.</summary>
    /// <param name="client">The caller-owned Redis client or key-prefixed view.</param>
    /// <param name="queueName">A nonblank queue name without braces. Each name has its own Redis Cluster hash tag.</param>
    /// <param name="serialize">Serializes a job payload to owned bytes; can be called concurrently.</param>
    public RespireJobQueue(IRespireClient client, string queueName, Func<T, byte[]> serialize)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serialize);
        StreamKey = GetStreamKey(queueName);
        DeadLetterStreamKey = GetDeadLetterStreamKey(queueName);
        _client = client;
        _serialize = serialize;
    }

    /// <summary>The logical stream key, before any client key prefix is applied.</summary>
    public string StreamKey { get; }

    /// <summary>The default logical dead-letter stream key. Worker options can override it with a same-slot destination.</summary>
    public string DeadLetterStreamKey { get; }

    /// <summary>Enqueues a job once within a window, returning the original entry ID on duplicate submissions.</summary>
    /// <remarks>The window starts at the first successful enqueue and is not extended by duplicates.
    /// It defaults to one day and must be positive and at most Int32.MaxValue milliseconds.
    /// Reuse the same job ID after an ambiguous network outcome. Duplicate submissions preserve the first payload,
    /// even after that job completes. The window expires only deduplication metadata, never the job itself.</remarks>
    public async ValueTask<RespireJobEnqueueResult> EnqueueAsync(string jobId, T payload,
        TimeSpan? deduplicationWindow = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var window = deduplicationWindow ?? TimeSpan.FromDays(1);
        if (window <= TimeSpan.Zero || window > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(deduplicationWindow));
        cancellationToken.ThrowIfCancellationRequested();
        // Hash the ID so user-supplied braces and arbitrarily long IDs cannot change the Redis key's hash tag.
        var deduplicationKey = StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(jobId)));
        var streamKey = _client.ResolveKey(StreamKey).ToBytes();
        var deduplicationKeyBytes = _client.ResolveKey(deduplicationKey).ToBytes();
        if (streamKey.AsSpan().SequenceEqual(deduplicationKeyBytes)
            || ClusterHash.GetSlot(streamKey) != ClusterHash.GetSlot(deduplicationKeyBytes))
            throw new ArgumentException("Queue keys must resolve to distinct keys in the same Cluster slot.");
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        using var result = await _client.Scripts.ExecuteAsync(Enqueue, [StreamKey, deduplicationKey],
            [jobId, bytes, (long)Math.Ceiling(window.TotalMilliseconds)], cancellationToken).ConfigureAwait(false);
        return new(new(result[0].AsString()), result[1].AsInteger() == 1);
    }

    internal static string GetStreamKey(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (queueName.Contains('{') || queueName.Contains('}'))
            throw new ArgumentException("Queue names cannot contain braces.", nameof(queueName));
        _ = StrictUtf8.GetByteCount(queueName);
        return "{respire-job:" + queueName + "}:ready";
    }

    internal static string GetDeadLetterStreamKey(string queueName)
        => GetStreamKey(queueName) + ":dead";

    internal static RespireJob<T> Deserialize(RespireStreamEntry entry, Func<byte[], T> deserialize)
    {
        var id = entry.GetString("_respire.job.id");
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException("The stream entry has no job ID.");
        var payload = entry["_respire.job.payload"] ?? throw new FormatException("The stream entry has no job payload.");
        return new(id, deserialize(payload), entry.Id);
    }
}
