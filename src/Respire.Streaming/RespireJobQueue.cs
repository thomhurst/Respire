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
    private static readonly RespireScript EnqueueScript = RespireScript.Create(
        "local deduplicationKey = KEYS[2]\nlocal deduplicationMode, revision = ARGV[6], ARGV[7]\n" + JobDeduplication.Duplicate + """
        -- Lua errors do not roll back writes. Check both mutations and rollback before appending.
        if not redis.acl_check_cmd then
            return redis.error_reply('ERR job enqueue requires Redis 7.0 or later')
        end
        local fields = {'_respire.job.id', ARGV[1], '_respire.job.payload', ARGV[2]}
        envelope(fields)
        if ARGV[4] and ARGV[4] ~= '' then
            table.insert(fields, 'traceparent'); table.insert(fields, ARGV[4])
            if ARGV[5] and ARGV[5] ~= '' then table.insert(fields, 'tracestate'); table.insert(fields, ARGV[5]) end
        end
        if not redis.acl_check_cmd('XADD', KEYS[1], '*', unpack(fields))
            or not redis.acl_check_cmd('SET', KEYS[2], '0-0', 'PX', ARGV[3])
            or not redis.acl_check_cmd('XDEL', KEYS[1], '0-0') then
            return redis.error_reply('NOPERM job enqueue is not permitted')
        end
        -- Keep the original three EVAL keys: Redis checks declared key ACLs even
        -- when unused. Only an active KeepLastIfActive generation touches these
        -- fixed suffixes of the already-prefixed, same-slot ready key.
        local target = followup and KEYS[1] .. ':scheduled' or KEYS[1]
        local followups = followup and KEYS[1] .. ':followups'
        if followup then
            if not redis.acl_check_cmd('XADD', target, '*', unpack(fields))
                or not redis.acl_check_cmd('XDEL', target, '0-0')
                or not redis.acl_check_cmd('ZADD', followups, 0, '0-0')
                or not redis.acl_check_cmd('ZREM', followups, '0-0') then
                return redis.error_reply('NOPERM job follow-up staging is not permitted')
            end
            local kind = redis.call('TYPE', followups).ok
            if kind ~= 'none' and kind ~= 'zset' then return redis.error_reply('WRONGTYPE job due index') end
        end
        local id = redis.call('XADD', target, '*', unpack(fields))
        if followup then
            local indexed = redis.pcall('ZADD', followups, 0, id)
            if type(indexed) == 'table' and indexed.err then redis.call('XDEL', target, id); return indexed end
        end
        local recorded = persist(id, followup)
        if type(recorded) == 'table' and recorded.err then
            redis.call('XDEL', target, id)
            if followup then redis.call('ZREM', followups, id) end
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

    /// <summary>Reads up to count newest retained log lines in append order for an application job ID. Count is 1 through 1000.</summary>
    /// <remarks>Returns an empty array for missing or expired logs. Reusing a job ID before log expiry shares its log history.</remarks>
    public ValueTask<string[]> GetLogsAsync(string jobId, int count = 100, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count));
        return _client.Lists.RangeAsync(JobLogKeys.ForJob(StreamKey, jobId), -count, -1, cancellationToken);
    }

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
    /// <remarks>A new job uses FirstSubmissionWins: its window starts at the first successful enqueue and is not extended by duplicates.
    /// It defaults to one day and must be positive and at most Int32.MaxValue milliseconds.
    /// Reuse the same job ID after an ambiguous network outcome. Duplicate submissions preserve the first payload,
    /// even after that job completes. An existing Extend job instead renews its original window.
    /// The window expires only deduplication metadata, never the job itself.</remarks>
    public ValueTask<RespireJobEnqueueResult> EnqueueAsync(string jobId, T payload,
        TimeSpan? deduplicationWindow = null, CancellationToken cancellationToken = default)
        => EnqueueAsync(RespireJobDeduplicationMode.FirstSubmissionWins, jobId, payload,
            deduplicationWindow, cancellationToken);

    /// <summary>Enqueues with an explicit deduplication policy using the application job ID as identity.</summary>
    /// <remarks>The first successful enqueue fixes the mode and window until that window expires.
    /// Extend renews the original window on duplicates, including after completion, without replacing payload or options.
    /// Replace accepts the latest payload and options until first acquisition, without renewing the window.
    /// Acquired Replace generations retain their envelope through retries and completion.
    /// KeepLastIfActive preserves queued work and retains one latest follow-up while acquired, including retries.
    /// Terminal completion or job expiry unblocks that follow-up; the original window never slides.
    /// Immediate follow-ups work with every hosted job worker; scheduled/grouped follow-ups require scheduling.
    /// Upgrade all producers before opting into Extend, and all producers and workers before using Replace or KeepLastIfActive.
    /// Workers do not renew the window on acquisition or retries.</remarks>
    public async ValueTask<RespireJobEnqueueResult> EnqueueAsync(RespireJobDeduplicationMode deduplicationMode,
        string jobId, T payload, TimeSpan? deduplicationWindow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        JobDeduplication.Validate(deduplicationMode);
        var window = deduplicationWindow ?? TimeSpan.FromDays(1);
        if (window <= TimeSpan.Zero || window > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(deduplicationWindow));
        cancellationToken.ThrowIfCancellationRequested();
        // Hash the ID so user-supplied braces and arbitrarily long IDs cannot change the Redis key's hash tag.
        var deduplicationKey = StreamKey + ":dedup:" + Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(jobId)));
        var keys = ImmediateKeys(deduplicationKey);
        JobQueueScheduling.ValidateKeys(_client, keys);
        using var trace = new JobEnqueueTrace();
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        using var result = await _client.Scripts.ExecuteAsync(EnqueueScript, [.. keys],
            [jobId, bytes, (long)Math.Ceiling(window.TotalMilliseconds), trace.Parent, trace.State,
                (int)deduplicationMode, Guid.NewGuid().ToString("N")], cancellationToken).ConfigureAwait(false);
        trace.Succeeded = true;
        return new(new(result[0].AsString()), result[1].AsInteger() == 1);
    }

    private string[] ImmediateKeys(string deduplicationKey)
        => [StreamKey, deduplicationKey, JobDeduplication.Key(StreamKey)];

    /// <summary>Enqueues with delay, bounded-batch priority and optional lifetime from enqueue.</summary>
    /// <remarks>The returned ID identifies the staged enqueue; processing receives a new ready-stream ID.
    /// A registered job worker promotes due jobs using Redis server time. Duplicate calls retain the first options
    /// unless the generation uses Replace and has never been acquired, or KeepLastIfActive holds a follow-up.</remarks>
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
        keys = [.. keys, JobDeduplication.Key(StreamKey)];
        JobQueueScheduling.ValidateKeys(_client, keys);
        using var trace = new JobEnqueueTrace();
        var bytes = _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null.");
        using var result = await _client.Scripts.ExecuteAsync(JobQueueScheduling.Enqueue, [.. keys],
            [jobId, bytes, (long)Math.Ceiling(options.DeduplicationWindow.TotalMilliseconds),
                (long)Math.Ceiling(options.Delay.TotalMilliseconds), options.Priority,
                (long)Math.Ceiling(options.TimeToLive?.TotalMilliseconds ?? 0), trace.Parent, trace.State,
                options.Group ?? string.Empty, (long)Math.Ceiling(options.MaximumRuntime?.TotalMilliseconds ?? 0),
                options.MaximumRuntime is null ? string.Empty : Guid.NewGuid().ToString("N"),
                (int)options.DeduplicationMode, Guid.NewGuid().ToString("N")], cancellationToken).ConfigureAwait(false);
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
