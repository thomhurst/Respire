using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;

namespace Respire.Extensions.Caching;

/// <summary>
/// An <see cref="IDistributedCache"/> (and <see cref="IBufferDistributedCache"/>, so HybridCache
/// takes its allocation-free path) backed by Redis through Respire. Entries use the same hash
/// layout as Microsoft.Extensions.Caching.StackExchangeRedis — fields <c>absexp</c>,
/// <c>sldexp</c> and <c>data</c> — so the two implementations can read each other's entries.
/// Unlike the Microsoft implementation, a read of a sliding-expiration entry refreshes the TTL
/// in the same round trip via a Lua script instead of issuing a second command.
/// </summary>
public sealed class RespireDistributedCache : IDistributedCache, IBufferDistributedCache, IAsyncDisposable, IDisposable
{
    private const long NotPresent = -1;

    // ARGV: [1] absolute expiration (UTC ticks, -1 none), [2] sliding expiration (ticks, -1 none),
    // [3] relative expiry (ms, -1 none), [4] payload. PERSIST clears a leftover TTL when an
    // existing entry is overwritten without one (HSET alone keeps the old TTL).
    private static readonly RespireScript SetScript = RespireScript.Create("""
        redis.call('HSET', KEYS[1], 'absexp', ARGV[1], 'sldexp', ARGV[2], 'data', ARGV[4])
        if ARGV[3] ~= '-1' then
          redis.call('PEXPIRE', KEYS[1], ARGV[3])
        else
          redis.call('PERSIST', KEYS[1])
        end
        return 1
        """);

    // ARGV: [1] '1' to return the payload ('0' for refresh-only), [2] current UTC ticks. For
    // sliding entries the TTL is re-armed to min(sliding, time left until absolute expiration),
    // atomically with the read. Ticks (100ns) to Redis milliseconds is a divide by 10000.
    // The TTL Redis already holds is the authority on expiry — absexp caps re-arming and never
    // deletes. The metadata can carry a writer's clock-offset quirk (the Microsoft
    // implementation stores DateTimeOffset.Ticks with the caller's offset baked in, then reads
    // them back as UTC), which makes a live key's absexp able to read as already past. The true
    // deadline is then unknowable, so no re-arm can be proven safe: a non-positive remainder
    // skips the re-arm and lets the write-time TTL run out. Such an entry loses sliding
    // refresh but is never served past its deadline nor deleted early (the Microsoft reader
    // deletes it on sight, via KeyExpire with a negative TimeSpan).
    private static readonly RespireScript GetAndRefreshScript = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'absexp', 'sldexp', 'data')
        if entry[1] == false and entry[3] == false then
          return nil
        end
        local sldexp = tonumber(entry[2]) or -1
        if sldexp ~= -1 then
          local ttl = math.floor(sldexp / 10000)
          local absexp = tonumber(entry[1]) or -1
          if absexp ~= -1 then
            local remaining = math.floor((absexp - tonumber(ARGV[2])) / 10000)
            if remaining < ttl then
              ttl = remaining
            end
          end
          if ttl > 0 then
            redis.call('PEXPIRE', KEYS[1], ttl)
          end
        end
        if ARGV[1] == '1' then
          return entry[3]
        end
        return 1
        """);

    // Removal goes through a script (rather than the client's UNLINK facet) so the caller's
    // cancellation token covers the wait for the reply, like every other cache operation.
    private static readonly RespireScript RemoveScript = RespireScript.Create("""
        redis.call('UNLINK', KEYS[1])
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly RespireClient? _ownedClient;

    /// <summary>Wraps an existing client; the caller keeps ownership of it.</summary>
    public RespireDistributedCache(IRespireClient client, RespireCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = ApplyInstanceName(client, options);
    }

    /// <summary>Owns <paramref name="ownedClient"/> — disposes it with the cache.</summary>
    internal RespireDistributedCache(RespireClient ownedClient, RespireCacheOptions? options)
    {
        _ownedClient = ownedClient;
        _client = ApplyInstanceName(ownedClient, options);
    }

    private static IRespireClient ApplyInstanceName(IRespireClient client, RespireCacheOptions? options)
        => string.IsNullOrEmpty(options?.InstanceName) ? client : client.WithKeyPrefix(options.InstanceName);

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        using var result = await RunGetScriptAsync(key, returnData: true, token).ConfigureAwait(false);
        return result.IsNull ? null : result.AsBytes();
    }

    public bool TryGet(string key, IBufferWriter<byte> destination)
        => TryGetAsync(key, destination).AsTask().GetAwaiter().GetResult();

    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        token.ThrowIfCancellationRequested();
        using var result = await RunGetScriptAsync(key, returnData: true, token).ConfigureAwait(false);
        if (result.IsNull)
        {
            return false;
        }

        destination.Write(result.AsSpan());
        return true;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetCoreAsync(key, value, options, token).AsTask();
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).AsTask().GetAwaiter().GetResult();

    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => SetCoreAsync(key, value.IsSingleSegment ? value.First : value.ToArray(), options, token);

    private async ValueTask SetCoreAsync(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var absoluteExpiration = GetAbsoluteExpiration(now, options);
        var result = await _client.Scripts.ExecuteAsync(
            SetScript,
            [key],
            [
                absoluteExpiration?.UtcTicks ?? NotPresent,
                options.SlidingExpiration?.Ticks ?? NotPresent,
                GetExpirationMilliseconds(now, absoluteExpiration, options),
                value,
            ],
            token).ConfigureAwait(false);
        result.Dispose();
    }

    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        var result = await RunGetScriptAsync(key, returnData: false, token).ConfigureAwait(false);
        result.Dispose();
    }

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        var result = await _client.Scripts.ExecuteAsync(RemoveScript, [key], args: null, token).ConfigureAwait(false);
        result.Dispose();
    }

    private ValueTask<RespireResult> RunGetScriptAsync(string key, bool returnData, CancellationToken token)
        => _client.Scripts.ExecuteAsync(
            GetAndRefreshScript, [key], [returnData ? "1" : "0", DateTimeOffset.UtcNow.UtcTicks], token);

    private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset now, DistributedCacheEntryOptions options)
    {
        if (options.AbsoluteExpiration is { } absolute && absolute <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration), absolute,
                "The absolute expiration value must be in the future.");
        }

        return options.AbsoluteExpirationRelativeToNow is { } relative ? now + relative : options.AbsoluteExpiration;
    }

    /// <summary>The TTL to arm at write time: min(sliding, time until absolute), or -1 for none.</summary>
    private static long GetExpirationMilliseconds(DateTimeOffset now, DateTimeOffset? absoluteExpiration, DistributedCacheEntryOptions options)
    {
        if (absoluteExpiration is { } absolute)
        {
            var untilAbsolute = (long)(absolute - now).TotalMilliseconds;
            return options.SlidingExpiration is { } sliding
                ? Math.Min(untilAbsolute, (long)sliding.TotalMilliseconds)
                : untilAbsolute;
        }

        return options.SlidingExpiration is { } slidingOnly ? (long)slidingOnly.TotalMilliseconds : NotPresent;
    }

    /// <summary>Disposes the client only when the cache created it (connection-string configuration).</summary>
    public ValueTask DisposeAsync() => _ownedClient?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>Synchronous counterpart, for containers that are disposed synchronously.</summary>
    public void Dispose()
    {
        if (_ownedClient is not null)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
