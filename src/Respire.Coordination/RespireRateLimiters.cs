using System.Diagnostics;
using System.Threading.RateLimiting;

namespace Respire.Coordination;

/// <summary>Creates Redis-backed implementations of .NET rate limiters.
/// Acquisitions are asynchronous. Synchronous attempts cannot reach Redis, so they return an unacquired lease
/// without retry metadata; callers such as ASP.NET Core rate-limiting middleware then fall back to <c>AcquireAsync</c>.</summary>
public sealed class RespireRateLimiters(RespireCoordination coordination)
{
    private readonly RespireCoordination _coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
    private RespireRateLimitDenyList? _denyList;
    private string? _identity;

    /// <summary>Creates a factory whose limiters check the given identity before admission and each queued retry.</summary>
    /// <remarks>The original factory is unchanged. Share the deny list across identities to share its bounded cache.
    /// Denied requests consume no permits and carry ReasonPhrase = "deny_list" without RetryAfter.</remarks>
    public RespireRateLimiters WithDenyList(RespireRateLimitDenyList denyList, string identity)
    {
        ArgumentNullException.ThrowIfNull(denyList);
        ArgumentException.ThrowIfNullOrEmpty(identity);
        return new RespireRateLimiters(_coordination) { _denyList = denyList, _identity = identity };
    }

    private RedisRateLimiter Configure(RedisRateLimiter limiter)
    {
        limiter.DenyList = _denyList;
        limiter.Identity = _identity;
        return limiter;
    }

    /// <summary>Changes a standalone limiter's settings while retaining its usage and local queue.</summary>
    /// <remarks>The algorithm cannot change. Active Redis attempts finish before migration; later attempts use
    /// the new settings. Coordinate updates across all processes sharing the key. See the coordination guide
    /// for each algorithm's migration semantics. Queue capacity and order remain unchanged.</remarks>
    public ValueTask UpdateAsync(RateLimiter limiter, RespireRateLimit settings,
        RespireRateLimitUpdateMode mode = RespireRateLimitUpdateMode.Migrate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GetOwnedLimiter(limiter).UpdateAsync([settings], composite: false, mode, cancellationToken);
    }

    /// <summary>Changes a composite limiter's settings, preserving component count, order, algorithms and usage.</summary>
    /// <remarks>Coordinate all processes sharing the key. Fixed-window deadlines are retained; sliding history
    /// is retained without resurrecting expired usage; buckets settle old refill before changing settings.
    /// Queue capacity and order remain unchanged. Acquisitions pause locally during migration.</remarks>
    public ValueTask UpdateAsync(RateLimiter limiter, IReadOnlyList<RespireRateLimit> settings,
        RespireRateLimitUpdateMode mode = RespireRateLimitUpdateMode.Migrate, CancellationToken cancellationToken = default)
        => GetOwnedLimiter(limiter).UpdateAsync(settings, composite: true, mode, cancellationToken);

    private RedisRateLimiter GetOwnedLimiter(RateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        if (limiter is not RedisRateLimiter redis || !ReferenceEquals(redis.Coordination, _coordination))
            throw new ArgumentException("The limiter must belong to this coordination instance.", nameof(limiter));
        return redis;
    }

    /// <summary>Combines one to 32 limits on a dedicated key, consuming permits only when every limit allows them.</summary>
    /// <remarks>Supports fixed windows, segmented sliding windows, and token buckets. Each Redis attempt uses one
    /// EVAL, including on a cold server. RetryAfter is the longest component delay. All clients must use identical
    /// ordered definitions. The key supports binary values and client prefixes and occupies one Cluster slot.
    /// Use AcquireAsync; zero permits probe without writing, and requests above any component limit cannot succeed.</remarks>
    public RateLimiter Composite(RespireKey key, IReadOnlyList<RespireRateLimit> limits, int queueLimit = 0,
        QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => Configure(new RedisRateLimiter(_coordination, key, limits, queueLimit, queueProcessingOrder));

    /// <summary>Creates a fixed-window rate limiter.</summary>
    /// <remarks>The limiter is asynchronous-only: use <c>AcquireAsync</c>. <c>AttemptAcquire</c> cannot reach Redis and always
    /// returns an unacquired lease without <c>RetryAfter</c>, even when permits are free. A request for more permits than the
    /// limit returns an unacquired lease without <c>RetryAfter</c>, because it can never succeed.</remarks>
    public RateLimiter FixedWindow(RespireKey key, int permitLimit, TimeSpan window, int queueLimit = 0,
        QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => Configure(new RedisRateLimiter(_coordination, key, permitLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.FixedWindow, window));

    /// <summary>Creates a sliding-window rate limiter divided into segments.</summary>
    /// <remarks>The limiter is asynchronous-only: use <c>AcquireAsync</c>. <c>AttemptAcquire</c> cannot reach Redis and always
    /// returns an unacquired lease without <c>RetryAfter</c>, even when permits are free. A request for more permits than the
    /// limit returns an unacquired lease without <c>RetryAfter</c>, because it can never succeed.</remarks>
    public RateLimiter SlidingWindow(RespireKey key, int permitLimit, TimeSpan window, int segments,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => Configure(new RedisRateLimiter(_coordination, key, permitLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.SlidingWindow, window, segments));

    /// <summary>Creates a token-bucket rate limiter.</summary>
    /// <remarks>The limiter is asynchronous-only: use <c>AcquireAsync</c>. <c>AttemptAcquire</c> cannot reach Redis and always
    /// returns an unacquired lease without <c>RetryAfter</c>, even when permits are free. A request for more permits than the
    /// limit returns an unacquired lease without <c>RetryAfter</c>, because it can never succeed.</remarks>
    public RateLimiter TokenBucket(RespireKey key, int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => Configure(new RedisRateLimiter(_coordination, key, tokenLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.TokenBucket, replenishmentPeriod, tokensPerPeriod: tokensPerPeriod));

    /// <summary>Creates a Generic Cell Rate Algorithm (GCRA) limiter with an initial burst and a steady refill rate.</summary>
    /// <param name="key">The Redis key, before the client's key prefix. All users of this key must use identical settings.</param>
    /// <param name="burstLimit">The maximum permits available at once, including the initial burst.</param>
    /// <param name="emissionInterval">Time to restore one permit; at least one millisecond, rounded up to whole milliseconds.
    /// The time to restore the entire burst must fit in a <see cref="TimeSpan"/>.</param>
    /// <param name="queueLimit">Maximum permits queued locally while awaiting capacity.</param>
    /// <param name="queueProcessingOrder">The order in which local queued requests are retried.</param>
    /// <remarks>Use <c>AcquireAsync</c>; synchronous attempts return an unacquired lease without retry metadata.
    /// A zero-permit acquisition probes for one available permit without consuming it. Requests above
    /// <paramref name="burstLimit"/> are rejected without retry metadata. Redis server time controls refill.</remarks>
    public RateLimiter Gcra(RespireKey key, int burstLimit, TimeSpan emissionInterval,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => Configure(new RedisRateLimiter(_coordination, key, burstLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.Gcra, emissionInterval));
}

internal enum RedisRateLimiterKind { FixedWindow, SlidingWindow, TokenBucket, Gcra, Composite }

internal sealed partial class RedisRateLimiter : RateLimiter
{
    internal RespireRateLimitDenyList? DenyList { get; set; }
    internal string? Identity { get; set; }
    private static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private const long MaxExactLuaInteger = 9_007_199_254_740_991;
    private static readonly long MaxTimeSpanMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
    private static readonly RespireScript GcraScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local interval = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local requested = tonumber(ARGV[3])
        local capacity = interval * limit
        local tat = math.max(now, tonumber(redis.call('GET', KEYS[1]) or now))
        local debt = tat - now
        local available = math.max(0, math.floor((capacity - debt) / interval))
        -- Probes and denied requests must not advance the theoretical arrival time (TAT).
        local cost = math.max(1, requested) * interval
        local retry = debt + cost - capacity
        if retry > 0 then return {0, retry, available} end
        if requested == 0 then return {1, 0, available} end
        tat = tat + cost
        -- Lua's default number-to-string conversion can round large integer timestamps.
        redis.call('SET', KEYS[1], string.format('%.0f', tat), 'PX', string.format('%.0f', tat - now))
        return {1, 0, available - requested}
        """);

    private static readonly RespireScript FixedWindowScript = RespireScript.Create("""
        local width = tonumber(ARGV[1])
        local requested = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        if requested == 0 then
            local value = tonumber(redis.call('GET', KEYS[1]) or '0')
            local ttl = redis.call('PTTL', KEYS[1])
            local available = limit - value
            if available > 0 then return {1, 0, available, 0} end
            return {0, math.max(1, ttl), available, 0}
        end
        -- The fourth result element reports that INCREX is unavailable so the caller stops probing it.
        local unsupported = 0
        if ARGV[4] == '1' then
            local result = redis.pcall('INCREX', KEYS[1], 'BYINT', requested, 'UBOUND', limit, 'PX', width, 'ENX')
            if type(result) ~= 'table' then return redis.error_reply('ERR INCREX returned an unexpected reply') end
            if not result.err then
                local current = tonumber(result[1])
                local applied = tonumber(result[2])
                if applied == requested then return {1, 0, limit - current, 0} end
                return {0, math.max(1, redis.call('PTTL', KEYS[1])), limit - current, 0}
            end
            -- Only a missing command selects the fallback ("unknown command" or "Unknown Redis command
            -- called from script"); any other INCREX error, such as an unknown option, is surfaced.
            local err = string.lower(result.err)
            if not (string.find(err, 'unknown command', 1, true) or string.find(err, 'unknown %a+ command')) then
                return redis.error_reply(result.err)
            end
            unsupported = 1
        end
        local value = tonumber(redis.call('GET', KEYS[1]) or '0')
        local ttl = redis.call('PTTL', KEYS[1])
        if value + requested <= limit then
            value = value + requested
            if ttl < 0 then redis.call('SET', KEYS[1], value, 'PX', width)
            else redis.call('SET', KEYS[1], value, 'KEEPTTL') end
            return {1, 0, limit - value, unsupported}
        end
        return {0, math.max(1, ttl), limit - value, unsupported}
        """);

    private static readonly RespireScript SlidingWindowScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local width = tonumber(ARGV[1])
        local segment = math.ceil(width / tonumber(ARGV[2]))
        local bucket = math.floor(now / segment) * segment
        -- Score at the segment end so a permit cannot expire before its full window.
        local bucketEnd = bucket + segment
        local cutoff = now - width
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', cutoff)
        -- At most one member per segment of the current window remains, so this scan is bounded.
        local entries = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local count = 0
        for i = 1, #entries, 2 do
            local separator = string.find(entries[i], ':', 1, true)
            count = count + tonumber(string.sub(entries[i], separator + 1))
        end
        local requested = tonumber(ARGV[3])
        local limit = tonumber(ARGV[4])
        if requested == 0 then
            if count < limit then return {1, 0, limit - count} end
            requested = 1
        end
        if count + requested <= limit then
            local current = redis.call('ZRANGEBYSCORE', KEYS[1], bucketEnd, bucketEnd)
            local segmentCount = 0
            if #current > 0 then
                local separator = string.find(current[1], ':', 1, true)
                segmentCount = tonumber(string.sub(current[1], separator + 1))
                redis.call('ZREM', KEYS[1], current[1])
            end
            redis.call('ZADD', KEYS[1], bucketEnd, bucket .. ':' .. (segmentCount + requested))
            local latest = math.max(bucketEnd, tonumber(entries[#entries]) or bucketEnd)
            redis.call('PEXPIRE', KEYS[1], math.max(width * 2, latest + width - now))
            return {1, 0, limit - count - requested}
        end
        local needed = count + requested - limit
        local released = 0
        local retry = 1
        for i = 1, #entries, 2 do
            local separator = string.find(entries[i], ':', 1, true)
            released = released + tonumber(string.sub(entries[i], separator + 1))
            if released >= needed then
                retry = tonumber(entries[i + 1]) + width - now
                break
            end
        end
        return {0, math.max(1, retry), limit - count}
        """);

    private static readonly RespireScript TokenBucketScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local last = tonumber(redis.call('HGET', KEYS[1], 'time') or now)
        local tokens = tonumber(redis.call('HGET', KEYS[1], 'tokens') or ARGV[1])
        local elapsed = math.max(0, now - last)
        tokens = math.min(tonumber(ARGV[1]), tokens + math.floor(elapsed / tonumber(ARGV[2])) * tonumber(ARGV[3]))
        last = last + math.floor(elapsed / tonumber(ARGV[2])) * tonumber(ARGV[2])
        local requested = tonumber(ARGV[4])
        if requested == 0 then
            redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', last)
            redis.call('PEXPIRE', KEYS[1], ARGV[5])
            if tokens > 0 then return {1, 0, tokens} end
            return {0, math.max(1, tonumber(ARGV[2]) - (now - last)), tokens}
        end
        if tokens >= requested then
            tokens = tokens - requested
            redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', last)
            redis.call('PEXPIRE', KEYS[1], ARGV[5])
            return {1, 0, tokens}
        end
        local missing = requested - tokens
        local retry = math.ceil(missing / tonumber(ARGV[3])) * tonumber(ARGV[2]) - (now - last)
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', last)
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return {0, math.max(1, retry), tokens}
        """);

    private readonly RespireCoordination _coordination;
    private readonly RespireKey _key;
    private int _permitLimit;
    private readonly int _queueLimit;
    private readonly QueueProcessingOrder _queueOrder;
    private readonly RedisRateLimiterKind _kind;
    private long _periodMs;
    private int _segments;
    private int _tokensPerPeriod;
    private long _tokenBucketExpiryMs;
    private readonly Lock _queueGate = new();
    private readonly LinkedList<QueuedRequest> _queue = [];
    private readonly SemaphoreSlim _queueChanged = new(0, 1);
    private int _queuedPermits;
    private bool _pumpRunning;
    private volatile bool _disposed;
    private readonly RespireScript _script;
    internal bool IncrexUnsupported => _coordination.IncrexUnsupported;
    internal RespireCoordination Coordination => _coordination;
    private int _activeAcquisitions;
    private long _lastActivity = Stopwatch.GetTimestamp();
    private long _availablePermits;
    private long _successfulLeases;
    private long _failedLeases;

    internal RedisRateLimiter(RespireCoordination coordination, RespireKey key, int permitLimit, int queueLimit,
        QueueProcessingOrder queueOrder, RedisRateLimiterKind kind, TimeSpan period, int segments = 0,
        int tokensPerPeriod = 0)
    {
        if (key.IsEmpty) throw new ArgumentException("The rate-limit key must not be empty.", nameof(key));
        if (permitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(permitLimit));
        if (queueLimit < 0) throw new ArgumentOutOfRangeException(nameof(queueLimit));
        if (queueOrder is not QueueProcessingOrder.OldestFirst and not QueueProcessingOrder.NewestFirst)
            throw new ArgumentOutOfRangeException(nameof(queueOrder));
        if (period < TimeSpan.FromMilliseconds(1)) throw new ArgumentOutOfRangeException(nameof(period));
        if (kind == RedisRateLimiterKind.SlidingWindow && segments < 1)
            throw new ArgumentOutOfRangeException(nameof(segments));
        if (kind == RedisRateLimiterKind.TokenBucket && tokensPerPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokensPerPeriod));
        _coordination = coordination;
        _key = key.Snapshot();
        _permitLimit = permitLimit;
        _queueLimit = queueLimit;
        _queueOrder = queueOrder;
        _kind = kind;
        _periodMs = kind == RedisRateLimiterKind.Gcra
            ? (period.Ticks - 1) / TimeSpan.TicksPerMillisecond + 1
            : checked((long)period.TotalMilliseconds);
        _segments = segments;
        _tokensPerPeriod = tokensPerPeriod;
        _script = kind switch
        {
            RedisRateLimiterKind.FixedWindow => FixedWindowScript,
            RedisRateLimiterKind.SlidingWindow => SlidingWindowScript,
            RedisRateLimiterKind.Gcra => GcraScript,
            RedisRateLimiterKind.Composite => CompositeScript,
            _ => TokenBucketScript,
        };
        // Bounds the retry/expiry horizon and keeps timestamp arithmetic within Lua's exact integer range.
        if (kind == RedisRateLimiterKind.Gcra && _periodMs > MaxTimeSpanMilliseconds / permitLimit)
            throw new ArgumentOutOfRangeException(nameof(period), "GCRA burst refill duration exceeds the TimeSpan range.");
        if (kind == RedisRateLimiterKind.TokenBucket)
        {
            var periodsToFull = ((long)permitLimit + tokensPerPeriod - 1) / tokensPerPeriod;
            if (_periodMs > MaxExactLuaInteger / periodsToFull)
                throw new ArgumentOutOfRangeException(nameof(period), "Token-bucket refill duration exceeds Redis/Lua's exact integer range.");
            _tokenBucketExpiryMs = _periodMs * periodsToFull;
        }
        _availablePermits = permitLimit;
    }

    // Shared state lives in Redis, so idle means no local acquisition or queued request.
    // PartitionedRateLimiter may dispose an idle limiter safely; a replacement resumes from Redis state.
    public override TimeSpan? IdleDuration
        => Volatile.Read(ref _activeAcquisitions) != 0 || Volatile.Read(ref _queuedPermits) != 0
            || Volatile.Read(ref _redisReaders) != 0
            ? null
            : Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity));

    // Available permits reflect the latest Redis response seen by this instance; other processes may
    // have consumed permits since.
    public override RateLimiterStatistics GetStatistics() => new()
    {
        CurrentAvailablePermits = Volatile.Read(ref _availablePermits),
        CurrentQueuedCount = Volatile.Read(ref _queuedPermits),
        TotalSuccessfulLeases = Volatile.Read(ref _successfulLeases),
        TotalFailedLeases = Volatile.Read(ref _failedLeases),
    };

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        if (permitCount < 0) throw new ArgumentOutOfRangeException(nameof(permitCount));
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A synchronous probe cannot reach Redis. Report "not acquired" without retry metadata so callers
        // that probe first (ASP.NET Core middleware, chained limiters) fall back to AcquireAsync. The probe
        // never consulted the shared limiter, so it is not counted in statistics.
        return new RedisRateLimitLease(false, null);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        => CoordinationCommandHooks.RunAsync(_coordination.Client, "RATE LIMIT ACQUIRE", cancellationToken,
            (Limiter: this, Permits: permitCount, Token: cancellationToken),
            static s => s.Limiter.AcquireObservedAsync(s.Permits, s.Token));

    private async ValueTask<RateLimitLease> AcquireObservedAsync(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount < 0) throw new ArgumentOutOfRangeException(nameof(permitCount));
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _activeAcquisitions);
        try
        {
            var lease = await AcquireCoreAsync(permitCount, cancellationToken).ConfigureAwait(false);
            if (lease.IsAcquired) Interlocked.Increment(ref _successfulLeases);
            else Interlocked.Increment(ref _failedLeases);
            RateLimiterMetrics.Record(lease.IsAcquired, IsDenyListRejection(lease));
            return lease;
        }
        finally
        {
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _activeAcquisitions);
        }
    }

    private async ValueTask<RateLimitLease> AcquireCoreAsync(int permitCount, CancellationToken cancellationToken)
    {
        var settingsVersion = Volatile.Read(ref _settingsVersion);
        if (DenyList is not null && await DenyList.IsDeniedForAcquisitionAsync(_coordination.Client,
            Identity!, cancellationToken).ConfigureAwait(false))
            return new RedisRateLimitLease(false, null, denyList: true);
        if (permitCount == 0) return await AcquireFromRedisAsync(0, cancellationToken, checkDenyList: false).ConfigureAwait(false);
        if (Volatile.Read(ref _queuedPermits) != 0)
        {
            if (_queueLimit == 0) return new RedisRateLimitLease(false, null);
            return await QueueAsync(permitCount, new RedisRateLimitLease(false, null), settingsVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        var lease = await AcquireFromRedisAsync(permitCount, cancellationToken, checkDenyList: false).ConfigureAwait(false);
        if (lease.IsAcquired || _queueLimit == 0) return lease;
        return await QueueAsync(permitCount, lease, settingsVersion, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RateLimitLease> AcquireFromRedisAsync(int permitCount, CancellationToken cancellationToken,
        bool checkDenyList = true)
    {
        if (checkDenyList && DenyList is not null
            && await DenyList.IsDeniedForAcquisitionAsync(_coordination.Client,
                Identity!, cancellationToken).ConfigureAwait(false))
            return new RedisRateLimitLease(false, null, denyList: true);
        await EnterRedisAttemptAsync(cancellationToken).ConfigureAwait(false);
        var attempt = ExecuteRegisteredAttemptAsync(permitCount, cancellationToken);
        if (!cancellationToken.CanBeCanceled) return await attempt.ConfigureAwait(false);
        var task = attempt.AsTask();
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = DisposeAbandonedAttemptAsync(task);
            throw;
        }
    }

    private async ValueTask<RateLimitLease> ExecuteRegisteredAttemptAsync(int permitCount, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (permitCount > _permitLimit) return new RedisRateLimitLease(false, null);
            // Caller cancellation may stop waiting, but migration must still drain the server reply.
            return await ExecuteAcquisitionAsync(permitCount, CancellationToken.None).ConfigureAwait(false);
        }
        finally { ExitRedisAttempt(); }
    }

    private static async Task DisposeAbandonedAttemptAsync(Task<RateLimitLease> attempt)
    {
        try { (await attempt.ConfigureAwait(false)).Dispose(); }
        catch { /* The canceled caller has left; observe a later transport failure. */ }
    }

    private async ValueTask<RateLimitLease> ExecuteAcquisitionAsync(int permitCount, CancellationToken cancellationToken)
    {
        RespireValue[] args = _kind switch
        {
            RedisRateLimiterKind.Composite => CreateCompositeArguments(permitCount),
            RedisRateLimiterKind.FixedWindow => [_periodMs, permitCount, _permitLimit, _coordination.IncrexUnsupported ? 0 : 1],
            RedisRateLimiterKind.SlidingWindow => [_periodMs, _segments, permitCount, _permitLimit],
            RedisRateLimiterKind.Gcra => [_periodMs, _permitLimit, permitCount],
            _ => [_permitLimit, _periodMs, _tokensPerPeriod, permitCount, _tokenBucketExpiryMs],
        };
        using var result = await (_kind == RedisRateLimiterKind.Composite
            ? _coordination.ExecuteCompositeRateLimitAsync(_script, _key, args, cancellationToken)
            : _coordination.ExecuteRateLimitScriptAsync(_script, _key, args, cancellationToken))
            .ConfigureAwait(false);
        var expectedCount = _kind == RedisRateLimiterKind.FixedWindow ? 4 : 3;
        if (result.Count != expectedCount) throw new RespireProtocolException("Rate-limit script returned an invalid response.");
        // Pre-8.8 servers reject INCREX; remember that per connection so later limiters skip the failing probe.
        if (expectedCount == 4 && result[3].AsInteger() == 1) _coordination.IncrexUnsupported = true;
        Volatile.Write(ref _availablePermits, Math.Max(0, result[2].AsInteger()));
        var granted = result[0].AsInteger() == 1;
        var retry = Math.Clamp(result[1].AsInteger(), 0, MaxTimeSpanMilliseconds);
        return new RedisRateLimitLease(granted, TimeSpan.FromMilliseconds(retry));
    }

    private async ValueTask<RateLimitLease> QueueAsync(int permitCount, RateLimitLease denied, long settingsVersion, CancellationToken cancellationToken)
    {
        var request = new QueuedRequest(permitCount, ReadRetryAfter(denied), cancellationToken);
        var startPump = false;
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permitCount > _permitLimit) return new RedisRateLimitLease(false, null);
            if (settingsVersion != _settingsVersion) request.SetRetryAfter(TimeSpan.Zero);
            if (permitCount > _queueLimit || (_queueOrder == QueueProcessingOrder.OldestFirst
                && permitCount > _queueLimit - _queuedPermits))
                return _queuedPermits == 0 ? denied : new RedisRateLimitLease(false, null);
            if (_queueOrder == QueueProcessingOrder.NewestFirst && permitCount > _queueLimit - _queuedPermits)
            {
                var removablePermits = 0;
                for (var node = _queue.Last; node is not null && !node.Value.IsProcessing; node = node.Previous)
                    removablePermits += node.Value.PermitCount;
                if (permitCount > _queueLimit - _queuedPermits + removablePermits)
                    return new RedisRateLimitLease(false, null);
                while (permitCount > _queueLimit - _queuedPermits)
                {
                    var removed = _queue.Last!;
                    // Capacity was preflighted above; the processing request cannot be
                    // reached before enough removable tail permits have been evicted.
                    if (removed.Value.IsProcessing) throw new InvalidOperationException("Queue capacity preflight was inconsistent.");
                    _queue.RemoveLast();
                    removed.Value.Node = null;
                    _queuedPermits -= removed.Value.PermitCount;
                    removed.Value.Registration.Unregister();
                    removed.Value.Completion.TrySetResult(new RedisRateLimitLease(false, null));
                }
            }
            request.Node = _queueOrder == QueueProcessingOrder.NewestFirst
                ? _queue.AddFirst(request)
                : _queue.AddLast(request);
            _queuedPermits += permitCount;
            request.Registration = cancellationToken.Register(() => CancelQueued(request));
            if (!_pumpRunning)
            {
                _pumpRunning = true;
                startPump = true;
            }
        }
        SignalQueueChanged();
        if (startPump) _ = PumpQueueAsync();
        return await request.Completion.Task.ConfigureAwait(false);
    }

    private async Task PumpQueueAsync()
    {
        try
        {
            await RunPumpAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Never leave waiters behind a faulted pump: fail them and let the next request start a new pump.
            lock (_queueGate)
            {
                _pumpRunning = false;
                FailQueuedRequests(error);
            }
        }
    }

    private async Task RunPumpAsync()
    {
        while (true)
        {
            QueuedRequest request;
            lock (_queueGate)
            {
                if (_queue.First is null)
                {
                    _pumpRunning = false;
                    return;
                }
                request = _queue.First.Value;
            }
            using var wakeCancellation = new CancellationTokenSource();
            TimeSpan remaining;
            lock (_queueGate) remaining = request.RemainingRetryAfter;
            var delay = remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1)
                : remaining > MaxQueueDelay ? MaxQueueDelay : remaining;
            var timer = Task.Delay(delay, wakeCancellation.Token);
            var changed = _queueChanged.WaitAsync(wakeCancellation.Token);
            await Task.WhenAny(timer, changed).ConfigureAwait(false);
#pragma warning disable CA1849 // This private token only serves Task.Delay and SemaphoreSlim.WaitAsync; detach the losing wait without a worker hop on each queue tick.
            if (!timer.IsCompleted)
            {
                wakeCancellation.Cancel();
                continue;
            }
            wakeCancellation.Cancel();
#pragma warning restore CA1849
            lock (_queueGate)
            {
                if (request.Node?.List is null || request.Node != _queue.First) continue;
                request.IsProcessing = true;
                request.SettingsVersion = _settingsVersion;
            }
            if (request.CancellationToken.IsCancellationRequested)
            {
                CancelQueued(request);
                continue;
            }
            RateLimitLease lease;
            try { lease = await AcquireFromRedisAsync(request.PermitCount, request.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
            {
                CancelQueued(request);
                continue;
            }
            catch (Exception error)
            {
                FailQueued(request, error);
                continue;
            }
            if (!lease.IsAcquired && lease.TryGetMetadata(MetadataName.RetryAfter, out _))
            {
                lock (_queueGate)
                {
                    request.IsProcessing = false;
                    // Updates wake the queue; do not restore retry metadata from older settings.
                    request.SetRetryAfter(request.SettingsVersion == _settingsVersion ? ReadRetryAfter(lease) : TimeSpan.Zero);
                }
                continue;
            }
            CompleteQueued(request, lease);
        }
    }

    private void CancelQueued(QueuedRequest request)
    {
        if (TryRemoveQueued(request)) request.Completion.TrySetCanceled(request.CancellationToken);
    }

    private void CompleteQueued(QueuedRequest request, RateLimitLease lease)
    {
        if (!TryRemoveQueued(request))
        {
            // Cancellation or disposal won the race after Redis granted the permits. Leases are consumptive
            // and Redis has no refund path, so those permits stay consumed until the window or refill.
            lease.Dispose();
            return;
        }
        request.Completion.TrySetResult(lease);
    }

    private void FailQueued(QueuedRequest request, Exception error)
    {
        if (TryRemoveQueued(request)) request.Completion.TrySetException(error);
    }

    private bool TryRemoveQueued(QueuedRequest request)
    {
        lock (_queueGate)
        {
            if (request.Node?.List is null) return false;
            _queuedPermits -= request.PermitCount;
            _queue.Remove(request.Node);
            request.Node = null;
            request.Registration.Unregister();
        }
        SignalQueueChanged();
        return true;
    }

    private void SignalQueueChanged()
    {
        try { _queueChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static TimeSpan ReadRetryAfter(RateLimitLease lease)
        => lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry) ? retry : TimeSpan.FromMilliseconds(1);

    protected override void Dispose(bool disposing)
    {
        lock (_queueGate)
        {
            _disposed = true;
            _settingsChange?.TrySetResult();
        }
        FailQueuedRequests(new ObjectDisposedException(nameof(RedisRateLimiter)));
        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        lock (_queueGate)
        {
            _disposed = true;
            _settingsChange?.TrySetResult();
        }
        FailQueuedRequests(new ObjectDisposedException(nameof(RedisRateLimiter)));
        return ValueTask.CompletedTask;
    }

    private void FailQueuedRequests(Exception error)
    {
        lock (_queueGate)
        {
            foreach (var request in _queue)
            {
                request.Registration.Unregister();
                request.Node = null;
                request.Completion.TrySetException(error);
            }
            _queue.Clear();
            _queuedPermits = 0;
        }
        SignalQueueChanged();
    }

    private sealed class QueuedRequest(int permitCount, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        public int PermitCount { get; } = permitCount;
        private TimeSpan _retryAfter = retryAfter;
        private long _retryStarted = Stopwatch.GetTimestamp();
        public TimeSpan RemainingRetryAfter
            => _retryAfter - Stopwatch.GetElapsedTime(Volatile.Read(ref _retryStarted));
        public void SetRetryAfter(TimeSpan retryAfter)
        {
            _retryAfter = retryAfter;
            Volatile.Write(ref _retryStarted, Stopwatch.GetTimestamp());
        }
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<RateLimitLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public LinkedListNode<QueuedRequest>? Node { get; set; }
        public bool IsProcessing { get; set; }
        public long SettingsVersion { get; set; }
    }

    private static bool IsDenyListRejection(RateLimitLease lease)
        => lease is RedisRateLimitLease { DeniedByList: true };

    private sealed class RedisRateLimitLease(bool acquired, TimeSpan? retryAfter, bool denyList = false) : RateLimitLease
    {
        internal bool DeniedByList => denyList;
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames
            => denyList ? [MetadataName.ReasonPhrase.Name]
                : acquired || retryAfter is null ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (denyList && metadataName == MetadataName.ReasonPhrase.Name)
            {
                metadata = "deny_list";
                return true;
            }
            if (!acquired && retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
