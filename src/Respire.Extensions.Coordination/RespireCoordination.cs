using System.Diagnostics;
using System.Globalization;

namespace Respire.Extensions.Coordination;

/// <summary>Coordination primitives using a caller-owned Redis client.</summary>
public sealed class RespireCoordination
{
    private static readonly TimeSpan BestEffortCleanupTimeout = TimeSpan.FromSeconds(1);
    private readonly IRespireClient _client;

    private readonly struct FencedLockWaitTarget(
        RespireCoordination coordination, RespireKey leaseKey, RespireKey counterKey, long milliseconds)
        : IRespireNotificationWaitTarget<RespireFencedLock>
    {
        public async ValueTask TrackAsync(CancellationToken cancellationToken)
            => _ = await coordination._client.Strings.GetStringAsync(leaseKey, cancellationToken).ConfigureAwait(false);

        public ValueTask<RespireTtl> GetTimeToLiveAsync(CancellationToken cancellationToken)
            => coordination._client.Keys.ExpiryAsync(leaseKey, cancellationToken);

        public async ValueTask<(bool Succeeded, RespireFencedLock Result)> TryAsync(CancellationToken cancellationToken)
        {
            var attempt = await coordination.AcquireAsync(leaseKey, counterKey, milliseconds, cancellationToken)
                .ConfigureAwait(false);
            return attempt.Acquired ? (true, attempt.Lock) : (false, default!);
        }
    }

    private readonly struct HashFieldLeaseWaitTarget(
        RespireCoordination coordination, RespireKey hashKey, RespireKey field, long milliseconds)
        : IRespireNotificationWaitTarget<RespireCoordinationLease>
    {
        public async ValueTask TrackAsync(CancellationToken cancellationToken)
            => _ = await coordination._client.Hashes.GetBytesAsync(hashKey, field, cancellationToken).ConfigureAwait(false);

        public ValueTask<RespireTtl> GetTimeToLiveAsync(CancellationToken cancellationToken)
            => coordination._client.Hashes.ExpiryAsync(hashKey, field, cancellationToken);

        public async ValueTask<(bool Succeeded, RespireCoordinationLease Result)> TryAsync(CancellationToken cancellationToken)
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            var lease = await coordination.TryAcquireLeaseAsync(hashKey, field, duration, cancellationToken)
                .ConfigureAwait(false);
            return lease is null ? (false, default!) : (true, lease);
        }
    }

    /// <summary>Creates coordination operations without taking ownership of <paramref name="client"/>.</summary>
    public RespireCoordination(IRespireClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    internal static readonly RespireScript CreateCountdownLatch = RespireScript.Create("""
        if redis.call('EXISTS', KEYS[1]) == 1 then return redis.error_reply('ERR latch already exists; use reset') end
        redis.call('HSET', KEYS[1], 'generation', ARGV[1], 'remaining', ARGV[2], 'channel', ARGV[3])
        return 1
        """);

    internal static readonly RespireScript ResetCountdownLatch = RespireScript.Create("""
        local oldChannel = redis.call('HGET', KEYS[1], 'channel')
        if oldChannel then redis.call('PUBLISH', oldChannel, ARGV[1]) end
        redis.call('HSET', KEYS[1], 'generation', ARGV[1], 'remaining', ARGV[2], 'channel', ARGV[3])
        return 1
        """);

    internal static readonly RespireScript CountDownLatch = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], 'generation') ~= ARGV[1] then return '-1' end
        if redis.call('HGET', KEYS[1], 'channel') ~= ARGV[2] then
            return redis.error_reply('ERR latch channel does not match generation')
        end
        local remaining = redis.call('HGET', KEYS[1], 'remaining')
        if not remaining or not string.match(remaining, '^%d+$')
            or (#remaining > 1 and string.sub(remaining, 1, 1) == '0') then
            return redis.error_reply('ERR latch count is invalid')
        end
        if remaining == '0' then return redis.error_reply('ERR latch count is already zero') end
        if remaining == '1' then redis.call('PUBLISH', ARGV[2], ARGV[1]) end
        redis.call('HINCRBY', KEYS[1], 'remaining', -1)
        return redis.call('HGET', KEYS[1], 'remaining')
        """);

    private const string ReadCountdownLatchSource = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return {'0', '', '', ''} end
        local values = redis.call('HMGET', KEYS[1], 'generation', 'remaining', 'channel')
        return {'1', values[1] or '', values[2] or '-1', values[3] or ''}
        """;

    internal static readonly RespireScript ReadCountdownLatch = RespireScript.Create(ReadCountdownLatchSource, readOnly: true);
    private static readonly RespireScript ReadCountdownLatchCompatibility =
        RespireScript.Create(ReadCountdownLatchSource, readOnly: false, cacheReadOnly: true);

    /// <summary>Creates a single-use countdown latch with an initial nonnegative count.</summary>
    /// <param name="key">The latch key, before the client's key prefix. On Cluster, use a hash tag if related keys are added by an application.</param>
    /// <param name="count">Initial number of signals required to release waiters.</param>
    /// <param name="cancellationToken">Cancels before or during the accepted Redis command.</param>
    public ValueTask<RespireCountdownLatch> CreateCountdownLatchAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => CreateLatchAsync(key, count, reset: false, cancellationToken);

    /// <summary>Joins the current countdown-latch generation stored at <paramref name="key"/>.</summary>
    /// <param name="key">The latch key, before the client's key prefix.</param>
    /// <param name="cancellationToken">Cancels the Redis state read.</param>
    /// <returns>A handle for the current generation, or <see langword="null"/> when no latch exists.</returns>
    public async ValueTask<RespireCountdownLatch?> JoinCountdownLatchAsync(
        RespireKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = key.Snapshot();
        using var result = await ReadLatchStateAsync(_client, snapshot, cancellationToken).ConfigureAwait(false);
        if (result[0].AsString() == "0") return null;
        var generation = result[1].AsString();
        var remainingText = result[2].AsString();
        var channel = result[3].AsString();
        if (generation.Length == 0 || channel.Length == 0
            || !long.TryParse(remainingText, NumberStyles.None, CultureInfo.InvariantCulture, out var remaining)
            || remaining < 0)
            throw new RespireProtocolException("Redis returned invalid countdown-latch state.");
        return new RespireCountdownLatch(_client, snapshot, generation, channel);
    }

    internal static async ValueTask<RespireResult> ReadLatchStateAsync(
        IRespireClient client, RespireKey key, CancellationToken cancellationToken)
    {
        try
        {
            return await client.Scripts.ExecuteAsync(ReadCountdownLatch, [key], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RespireServerException error) when (IsUnsupportedReadOnlyScriptCommand(error))
        {
            return await client.Scripts.ExecuteAsync(ReadCountdownLatchCompatibility, [key], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsUnsupportedReadOnlyScriptCommand(RespireServerException error)
        => error.Code == "ERR"
            && error.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            && (error.Message.Contains("EVALSHA_RO", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("EVAL_RO", StringComparison.OrdinalIgnoreCase));

    /// <summary>Atomically replaces the latch state with a fresh generation and count.</summary>
    /// <param name="key">The latch key, before the client's key prefix.</param>
    /// <param name="count">Nonnegative count for the new generation.</param>
    /// <param name="cancellationToken">Cancels before or during the accepted Redis command.</param>
    public ValueTask<RespireCountdownLatch> ResetCountdownLatchAsync(
        RespireKey key, long count, CancellationToken cancellationToken = default)
        => CreateLatchAsync(key, count, reset: true, cancellationToken);

    private async ValueTask<RespireCountdownLatch> CreateLatchAsync(
        RespireKey key, long count, bool reset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Latch count must be nonnegative.");
        var snapshot = key.Snapshot();
        var generation = Guid.NewGuid().ToString("N");
        var channel = "respire:latch:" + Guid.NewGuid().ToString("N");
        using var result = await _client.Scripts.ExecuteAsync(
            reset ? ResetCountdownLatch : CreateCountdownLatch,
            [snapshot], [generation, count, channel], cancellationToken).ConfigureAwait(false);
        return new RespireCountdownLatch(_client, snapshot, generation, channel);
    }

    internal static readonly RespireScript AcquireFencedLock = RespireScript.Create("""
        -- Keep the invariant even when this script is invoked without the managed entry point.
        if KEYS[1] == KEYS[2] then
            return redis.error_reply('ERR lock and fencing counter keys must differ')
        end
        if redis.call('EXISTS', KEYS[1]) == 1 then return false end
        local counter = redis.call('GET', KEYS[2])
        if counter and (not string.match(counter, '^%d+$') or (#counter > 1 and string.sub(counter, 1, 1) == '0')) then
            return redis.error_reply('ERR fencing counter must be a nonnegative canonical integer')
        end
        if redis.call('PTTL', KEYS[2]) >= 0 then
            return redis.error_reply('ERR fencing counter must not expire')
        end
        -- INCR rejects Int64 overflow (including oversized digit strings) before creating a lease.
        redis.call('INCR', KEYS[2])
        local fence = redis.call('GET', KEYS[2])
        redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2])
        return fence
        """);

    private static readonly RespireScript AcquireHashFieldLease = RespireScript.Create("""
        local capability = redis.pcall('HPTTL', KEYS[1], 'FIELDS', 1, ARGV[1])
        if type(capability) == 'table' and capability.err then
            if string.find(string.lower(capability.err), 'unknown', 1, true) then
                return redis.error_reply('ERR coordination leases require hash-field expiration (Redis 7.4+ or compatible server)')
            end
            return redis.error_reply(capability.err)
        end
        if redis.call('PTTL', KEYS[1]) >= 0 then
            return redis.error_reply('ERR coordination leases require a hash key without key expiration')
        end
        if redis.call('HSETNX', KEYS[1], ARGV[1], ARGV[2]) == 0 then return false end
        local expiry = redis.pcall('HPEXPIRE', KEYS[1], ARGV[3], 'FIELDS', 1, ARGV[1])
        if type(expiry) == 'table' and expiry.err then
            redis.call('HDEL', KEYS[1], ARGV[1])
            if string.find(string.lower(expiry.err), 'unknown', 1, true) then
                return redis.error_reply('ERR coordination leases require hash-field expiration (Redis 7.4+ or compatible server)')
            end
            return redis.error_reply(expiry.err)
        end
        if expiry[1] ~= 1 then
            redis.call('HDEL', KEYS[1], ARGV[1])
            return redis.error_reply('ERR coordination lease expiry could not be applied')
        end
        return 1
        """);

    private static readonly RespireScript RenewHashFieldLease = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end
        local expiry = redis.pcall('HPEXPIRE', KEYS[1], ARGV[3], 'FIELDS', 1, ARGV[1])
        if type(expiry) == 'table' and expiry.err then
            return redis.error_reply(expiry.err)
        end
        return expiry[1] == 1 and 1 or 0
        """);

    private static readonly RespireScript VerifyHashFieldLease = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end
        local ttl = redis.pcall('HPTTL', KEYS[1], 'FIELDS', 1, ARGV[1])
        if type(ttl) == 'table' and ttl.err then
            if string.find(string.lower(ttl.err), 'unknown', 1, true) then
                return redis.error_reply('ERR coordination leases require hash-field expiration (Redis 7.4+ or compatible server)')
            end
            return redis.error_reply(ttl.err)
        end
        return ttl[1] > 0 and 1 or 0
        """, readOnly: true);

    private static readonly RespireScript ReleaseHashFieldLease = RespireScript.Create("""
        if redis.call('HGET', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end
        return redis.call('HDEL', KEYS[1], ARGV[1])
        """);

    /// <summary>Immediately tries to acquire a lease with a new fencing token; contention returns an unacquired attempt.</summary>
    /// <param name="key">The lease key, before the client's prefix.</param>
    /// <param name="fencingCounterKey">A distinct, persistent counter dedicated to this lease key. Both keys must share a Cluster slot.</param>
    /// <param name="duration">A positive lease duration of at least one millisecond, truncated to whole milliseconds.</param>
    /// <param name="cancellationToken">Cancels waiting; an accepted command can still execute and its lease then expires naturally.</param>
    /// <remarks>
    /// Every contender must use the same key pair. Never delete, expire, evict or reset the counter.
    /// Tokens increase only within a retained Redis history: asynchronous failover or data restoration can roll them back.
    /// A protected resource must enforce fencing itself. This API is not a consensus-backed fencing service.
    /// A lost reply can consume a token and leave a lease until expiry; acquisition is never replayed after uncertain acceptance.
    /// </remarks>
    public ValueTask<RespireFencedLockAttempt> TryAcquireFencedLockAsync(
        RespireKey key, RespireKey fencingCounterKey, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var milliseconds = ValidateAcquisition(key, fencingCounterKey, duration);
        // Both inputs may wrap caller-owned binary buffers. Snapshot before the first await.
        return AcquireAsync(key.Snapshot(), fencingCounterKey.Snapshot(), milliseconds, cancellationToken);
    }

    /// <summary>Waits without polling until this client acquires a fenced lease.</summary>
    /// <param name="key">The lease key, before the client's prefix.</param>
    /// <param name="fencingCounterKey">A distinct persistent counter key in the same Cluster slot.</param>
    /// <param name="duration">A positive lease duration of at least one millisecond.</param>
    /// <param name="cancellationToken">Cancels the wait; an accepted acquisition can still execute and expire naturally.</param>
    /// <remarks>
    /// Requires RESP3 client-side caching/tracking. Subscribe-before-check ordering avoids missed
    /// wakeups; every wake retries the atomic acquisition script, so a notification never grants
    /// ownership. Notifications are hints and can be coalesced. The lease PTTL schedules one
    /// expiry wake if Redis delays its invalidation. Reconnect continuity loss wakes waiters to
    /// recheck. Client tracking must be active on connections to Cluster slot owners.
    /// Cancellation or connection loss after Redis accepts acquisition can leave an unreturned
    /// lease until its server-side duration elapses.
    /// </remarks>
    public async ValueTask<RespireFencedLock> AcquireFencedLockAsync(
        RespireKey key, RespireKey fencingCounterKey, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var milliseconds = ValidateAcquisition(key, fencingCounterKey, duration);
        var leaseKey = key.Snapshot();
        var counterKey = fencingCounterKey.Snapshot();
        return await RespireNotificationWaiter.WaitAsync<FencedLockWaitTarget, RespireFencedLock>(
            _client, leaseKey, new FencedLockWaitTarget(this, leaseKey, counterKey, milliseconds), cancellationToken)
            .ConfigureAwait(false);
    }

    private static long ValidateAcquisition(RespireKey key, RespireKey fencingCounterKey, TimeSpan duration)
    {
        var milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        if (key == fencingCounterKey)
            throw new ArgumentException("Lock and fencing counter keys must differ.", nameof(fencingCounterKey));
        return milliseconds;
    }

    /// <summary>Immediately tries to create a named lease in a Redis hash field.</summary>
    /// <param name="hashKey">The hash key, before the client's prefix.</param>
    /// <param name="field">The binary-safe lease name.</param>
    /// <param name="duration">A positive lease duration of at least one millisecond.</param>
    /// <param name="cancellationToken">Cancels this attempt; when acceptance is uncertain, Respire best-effort releases the owner-checked field.</param>
    /// <remarks>
    /// Requires Redis 7.4 or later and a hash key without key-level expiration. With a concrete
    /// <see cref="RespireClient"/>, reliable cleanup after an uncertain acquisition also requires
    /// Redis <c>CLIENT ID</c> and <c>CLIENT KILL</c> permissions.
    /// </remarks>
    public async ValueTask<RespireCoordinationLease?> TryAcquireLeaseAsync(
        RespireKey hashKey, RespireKey field, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var milliseconds = ValidateLease(hashKey, field, duration);
        cancellationToken.ThrowIfCancellationRequested();
        hashKey = hashKey.Snapshot();
        field = field.Snapshot();
        var owner = RespireLock.NewToken();
        var started = Stopwatch.GetTimestamp();
        var concreteClient = _client as RespireClient;
        RespireClient.TrackedScriptExecution? execution = null;
        bool acquired;
        try
        {
            if (concreteClient is null)
            {
                using var response = await _client.Scripts.ExecuteAsync(AcquireHashFieldLease, [hashKey],
                    [field, owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
                acquired = !response.IsNull && response.AsInteger() != 0;
            }
            else
            {
                await concreteClient.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
                execution = await concreteClient.StartTrackedScriptExecutionAsync(
                    AcquireHashFieldLease, [hashKey], [field, owner.Bytes, milliseconds], cancellationToken,
                    requireReliableCorrectionOrdering: true).ConfigureAwait(false);
                using var response = await execution.Response.ConfigureAwait(false);
                acquired = !response.IsNull && response.AsInteger() != 0;
            }
        }
        catch (RespireServerException)
        {
            // The script returned a definitive Redis error, so there is no uncertain acquisition to clean up.
            throw;
        }
        catch
        {
            await BestEffortReleaseHashFieldLeaseAsync(
                hashKey, field, owner, concreteClient, execution?.ConnectionIdentity ?? default).ConfigureAwait(false);
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            if (acquired)
            {
                await BestEffortReleaseHashFieldLeaseAsync(
                    hashKey, field, owner, concreteClient, execution?.ConnectionIdentity ?? default).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!acquired) return null;

        var appliedDuration = TimeSpan.FromMilliseconds(milliseconds);
        var acquiredTimestamp = execution?.StartedTimestamp ?? started;
        var lease = new RespireCoordinationLease(this, hashKey, field, owner, appliedDuration, acquiredTimestamp);
        if (lease.RemainingEstimate > TimeSpan.Zero) return lease;
        await BestEffortReleaseHashFieldLeaseAsync(
            hashKey, field, owner, concreteClient, execution?.ConnectionIdentity ?? default).ConfigureAwait(false);
        return null;
    }

    /// <summary>Waits for and acquires a named lease stored in a Redis hash field.</summary>
    /// <remarks>
    /// Requires RESP3 client-side caching/tracking and Redis 7.4 or later. With a concrete
    /// <see cref="RespireClient"/>, reliable cleanup after an uncertain acquisition also requires
    /// Redis <c>CLIENT ID</c> and <c>CLIENT KILL</c> permissions.
    /// </remarks>
    public async ValueTask<RespireCoordinationLease> AcquireLeaseAsync(
        RespireKey hashKey, RespireKey field, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var milliseconds = ValidateLease(hashKey, field, duration);
        cancellationToken.ThrowIfCancellationRequested();
        hashKey = hashKey.Snapshot();
        field = field.Snapshot();
        return await RespireNotificationWaiter.WaitAsync<HashFieldLeaseWaitTarget, RespireCoordinationLease>(
            _client, hashKey, new HashFieldLeaseWaitTarget(this, hashKey, field, milliseconds), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static long ValidateLease(RespireKey hashKey, RespireKey field, TimeSpan duration)
    {
        if (hashKey.IsEmpty) throw new ArgumentException("The hash key must not be empty.", nameof(hashKey));
        if (field.IsEmpty) throw new ArgumentException("The lease field must not be empty.", nameof(field));
        var milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        return milliseconds;
    }

    internal async ValueTask BestEffortReleaseHashFieldLeaseAsync(
        RespireKey hashKey,
        RespireKey field,
        RespireLockToken owner,
        RespireClient? concreteClient = null,
        RespireClient.TrackedConnectionIdentity connectionIdentity = default)
    {
        using var timeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        try
        {
            if (concreteClient is null)
            {
                _ = await ReleaseHashFieldLeaseAsync(hashKey, field, owner, timeout.Token).ConfigureAwait(false);
                return;
            }

            var correction = CorrectHashFieldLeaseAsync(
                concreteClient, hashKey, field, owner, connectionIdentity);
            try
            {
                await correction.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObserveCorrectionFailure(correction);
            }
        }
        catch
        {
            // The owner-checked lease expires naturally if cleanup cannot reach Redis.
        }
    }

    private static async Task CorrectHashFieldLeaseAsync(
        RespireClient client,
        RespireKey hashKey,
        RespireKey field,
        RespireLockToken owner,
        RespireClient.TrackedConnectionIdentity connectionIdentity)
    {
        Exception? originalFailure = null;
        Task? originalCorrection = null;
        if (connectionIdentity.Connection is not null)
        {
            try
            {
                originalCorrection = client.ExecuteOnAllConnectionsAsync(
                    ReleaseHashFieldLease, [hashKey], [field, owner.Bytes], connectionIdentity);
            }
            catch (Exception error)
            {
                originalFailure = error;
            }

            try
            {
                if (await client.HasDifferentSentinelGenerationAsync(connectionIdentity).ConfigureAwait(false))
                {
                    // The original generation preserves FIFO ordering for its accepted acquisition.
                    // Release the same owner field on the promoted generation as well.
                    await client.ExecuteOnAllConnectionsAsync(
                        ReleaseHashFieldLease, [hashKey], [field, owner.Bytes]).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                originalFailure ??= error;
            }

            if (originalCorrection is not null)
            {
                try { await originalCorrection.ConfigureAwait(false); }
                catch (Exception error) { originalFailure ??= error; }
            }
        }
        else
        {
            await client.ExecuteOnAllConnectionsAsync(
                ReleaseHashFieldLease, [hashKey], [field, owner.Bytes]).ConfigureAwait(false);
        }

        if (originalFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(originalFailure).Throw();
    }

    private static void ObserveCorrectionFailure(Task correction)
        => _ = correction.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    internal async ValueTask<bool> RenewHashFieldLeaseAsync(
        RespireKey hashKey, RespireKey field, RespireLockToken owner, long milliseconds, CancellationToken cancellationToken)
    {
        using var response = await _client.Scripts.ExecuteAsync(RenewHashFieldLease, [hashKey],
            [field, owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
        return response.AsInteger() == 1;
    }

    internal async ValueTask<bool> VerifyHashFieldLeaseAsync(
        RespireKey hashKey, RespireKey field, RespireLockToken owner, CancellationToken cancellationToken)
    {
        using var response = await _client.Scripts.ExecuteAsync(VerifyHashFieldLease, [hashKey],
            [field, owner.Bytes], cancellationToken).ConfigureAwait(false);
        return response.AsInteger() == 1;
    }

    internal async ValueTask<bool> ReleaseHashFieldLeaseAsync(
        RespireKey hashKey, RespireKey field, RespireLockToken owner, CancellationToken cancellationToken)
    {
        using var response = await _client.Scripts.ExecuteAsync(ReleaseHashFieldLease, [hashKey],
            [field, owner.Bytes], cancellationToken).ConfigureAwait(false);
        return response.AsInteger() == 1;
    }

    private async ValueTask<RespireFencedLockAttempt> AcquireAsync(
        RespireKey key, RespireKey counterKey, long milliseconds, CancellationToken cancellationToken)
    {
        var owner = RespireLock.NewToken();
        var started = Stopwatch.GetTimestamp();
        using var response = await _client.Scripts.ExecuteAsync(AcquireFencedLock, [key, counterKey],
            [owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
        if (response.IsNull) return default;

        var lease = new RespireLock(_client.Locks, key, owner,
            TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond), started);
        var transferred = false;
        try
        {
            // Lua numbers cannot represent every Int64. The script returns GET's decimal
            // bulk string after INCR, rather than converting INCR's Lua number back to RESP.
            if (!long.TryParse(response.AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var fence) || fence <= 0)
                throw new RespireProtocolException("Fencing acquisition did not return a positive Int64 token.");
            cancellationToken.ThrowIfCancellationRequested();
            // An already-expired lease is intentionally reported like contention: the caller
            // acquired no usable lease even though this attempt consumed a fencing token.
            if (lease.IsReleased) return default;
            var result = new RespireFencedLockAttempt(new RespireFencedLock(lease, counterKey, fence));
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
