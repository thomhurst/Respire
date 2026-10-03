using BenchmarkDotNet.Attributes;
#if RATE_LIMITER_API
using System.Threading.RateLimiting;
using Respire.Coordination;
#endif

namespace Respire.Benchmarks;

/// <summary>Measures each distributed limiter against the equivalent Redis script control.</summary>
[MemoryDiagnoser]
public class RedisRateLimiterBenchmarks
{
#if !RATE_LIMITER_API
    private static readonly RespireScript FixedWindowScript = RespireScript.Create("""
        local result = redis.pcall('INCREX', KEYS[1], 'BYINT', 1, 'UBOUND', 2147483647, 'PX', 60000, 'ENX')
        if type(result) == 'table' and not result.err then return tonumber(result[2]) end
        if not string.find(string.lower(result.err or ''), 'unknown', 1, true) then
            return redis.error_reply(result.err or 'ERR INCREX failed')
        end
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        if current >= 2147483647 then return 0 end
        current = current + 1
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 0 then redis.call('SET', KEYS[1], current, 'PX', 60000)
        else redis.call('SET', KEYS[1], current, 'KEEPTTL') end
        return 1
        """);

    private static readonly RespireScript SlidingWindowScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local width = 60000
        local segment = 6000
        local bucket = math.floor(now / segment) * segment
        local bucketEnd = bucket + segment
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - width)
        local entries = redis.call('ZRANGE', KEYS[1], 0, -1)
        local count = 0
        for _, entry in ipairs(entries) do
            local separator = string.find(entry, ':', 1, true)
            count = count + tonumber(string.sub(entry, separator + 1))
        end
        if count >= 2147483647 then return 0 end
        local current = redis.call('ZRANGEBYSCORE', KEYS[1], bucketEnd, bucketEnd)
        local segmentCount = 0
        if #current > 0 then
            local separator = string.find(current[1], ':', 1, true)
            segmentCount = tonumber(string.sub(current[1], separator + 1))
            redis.call('ZREM', KEYS[1], current[1])
        end
        redis.call('ZADD', KEYS[1], bucketEnd, bucket .. ':' .. (segmentCount + 1))
        redis.call('PEXPIRE', KEYS[1], width * 2)
        return 1
        """);

    private static readonly RespireScript TokenBucketScript = RespireScript.Create("""
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local last = tonumber(redis.call('HGET', KEYS[1], 'time') or now)
        local tokens = tonumber(redis.call('HGET', KEYS[1], 'tokens') or 2147483647)
        local elapsed = math.max(0, now - last)
        tokens = math.min(2147483647, tokens + math.floor(elapsed / 1000) * 100)
        last = last + math.floor(elapsed / 1000) * 1000
        if tokens < 1 then return 0 end
        tokens = tokens - 1
        redis.call('HSET', KEYS[1], 'tokens', tokens, 'time', last)
        redis.call('PEXPIRE', KEYS[1], 42949674000)
        return 1
        """);
#endif

    private RespireClient _client = null!;
    private RespireKey _fixedKey;
    private RespireKey _slidingKey;
    private RespireKey _bucketKey;
#if !RATE_LIMITER_API
    private long _operationId;
#endif
#if RATE_LIMITER_API
    private RateLimiter _fixed = null!;
    private RateLimiter _sliding = null!;
    private RateLimiter _bucket = null!;
#endif

    [GlobalSetup]
    public async Task Setup()
    {
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1,
            Protocol = RespProtocol.Resp3,
        });
        var tag = Guid.NewGuid().ToString("N");
        _fixedKey = $"{{{tag}}}:fixed";
        _slidingKey = $"{{{tag}}}:sliding";
        _bucketKey = $"{{{tag}}}:bucket";
#if RATE_LIMITER_API
        var limiters = new RespireCoordination(_client).RateLimiters;
        _fixed = limiters.FixedWindow(_fixedKey, int.MaxValue, TimeSpan.FromMinutes(1));
        _sliding = limiters.SlidingWindow(_slidingKey, int.MaxValue, TimeSpan.FromMinutes(1), segments: 10);
        _bucket = limiters.TokenBucket(_bucketKey, int.MaxValue, 100, TimeSpan.FromSeconds(1));
#endif
        await FixedWindow();
        await SlidingWindow();
        await TokenBucket();
#if !RATE_LIMITER_API
        _operationId = 0;
#endif
    }

    [Benchmark]
    public Task FixedWindow()
    {
#if RATE_LIMITER_API
        return AcquireAsync(_fixed);
#else
        return ExecuteControlAsync(FixedWindowScript, _fixedKey, []);
#endif
    }

    [Benchmark]
    public Task SlidingWindow()
    {
#if RATE_LIMITER_API
        return AcquireAsync(_sliding);
#else
        return ExecuteControlAsync(SlidingWindowScript, _slidingKey, [Interlocked.Increment(ref _operationId).ToString()]);
#endif
    }

    [Benchmark]
    public Task TokenBucket()
    {
#if RATE_LIMITER_API
        return AcquireAsync(_bucket);
#else
        return ExecuteControlAsync(TokenBucketScript, _bucketKey, []);
#endif
    }

#if RATE_LIMITER_API
    private static async Task AcquireAsync(RateLimiter limiter)
    {
        using var lease = await limiter.AcquireAsync(1).ConfigureAwait(false);
        if (!lease.IsAcquired) throw new InvalidOperationException("Benchmark limiter unexpectedly denied a permit.");
    }
#else
    private async Task ExecuteControlAsync(RespireScript script, RespireKey key, RespireValue[] args)
    {
        using var result = await _client.Scripts.ExecuteAsync(script, [key], args).ConfigureAwait(false);
        if (result.AsInteger() != 1) throw new InvalidOperationException("Benchmark control unexpectedly denied a permit.");
    }
#endif

    [GlobalCleanup]
    public async Task Cleanup()
    {
#if RATE_LIMITER_API
        await _fixed.DisposeAsync();
        await _sliding.DisposeAsync();
        await _bucket.DisposeAsync();
#endif
        await _client.DeleteAsync(_fixedKey);
        await _client.DeleteAsync(_slidingKey);
        await _client.DeleteAsync(_bucketKey);
        await _client.DisposeAsync();
    }
}
