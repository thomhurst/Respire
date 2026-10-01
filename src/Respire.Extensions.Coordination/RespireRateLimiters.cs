using System.Threading.RateLimiting;

namespace Respire.Extensions.Coordination;

/// <summary>Creates Redis-backed implementations of .NET rate limiters.
/// Acquisitions are asynchronous; synchronous attempts are unsupported because they require a network round trip.</summary>
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
    private static readonly RespireScript FixedWindowScript = RespireScript.Create("""
        local width = tonumber(ARGV[1])
        local requested = tonumber(ARGV[2])
        local limit = tonumber(ARGV[3])
        local result = redis.pcall('INCREX', KEYS[1], 'BYINT', requested, 'UBOUND', limit, 'PX', width, 'ENX')
        if type(result) == 'table' and not result.err then
            local current = tonumber(result[1])
            local applied = tonumber(result[2])
            if applied == requested then return {1, 0, limit - current} end
            return {0, math.max(1, redis.call('PTTL', KEYS[1])), limit - current}
        end
        if not string.find(string.lower(result.err or ''), 'unknown', 1, true) then
            return redis.error_reply(result.err or 'ERR INCREX failed')
        end
        local value = tonumber(redis.call('GET', KEYS[1]) or '0')
        local ttl = redis.call('PTTL', KEYS[1])
        if value + requested <= limit then
            value = value + requested
            if ttl < 0 then redis.call('SET', KEYS[1], value, 'PX', width)
            else redis.call('SET', KEYS[1], value, 'KEEPTTL') end
            return {1, 0, limit - value}
        end
        return {0, math.max(1, ttl), limit - value}
        """);

    private static readonly RespireScript SlidingWindowScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local width = tonumber(ARGV[1])
        local segment = math.ceil(width / tonumber(ARGV[2]))
        local bucket = math.floor(now / segment) * segment
        local cutoff = now - width
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', cutoff)
        local count = redis.call('ZCARD', KEYS[1])
        local requested = tonumber(ARGV[3])
        local limit = tonumber(ARGV[4])
        if count + requested <= limit then
            for i = 1, requested do
                redis.call('ZADD', KEYS[1], bucket, ARGV[5] .. ':' .. i)
            end
            redis.call('PEXPIRE', KEYS[1], width * 2)
            return {1, 0, limit - count - requested}
        end
        local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        local retry = tonumber(oldest[2]) + width - now
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
            redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', now)
            redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) * math.ceil(tonumber(ARGV[1]) / tonumber(ARGV[3])) * 2)
            return {1, 0, tokens}
        end
        local missing = requested - tokens
        local retry = math.ceil(missing / tonumber(ARGV[3])) * tonumber(ARGV[2]) - (now - last)
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', last)
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
    private readonly object _queueGate = new();
    private readonly LinkedList<QueuedRequest> _queue = [];
    private readonly SemaphoreSlim _queueChanged = new(0, 1);
    private int _queuedPermits;
    private bool _pumpRunning;
    private volatile bool _disposed;

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
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        if (permitCount < 0 || permitCount > _permitLimit) throw new ArgumentOutOfRangeException(nameof(permitCount));
        if (_disposed) throw new ObjectDisposedException(nameof(RedisRateLimiter));
        throw new NotSupportedException("Redis-backed rate limiters require asynchronous acquisition. Use AcquireAsync.");
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (permitCount < 0 || permitCount > _permitLimit) throw new ArgumentOutOfRangeException(nameof(permitCount));
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _queuedPermits) != 0)
        {
            if (_queueLimit == 0) return new RedisRateLimitLease(false, TimeSpan.Zero);
            return await QueueAsync(permitCount, new RedisRateLimitLease(false, TimeSpan.FromMilliseconds(1)), cancellationToken)
                .ConfigureAwait(false);
        }
        var lease = await AcquireFromRedisAsync(permitCount, cancellationToken).ConfigureAwait(false);
        if (lease.IsAcquired || _queueLimit == 0) return lease;
        return await QueueAsync(permitCount, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RateLimitLease> AcquireFromRedisAsync(int permitCount, CancellationToken cancellationToken)
    {
        var unique = Guid.NewGuid().ToString("N");
        RespireValue[] args = _kind switch
        {
            RedisRateLimiterKind.FixedWindow => [_periodMs, permitCount, _permitLimit],
            RedisRateLimiterKind.SlidingWindow => [_periodMs, _segments, permitCount, _permitLimit, unique],
            _ => [_permitLimit, _periodMs, _tokensPerPeriod, permitCount],
        };
        var script = _kind switch
        {
            RedisRateLimiterKind.FixedWindow => FixedWindowScript,
            RedisRateLimiterKind.SlidingWindow => SlidingWindowScript,
            _ => TokenBucketScript,
        };
        using var result = await _coordination.ExecuteRateLimitScriptAsync(script, _key, args, cancellationToken)
            .ConfigureAwait(false);
        if (result.Count != 3) throw new RespireProtocolException("Rate-limit script returned an invalid response.");
        var granted = result[0].AsInteger() == 1;
        var retry = Math.Max(0, result[1].AsInteger());
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
                && _queuedPermits + permitCount > _queueLimit)) return denied;
            while (_queueOrder == QueueProcessingOrder.NewestFirst && _queuedPermits + permitCount > _queueLimit)
            {
                var removed = _queue.Last!;
                if (removed.Value.IsProcessing) return denied;
                _queue.RemoveLast();
                removed.Value.Node = null;
                _queuedPermits -= removed.Value.PermitCount;
                removed.Value.Registration.Unregister();
                removed.Value.Completion.TrySetResult(new RedisRateLimitLease(false, TimeSpan.Zero));
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
            var delay = request.RetryAfter <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1)
                : request.RetryAfter > MaxQueueDelay ? MaxQueueDelay : request.RetryAfter;
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
                    request.RetryAfter = ReadRetryAfter(lease);
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
        public TimeSpan RetryAfter { get; set; } = retryAfter;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<RateLimitLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public LinkedListNode<QueuedRequest>? Node { get; set; }
        public bool IsProcessing { get; set; }
    }

    private sealed class RedisRateLimitLease(bool acquired, TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames => acquired ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (!acquired && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
