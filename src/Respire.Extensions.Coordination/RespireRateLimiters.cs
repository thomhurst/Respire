using System.Diagnostics;
using System.Threading.RateLimiting;

namespace Respire.Extensions.Coordination;

/// <summary>Creates Redis-backed implementations of .NET rate limiters.
/// Acquisitions are asynchronous. Synchronous attempts cannot reach Redis, so they return an unacquired lease
/// without retry metadata; callers such as ASP.NET Core rate-limiting middleware then fall back to <c>AcquireAsync</c>.</summary>
public sealed class RespireRateLimiters(RespireCoordination coordination)
{
    private readonly RespireCoordination _coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));

    /// <summary>Creates a fixed-window rate limiter.</summary>
    public RateLimiter FixedWindow(RespireKey key, int permitLimit, TimeSpan window, int queueLimit = 0,
        QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => new RedisRateLimiter(_coordination, key, permitLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.FixedWindow, window);

    /// <summary>Creates a sliding-window rate limiter divided into segments.</summary>
    public RateLimiter SlidingWindow(RespireKey key, int permitLimit, TimeSpan window, int segments,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => new RedisRateLimiter(_coordination, key, permitLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.SlidingWindow, window, segments);

    /// <summary>Creates a token-bucket rate limiter.</summary>
    public RateLimiter TokenBucket(RespireKey key, int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod,
        int queueLimit = 0, QueueProcessingOrder queueProcessingOrder = QueueProcessingOrder.OldestFirst)
        => new RedisRateLimiter(_coordination, key, tokenLimit, queueLimit, queueProcessingOrder,
            RedisRateLimiterKind.TokenBucket, replenishmentPeriod, tokensPerPeriod: tokensPerPeriod);
}

internal enum RedisRateLimiterKind { FixedWindow, SlidingWindow, TokenBucket }

internal sealed class RedisRateLimiter : RateLimiter
{
    private static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private const long MaxExactLuaInteger = 9_007_199_254_740_991;
    private static readonly long MaxTimeSpanMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
    private static readonly RespireScript FixedWindowScript = RespireScript.Create("""
        local width = tonumber(ARGV[1])
        local requested = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        -- The fourth result element reports that INCREX is unavailable so the caller stops probing it.
        local unsupported = 0
        if ARGV[4] == '1' then
            local result = redis.pcall('INCREX', KEYS[1], 'BYINT', requested, 'UBOUND', limit, 'PX', width, 'ENX')
            if type(result) == 'table' and not result.err then
                local current = tonumber(result[1])
                local applied = tonumber(result[2])
                if applied == requested then return {1, 0, limit - current, 0} end
                return {0, math.max(1, redis.call('PTTL', KEYS[1])), limit - current, 0}
            end
            if not string.find(string.lower(result.err or ''), 'unknown', 1, true) then
                return redis.error_reply(result.err or 'ERR INCREX failed')
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
        if count + requested <= limit then
            local current = redis.call('ZRANGEBYSCORE', KEYS[1], bucketEnd, bucketEnd)
            local segmentCount = 0
            if #current > 0 then
                local separator = string.find(current[1], ':', 1, true)
                segmentCount = tonumber(string.sub(current[1], separator + 1))
                redis.call('ZREM', KEYS[1], current[1])
            end
            redis.call('ZADD', KEYS[1], bucketEnd, bucket .. ':' .. (segmentCount + requested))
            redis.call('PEXPIRE', KEYS[1], width * 2)
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
    private readonly int _permitLimit;
    private readonly int _queueLimit;
    private readonly QueueProcessingOrder _queueOrder;
    private readonly RedisRateLimiterKind _kind;
    private readonly long _periodMs;
    private readonly int _segments;
    private readonly int _tokensPerPeriod;
    private readonly long _tokenBucketExpiryMs;
    private readonly object _queueGate = new();
    private readonly LinkedList<QueuedRequest> _queue = [];
    private readonly SemaphoreSlim _queueChanged = new(0, 1);
    private int _queuedPermits;
    private bool _pumpRunning;
    private volatile bool _disposed;
    private volatile bool _increxUnsupported;
    internal bool IncrexUnsupported => _increxUnsupported;
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
        _periodMs = checked((long)period.TotalMilliseconds);
        _segments = segments;
        _tokensPerPeriod = tokensPerPeriod;
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

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
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
        if (permitCount > _permitLimit) return new RedisRateLimitLease(false, null);
        if (permitCount == 0) return new RedisRateLimitLease(true, TimeSpan.Zero);
        if (Volatile.Read(ref _queuedPermits) != 0)
        {
            if (_queueLimit == 0) return new RedisRateLimitLease(false, null);
            return await QueueAsync(permitCount, new RedisRateLimitLease(false, null), cancellationToken)
                .ConfigureAwait(false);
        }
        var lease = await AcquireFromRedisAsync(permitCount, cancellationToken).ConfigureAwait(false);
        if (lease.IsAcquired || _queueLimit == 0) return lease;
        return await QueueAsync(permitCount, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RateLimitLease> AcquireFromRedisAsync(int permitCount, CancellationToken cancellationToken)
    {
        RespireValue[] args = _kind switch
        {
            RedisRateLimiterKind.FixedWindow => [_periodMs, permitCount, _permitLimit, _increxUnsupported ? 0 : 1],
            RedisRateLimiterKind.SlidingWindow => [_periodMs, _segments, permitCount, _permitLimit],
            _ => [_permitLimit, _periodMs, _tokensPerPeriod, permitCount, _tokenBucketExpiryMs],
        };
        var script = _kind switch
        {
            RedisRateLimiterKind.FixedWindow => FixedWindowScript,
            RedisRateLimiterKind.SlidingWindow => SlidingWindowScript,
            _ => TokenBucketScript,
        };
        using var result = await _coordination.ExecuteRateLimitScriptAsync(script, _key, args, cancellationToken)
            .ConfigureAwait(false);
        var expectedCount = _kind == RedisRateLimiterKind.FixedWindow ? 4 : 3;
        if (result.Count != expectedCount) throw new RespireProtocolException("Rate-limit script returned an invalid response.");
        // Pre-8.8 servers reject INCREX; remember that so later calls skip the failing probe.
        if (expectedCount == 4 && result[3].AsInteger() == 1) _increxUnsupported = true;
        Volatile.Write(ref _availablePermits, Math.Max(0, result[2].AsInteger()));
        var granted = result[0].AsInteger() == 1;
        var retry = Math.Clamp(result[1].AsInteger(), 0, MaxTimeSpanMilliseconds);
        return new RedisRateLimitLease(granted, TimeSpan.FromMilliseconds(retry));
    }

    private async ValueTask<RateLimitLease> QueueAsync(int permitCount, RateLimitLease denied, CancellationToken cancellationToken)
    {
        var request = new QueuedRequest(permitCount, ReadRetryAfter(denied), cancellationToken);
        var startPump = false;
        lock (_queueGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
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
            var remaining = request.RemainingRetryAfter;
            var delay = remaining <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1)
                : remaining > MaxQueueDelay ? MaxQueueDelay : remaining;
            var timer = Task.Delay(delay, wakeCancellation.Token);
            var changed = _queueChanged.WaitAsync(wakeCancellation.Token);
            await Task.WhenAny(timer, changed).ConfigureAwait(false);
            if (!timer.IsCompleted)
            {
                wakeCancellation.Cancel();
                continue;
            }
            wakeCancellation.Cancel();
            lock (_queueGate)
            {
                if (request.Node?.List is null || request.Node != _queue.First) continue;
                request.IsProcessing = true;
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
            if (!lease.IsAcquired)
            {
                lock (_queueGate)
                {
                    request.IsProcessing = false;
                    request.SetRetryAfter(ReadRetryAfter(lease));
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
        _disposed = true;
        FailQueuedRequests(new ObjectDisposedException(nameof(RedisRateLimiter)));
        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _disposed = true;
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
    }

    private sealed class RedisRateLimitLease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames
            => acquired || retryAfter is null ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
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
