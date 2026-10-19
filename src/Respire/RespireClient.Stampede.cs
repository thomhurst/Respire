using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Respire.Internal;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

public sealed partial class RespireClient
{
    /// <inheritdoc cref="IRespireClient.GetOrSetAsync{T}(RespireKey, RespireCacheAsideOptions, Func{CancellationToken, ValueTask{T}}, RespireExpiry, CancellationToken)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> GetOrSetAsync<T>(RespireKey key, RespireCacheAsideOptions options,
        Func<CancellationToken, ValueTask<T?>> factory, RespireExpiry expiry, CancellationToken cancellationToken = default)
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

            // An expired payload can retain its generation while a refresh is in flight.
            var version = entry[3].AsString();
            bool acquired;
            {
                using var attempt = await AcquireProtectedCacheAsync(key, lockKey, token, options.DistributedSingleFlight,
                    [version, token, (long)options.LockTimeout.TotalMilliseconds, options.DistributedSingleFlight ? 1 : 0],
                    cancellationToken, observation).ConfigureAwait(false);
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
        // Reuse the safe deadline timer: throwing factory callbacks must not escape a timer thread.
        using var lease = new DedicatedAcquisitionCancellation(cancellationToken);
        if (options.DistributedSingleFlight)
            lease.Arm(CommandDeadline.After((long)options.LockTimeout.TotalMilliseconds));
        try
        {
            var started = Stopwatch.GetTimestamp();
            var created = await factory(lease.Token).ConfigureAwait(false);
            lease.Token.ThrowIfCancellationRequested();
            var duration = Math.Max(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
            // Losing publication is a cache race, not a factory failure. Keep the fencing
            // decision and return an independent copy of this caller's computed value.
            using var computed = Protocol.RespValue.BulkString(bytes);
            return DeserializeBorrowed<T>(in computed);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && lease.IsCancellationRequested)
        {
            throw new TimeoutException("The cache-aside factory exceeded its lease duration.", error);
        }
        finally
        {
            if (options.DistributedSingleFlight && !published)
                await ReleaseProtectedCacheAsync(key, lockKey, token, observation).ConfigureAwait(false);
        }
    }

    private async ValueTask<RespireResult> AcquireProtectedCacheAsync(RespireKey key, RespireKey lockKey, string token, bool singleFlight,
        RespireValue[] args, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
    {
        if (!singleFlight)
            return await RunCacheAsideScriptAsync(ProtectedCacheAcquire, key, lockKey, args, cancellationToken, observation)
                .ConfigureAwait(false);
        await EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
        var execution = await StartTrackedScriptExecutionAsync(ProtectedCacheAcquire, [key, lockKey], args,
            cancellationToken, requireReliableCorrectionOrdering: true, errorObservation: observation, physicalKeys: true)
            .ConfigureAwait(false);
        // A cancellation may abandon a reply after SET took effect. Fence the submitted
        // connection before cleanup, so a delayed acquire cannot follow its own release.
        var response = ExecuteWithCorrectionAsync(execution, CorrectionOrdering.OrderedCorrection,
            (Client: this, Key: key, LockKey: lockKey, Token: token, Observation: observation),
            static (s, identity) => s.Client.CorrectProtectedCacheAcquisitionAsync(s.Key, s.LockKey, s.Token, identity, s.Observation));
        return await RespireResult.MaterializeAsync(response).ConfigureAwait(false);
    }

    private async ValueTask CorrectProtectedCacheAcquisitionAsync(RespireKey key, RespireKey lockKey, string token,
        TrackedConnectionIdentity identity, RespireTelemetry.ErrorObservation observation)
    {
        using var cleanup = new CancellationTokenSource(_core.Options.ConnectTimeout);
        try
        {
            await FenceCorrectionConnectionAsync(identity, cleanup.Token).ConfigureAwait(false);
            using var released = await RunCacheAsideScriptAsync(ProtectedCacheRelease, key, lockKey,
                [token], cleanup.Token, observation).ConfigureAwait(false);
        }
        catch (Exception) { /* Preserve the acquisition failure; PX bounds cleanup that cannot reach Redis. */ }
    }

    private async ValueTask ReleaseProtectedCacheAsync(RespireKey key, RespireKey lockKey, string token,
        RespireTelemetry.ErrorObservation observation)
    {
        try
        {
            using var released = await RunCacheAsideScriptAsync(ProtectedCacheRelease, key, lockKey,
                [token], CancellationToken.None, observation).ConfigureAwait(false);
        }
        catch (Exception) { /* Cleanup cannot mask the original failure; PX bounds a failed release. */ }
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
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        RespireKey[] keys = [key, lockKey];
        var tail = BuildPhysicalCacheAsideScriptTail(keys, args);
        var cache = _core.ClientCache;
        var fence = cache is null || script.IsCacheReadOnly ? default
            : cache.BeginKeysMutation(ReferenceEquals(script, ProtectedCacheRelease) ? [lockKey] : keys);
        var response = ExecuteScriptWithRetriesAsync(script, tail, cancellationToken, fence, observation);
        return RespireResult.MaterializeAsync(fence.IsRequired ? CompleteMutationAsync(response, cache!, fence) : response);
    }

    private static RespireValue[] BuildPhysicalCacheAsideScriptTail(RespireKey[] keys, ReadOnlySpan<RespireValue> args)
    {
        var tail = new RespireValue[1 + keys.Length + args.Length];
        tail[0] = keys.Length;
        for (var index = 0; index < keys.Length; index++) tail[index + 1] = keys[index].AsValue();
        args.CopyTo(tail.AsSpan(1 + keys.Length));
        return tail;
    }

    // Audited as non-mutating, but use EVAL so a replica read policy cannot route coordination reads away from the primary.
    private static readonly RespireScript ProtectedCacheRead = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'value', 'duration', 'version', 'stale', 'retained')
        if not entry[1] then return {false, 0, 0, ''} end
        local ttl = redis.call('PTTL', KEYS[1]) - tonumber(entry[5] or '0')
        if ttl <= 0 then return {false, 0, 0, entry[3]} end
        return {entry[1], ttl - tonumber(entry[4]), entry[2], entry[3]}
        """, readOnly: false, cacheReadOnly: true);

    private static readonly RespireScript ProtectedCacheAcquire = RespireScript.Create("""
        if (redis.call('HGET', KEYS[1], 'version') or '') ~= ARGV[1] then return 0 end
        if ARGV[4] == '1' and not redis.call('SET', KEYS[2], ARGV[2], 'NX', 'PX', ARGV[3]) then return 0 end
        local ttl = redis.call('PTTL', KEYS[1])
        local lease = tonumber(ARGV[3])
        if ttl >= 0 and ttl < lease then
          redis.call('HINCRBY', KEYS[1], 'retained', lease - ttl)
          redis.call('PEXPIRE', KEYS[1], lease)
        end
        return 1
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
          redis.call('HSET', KEYS[1], 'value', ARGV[4], 'duration', ARGV[5], 'version', ARGV[2], 'stale', ARGV[8], 'retained', 0)
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
