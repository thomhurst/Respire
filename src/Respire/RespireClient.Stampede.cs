using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Respire.Internal;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

public sealed partial class RespireClient
{
    /// <inheritdoc cref="IRespireClient.GetOrSetAsync{T}(RespireKey, Func{CancellationToken, ValueTask{T}}, RespireExpiry, RespireCacheAsideOptions, CancellationToken)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetOrSetAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, RespireCacheAsideOptions options, CancellationToken cancellationToken = default)
        => DispatchResponseSource<T?>.Run(
            (Client: this, Key: key, Factory: factory, Expiry: expiry, Options: options, Token: cancellationToken),
            static (s, observation) => s.Client.GetOrSetProtectedAsync(
                s.Key, s.Factory, s.Expiry, s.Options, s.Token, observation));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<T?> GetOrSetProtectedAsync<T>(RespireKey key, Func<CancellationToken, ValueTask<T?>> factory,
        RespireExpiry expiry, RespireCacheAsideOptions options, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        ValidateCacheAside(factory, expiry);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var physicalKey = ResolveKey(key).Snapshot();
        return _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "GETORSET", cancellationToken,
                (Client: this, Key: physicalKey, Factory: factory, Expiry: expiry, Options: options,
                    Token: cancellationToken, Observation: observation),
                static s => s.Client.GetOrSetProtectedUnhookedAsync(
                    s.Key, s.Factory, s.Expiry, s.Options, s.Token, s.Observation))
            : GetOrSetProtectedUnhookedAsync(physicalKey, factory, expiry, options, cancellationToken, observation);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> GetOrSetProtectedUnhookedAsync<T>(RespireKey key,
        Func<CancellationToken, ValueTask<T?>> factory, RespireExpiry expiry, RespireCacheAsideOptions options,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var lockKey = CreateCacheAsideLockKey(key);
        var token = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var entry = await RunCacheAsideScriptAsync(ProtectedCacheRead, key, lockKey, [],
                cancellationToken, observation).ConfigureAwait(false);
            var found = !entry[0].IsNull;
            var fresh = found && entry[1].AsInteger() > 0;
            if (fresh && !ShouldRefreshCacheAside(entry[1].AsInteger(), entry[2].AsDouble(),
                    options.EarlyRefreshBeta, Random.Shared.NextDouble()))
                return entry[0].As<T>();

            var version = found ? entry[3].AsString() : "";
            var acquired = !options.DistributedSingleFlight;
            if (!acquired)
            {
                using var attempt = await RunCacheAsideScriptAsync(ProtectedCacheAcquire, key, lockKey,
                    [version, token, (long)options.LockTimeout.TotalMilliseconds], cancellationToken, observation)
                    .ConfigureAwait(false);
                acquired = attempt.AsInteger() == 1;
            }
            if (acquired)
                return await PopulateProtectedCacheAsync(key, lockKey, token, version, factory, expiry,
                    options, cancellationToken, observation).ConfigureAwait(false);

            // A refresh contender can use the value it just read. Missing values never bypass the lease.
            if (fresh || (found && options.StaleWhileRevalidate > TimeSpan.Zero)) return entry[0].As<T>();
            var remaining = options.WaitTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Timed out waiting for the cache-aside factory lease.");
            await Task.Delay(remaining < options.RetryDelay ? remaining : options.RetryDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private async ValueTask<T?> PopulateProtectedCacheAsync<T>(RespireKey key, RespireKey lockKey,
        string token, string version, Func<CancellationToken, ValueTask<T?>> factory, RespireExpiry expiry,
        RespireCacheAsideOptions options, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        var published = false;
        try
        {
            // Reuse the safe deadline timer: throwing factory callbacks must not escape a timer thread.
            using var lease = new DedicatedAcquisitionCancellation(cancellationToken);
            if (options.DistributedSingleFlight)
                lease.Arm(CommandDeadline.After((long)options.LockTimeout.TotalMilliseconds));
            var started = Stopwatch.GetTimestamp();
            var created = await factory(lease.Token).ConfigureAwait(false);
            var duration = Math.Max(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            lease.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_core.Disposed, this);
            if (created is null) return default;
            var serialized = Serialize(created);
            RespireValue.ThrowIfNull(serialized, nameof(factory));
            var bytes = new byte[serialized.GetWireLength()];
            serialized.WriteWirePayload(bytes);
            var absolute = expiry.TryGetAbsoluteUnixMilliseconds(out var milliseconds);
            if (!absolute) expiry.TryGetRelativeMilliseconds(out milliseconds);
            using var result = await RunCacheAsideScriptAsync(ProtectedCachePublish, key, lockKey,
                [version, token, options.DistributedSingleFlight ? 1 : 0, bytes, duration,
                    milliseconds, absolute ? 1 : 0, (long)options.StaleWhileRevalidate.TotalMilliseconds],
                lease.Token, observation).ConfigureAwait(false);
            published = result.AsInteger() == 1;
            if (published)
            {
                // Return through the serializer so callers never share the factory's mutable object.
                using var value = Protocol.RespValue.BulkString(bytes);
                return DeserializeBorrowed<T>(in value);
            }
            using var winner = await RunCacheAsideScriptAsync(ProtectedCacheRead, key, lockKey, [],
                cancellationToken, observation).ConfigureAwait(false);
            if (!winner[0].IsNull && winner[1].AsInteger() > 0) return winner[0].As<T>();
            throw new TimeoutException("The cache-aside lease or value changed before publication.");
        }
        finally
        {
            if (options.DistributedSingleFlight && !published)
            {
                // Cleanup must not mask a factory failure or cancellation. PX still bounds a failed release.
                try
                {
                    using var released = await RunCacheAsideScriptAsync(ProtectedCacheRelease, key, lockKey,
                        [token], CancellationToken.None, observation).ConfigureAwait(false);
                }
                catch (Exception) { /* The token-owned lease expires on Redis. */ }
            }
        }
    }

    internal static bool ShouldRefreshCacheAside(long remainingMilliseconds, double durationMilliseconds,
        double beta, double sample)
        => beta > 0 && -durationMilliseconds * beta * Math.Log(sample) >= remainingMilliseconds;

    internal static RespireKey CreateCacheAsideLockKey(RespireKey physicalKey)
    {
        // Deterministic, binary-safe, and in the data key's slot even without a user-supplied hash tag.
        ReadOnlySpan<byte> prefix = "respire:cache-aside:v1:"u8;
        var bytes = new byte[4 + prefix.Length + SHA256.HashSizeInBytes];
        bytes[0] = (byte)'{';
        ClusterHash.WriteRemovalLeaseTag(physicalKey.ClusterSlot, bytes.AsSpan(1, 2));
        bytes[3] = (byte)'}';
        prefix.CopyTo(bytes.AsSpan(4));
        SHA256.HashData(physicalKey.ToBytes(), bytes.AsSpan(4 + prefix.Length));
        return new RespireKey(bytes);
    }

    private ValueTask<RespireResult> RunCacheAsideScriptAsync(RespireScript script, RespireKey key,
        RespireKey lockKey, ReadOnlySpan<RespireValue> args, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        // Keys are already physical. Do not apply a prefix twice. Borrow the logical GETORSET owner and hook.
        var tail = new RespireValue[3 + args.Length];
        tail[0] = 2;
        tail[1] = key.AsValue();
        tail[2] = lockKey.AsValue();
        args.CopyTo(tail.AsSpan(3));
        return ExecuteScriptResultUnhookedAsync(script, tail, cancellationToken, owned: true, observation);
    }

    // Audited as non-mutating, but use EVAL so a replica read policy cannot route coordination reads away from the primary.
    private static readonly RespireScript ProtectedCacheRead = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'value', 'duration', 'version', 'stale')
        if not entry[1] then return {false, 0, 0, ''} end
        return {entry[1], redis.call('PTTL', KEYS[1]) - tonumber(entry[4]), entry[2], entry[3]}
        """, readOnly: false, cacheReadOnly: true);

    private static readonly RespireScript ProtectedCacheAcquire = RespireScript.Create("""
        if (redis.call('HGET', KEYS[1], 'version') or '') ~= ARGV[1] then return 0 end
        if redis.call('SET', KEYS[2], ARGV[2], 'NX', 'PX', ARGV[3]) then return 1 end
        return 0
        """);

    private static readonly RespireScript ProtectedCachePublish = RespireScript.Create("""
        if ARGV[3] == '1' and redis.call('GET', KEYS[2]) ~= ARGV[2] then return 0 end
        if (redis.call('HGET', KEYS[1], 'version') or '') ~= ARGV[1] then return 0 end
        local ttl = tonumber(ARGV[6])
        if ARGV[7] == '1' then
          local now = redis.call('TIME')
          ttl = ttl - (tonumber(now[1]) * 1000 + math.floor(tonumber(now[2]) / 1000))
        end
        if ttl > 0 then
          redis.call('HSET', KEYS[1], 'value', ARGV[4], 'duration', ARGV[5], 'version', ARGV[2], 'stale', ARGV[8])
          redis.call('PEXPIRE', KEYS[1], ttl + tonumber(ARGV[8]))
        else
          redis.call('DEL', KEYS[1])
        end
        if ARGV[3] == '1' then redis.call('DEL', KEYS[2]) end
        return 1
        """);

    private static readonly RespireScript ProtectedCacheRelease = RespireScript.Create("""
        if redis.call('GET', KEYS[2]) == ARGV[1] then return redis.call('DEL', KEYS[2]) end
        return 0
        """);
}
