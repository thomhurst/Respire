using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using Respire.Internal;

namespace Respire.Streaming;

/// <summary>A typed job producer backed by Redis streams and expiring deduplication keys.</summary>
/// <remarks>The client is caller-owned. Enqueue deduplication does not make processing exactly once;
/// handlers must tolerate redelivery. Reserve this queue's generated key namespace for its exclusive use.
/// Requires Redis 7.0 or later, script execution, GET/SET on deduplication keys, and XADD/XDEL on the stream.</remarks>
public sealed partial class RespireJobQueue<T>
{
    /// <summary>The consumer group shared by all hosted workers for a queue.</summary>
    public const string ConsumerGroup = "respire-jobs";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly RespireScript Enqueue = RespireScript.Create("""
        local existing = redis.call('GET', KEYS[2])
        if existing then return {existing, 0} end
        -- Lua errors do not roll back writes. Check both mutations and rollback before appending.
        if not redis.acl_check_cmd then
            return redis.error_reply('ERR job enqueue requires Redis 7.0 or later')
        end
        local fields = {'_respire.job.id', ARGV[1], '_respire.job.payload', ARGV[2]}
        if ARGV[4] and ARGV[4] ~= '' then
            table.insert(fields, 'traceparent'); table.insert(fields, ARGV[4])
            if ARGV[5] and ARGV[5] ~= '' then table.insert(fields, 'tracestate'); table.insert(fields, ARGV[5]) end
        end
        if not redis.acl_check_cmd('XADD', KEYS[1], '*', unpack(fields))
            or not redis.acl_check_cmd('SET', KEYS[2], '0-0', 'PX', ARGV[3])
            or not redis.acl_check_cmd('XDEL', KEYS[1], '0-0') then
            return redis.error_reply('NOPERM job enqueue is not permitted')
        end
        local id = redis.call('XADD', KEYS[1], '*', unpack(fields))
        local recorded = redis.pcall('SET', KEYS[2], id, 'PX', ARGV[3])
        if type(recorded) == 'table' and recorded.err then
            redis.call('XDEL', KEYS[1], id)
            return recorded
        end
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

    /// <summary>The retained progress and completion stream. No automatic expiry or trimming is applied.</summary>
    public string EventsStreamKey => StreamKey + ":events";

    /// <summary>Creates a durable recurring scheduler for this queue using the supplied UTC clock.</summary>
    /// <remarks>Call RunDueAsync periodically, or register AddRespireJobScheduler to run it in the host.</remarks>
    public RespireJobScheduler<T> CreateScheduler(TimeProvider? timeProvider = null)
        => new(_client, StreamKey, _serialize, timeProvider ?? TimeProvider.System);

    /// <summary>Observes retained events after a cursor, resuming transient connections until cancellation.</summary>
    /// <remarks>Defaults to the beginning. Persist each event's Id after processing and pass it when restarting.
    /// Replaying an older cursor can deliver duplicates; trimming or eviction can lose events. Independent
    /// enumerations each see all events. Terminal events are committed atomically with job acknowledgement.</remarks>
    public async IAsyncEnumerable<RespireJobEvent> ReadEventsAsync(RespireStreamId after = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in _client.Streams.ReadAllAsync(EventsStreamKey, after,
            cancellationToken: cancellationToken).ConfigureAwait(false))
            yield return new(entry);
    }

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
        using var trace = new JobEnqueueTrace();
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        using var result = await _client.Scripts.ExecuteAsync(Enqueue, [StreamKey, deduplicationKey],
            [jobId, bytes, (long)Math.Ceiling(window.TotalMilliseconds), trace.Parent, trace.State], cancellationToken).ConfigureAwait(false);
        trace.Succeeded = true;
        return new(new(result[0].AsString()), result[1].AsInteger() == 1);
    }

    /// <summary>Enqueues with delay, bounded-batch priority and optional lifetime from enqueue.</summary>
    /// <remarks>The returned ID identifies the staged enqueue; processing receives a new ready-stream ID.
    /// A registered job worker promotes due jobs using Redis server time. Duplicate calls retain the first options.</remarks>
    public async ValueTask<RespireJobEnqueueResult> EnqueueScheduledAsync(string jobId, T payload,
        RespireJobEnqueueOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var deduplicationKey = StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(jobId)));
        string[] keys = options.Group is null
            ? [.. JobQueueScheduling.Keys(StreamKey), deduplicationKey]
            : [.. JobQueueScheduling.Keys(StreamKey), deduplicationKey, .. JobGroupScheduling.Keys(StreamKey).Skip(3)];
        JobQueueScheduling.ValidateKeys(_client, keys);
        using var trace = new JobEnqueueTrace();
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        using var result = await _client.Scripts.ExecuteAsync(JobQueueScheduling.Enqueue, [.. keys],
            [jobId, bytes, (long)Math.Ceiling(options.DeduplicationWindow.TotalMilliseconds),
                (long)Math.Ceiling(options.Delay.TotalMilliseconds), options.Priority,
                (long)Math.Ceiling(options.TimeToLive?.TotalMilliseconds ?? 0), trace.Parent, trace.State,
                options.Group ?? string.Empty], cancellationToken).ConfigureAwait(false);
        trace.Succeeded = true;
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
        return new(id, deserialize(payload), entry.Id) { Group = entry.GetString("_respire.job.group") };
    }
}
