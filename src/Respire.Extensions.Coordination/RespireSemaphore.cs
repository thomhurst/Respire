using System.Diagnostics;

namespace Respire.Extensions.Coordination;

/// <summary>Runs immediate, bounded permit acquisition against one Redis key.</summary>
/// <remarks>
/// Every contender for a key must use the same capacity. Capacity changes fail while any
/// permit remains active. Acquisitions do not queue and provide no fairness guarantee.
/// </remarks>
public sealed class RespireSemaphore
{
    private readonly IRespireClient _client;

    /// <summary>Creates a semaphore view over a dedicated Redis key.</summary>
    /// <param name="client">Caller-owned Redis client.</param>
    /// <param name="key">Dedicated key before the client's configured prefix.</param>
    /// <param name="capacity">Maximum active permits; must be positive.</param>
    public RespireSemaphore(IRespireClient client, RespireKey key, int capacity)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Key = key.Snapshot();
        Capacity = capacity;
    }

    /// <summary>The semaphore key before the client's configured prefix.</summary>
    public RespireKey Key { get; }

    /// <summary>Maximum active permits configured by this view.</summary>
    public int Capacity { get; }

    /// <summary>Immediately tries to acquire a permit with optional server-side expiry.</summary>
    /// <param name="expiry">Expiry of at least one millisecond, truncated to whole milliseconds, or null for owner-only release.</param>
    /// <param name="cancellationToken">Cancels the command. Uncertain outcomes trigger owner-token cleanup; finite expiry is fallback.</param>
    public ValueTask<RespireSemaphorePermitAttempt> TryAcquireAsync(
        TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var milliseconds = expiry?.Ticks / TimeSpan.TicksPerMillisecond ?? 0;
        if (expiry.HasValue && milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Permit expiry must be at least one millisecond.");
        return TryAcquireCoreAsync(milliseconds, cancellationToken);
    }

    private async ValueTask<RespireSemaphorePermitAttempt> TryAcquireCoreAsync(
        long milliseconds, CancellationToken cancellationToken)
    {
        var owner = RespireLock.NewToken();
        var started = Stopwatch.GetTimestamp();
        bool acquired;
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(AcquireScript, [Key],
                [Capacity, owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
            acquired = response.AsInteger() == 1;
        }
        catch
        {
            await ReleaseBestEffortAsync(owner).ConfigureAwait(false);
            throw;
        }
        if (!acquired) return default;

        var completed = Stopwatch.GetTimestamp();
        TimeSpan? expiry = milliseconds == 0 ? null : TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
        var remaining = expiry - Stopwatch.GetElapsedTime(started, completed);
        if (remaining.HasValue && remaining.Value <= TimeSpan.Zero)
        {
            await ReleaseBestEffortAsync(owner).ConfigureAwait(false);
            return default;
        }

        var permit = new RespireSemaphorePermit(_client, Key, owner, Capacity, expiry, remaining, completed);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new RespireSemaphorePermitAttempt(permit);
        }
        catch (OperationCanceledException)
        {
            await permit.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ReleaseBestEffortAsync(RespireLockToken owner)
    {
        try
        {
            using var _ = await _client.Scripts.ExecuteAsync(ReleaseScript, [Key], [owner.Bytes], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    internal static readonly RespireScript AcquireScript = RespireScript.Create("""
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)

        local requestedCapacity = 'C:' .. ARGV[1]
        local members = redis.call('ZRANGE', KEYS[1], 0, -1)
        local storedCapacity = nil
        for _, member in ipairs(members) do
            if string.sub(member, 1, 2) == 'C:' then
                storedCapacity = member
                break
            end
        end
        if storedCapacity and storedCapacity ~= requestedCapacity then
            if redis.call('ZCARD', KEYS[1]) > 1 then
                return redis.error_reply('ERR semaphore capacity cannot change while permits are active')
            end
            redis.call('ZREM', KEYS[1], storedCapacity)
            storedCapacity = nil
        end
        if not storedCapacity then redis.call('ZADD', KEYS[1], '-inf', requestedCapacity) end

        local active = redis.call('ZCARD', KEYS[1]) - 1
        if active >= tonumber(ARGV[1]) then return 0 end
        local expiry = tonumber(ARGV[3])
        local score = expiry == 0 and math.huge or now + expiry
        local added = redis.call('ZADD', KEYS[1], 'NX', score, 'P:' .. ARGV[2])
        if added == 0 then return 0 end

        local entries = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local latest = 0
        for i = 1, #entries, 2 do
            local current = tonumber(entries[i + 1])
            if current == math.huge then
                redis.call('PERSIST', KEYS[1])
                return 1
            end
            if current > latest then latest = current end
        end
        if latest > 0 then redis.call('PEXPIREAT', KEYS[1], math.ceil(latest)) end
        return 1
        """);

    internal static readonly RespireScript RenewScript = RespireScript.Create("""
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)
        local member = 'P:' .. ARGV[1]
        if not redis.call('ZSCORE', KEYS[1], member) then
            if redis.call('ZCARD', KEYS[1]) <= 1 then redis.call('DEL', KEYS[1]) end
            return 0
        end
        local expiry = tonumber(ARGV[2])
        local score = expiry == 0 and math.huge or now + expiry
        redis.call('ZADD', KEYS[1], 'XX', score, member)

        local entries = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local latest = 0
        for i = 1, #entries, 2 do
            local current = tonumber(entries[i + 1])
            if current == math.huge then
                redis.call('PERSIST', KEYS[1])
                return 1
            end
            if current > latest then latest = current end
        end
        if latest > 0 then redis.call('PEXPIREAT', KEYS[1], math.ceil(latest)) end
        return 1
        """);

    internal static readonly RespireScript ReleaseScript = RespireScript.Create("""
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)
        local removed = redis.call('ZREM', KEYS[1], 'P:' .. ARGV[1])
        if redis.call('ZCARD', KEYS[1]) <= 1 then
            redis.call('DEL', KEYS[1])
            return removed
        end

        local entries = redis.call('ZRANGE', KEYS[1], 0, -1, 'WITHSCORES')
        local latest = 0
        for i = 1, #entries, 2 do
            local current = tonumber(entries[i + 1])
            if current == math.huge then
                redis.call('PERSIST', KEYS[1])
                return removed
            end
            if current > latest then latest = current end
        end
        if latest > 0 then redis.call('PEXPIREAT', KEYS[1], math.ceil(latest)) end
        return removed
        """);

    internal static readonly RespireScript VerifyScript = RespireScript.Create("""
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)
        local held = redis.call('ZSCORE', KEYS[1], 'P:' .. ARGV[1])
        if not held and redis.call('ZCARD', KEYS[1]) <= 1 then redis.call('DEL', KEYS[1]) end
        return held and 1 or 0
        """);
}

/// <summary>The result of an immediate distributed semaphore acquisition.</summary>
public readonly struct RespireSemaphorePermitAttempt : IAsyncDisposable
{
    private readonly RespireSemaphorePermit? _permit;

    internal RespireSemaphorePermitAttempt(RespireSemaphorePermit permit) => _permit = permit;

    /// <summary>Whether this attempt acquired a permit.</summary>
    public bool Acquired => _permit is not null;

    /// <summary>The acquired permit. Throws when this attempt did not acquire capacity.</summary>
    public RespireSemaphorePermit Permit => _permit ?? throw new RespireLockNotAcquiredException();

    /// <summary>Releases the acquired permit, or does nothing when acquisition failed.</summary>
    public ValueTask DisposeAsync() => _permit?.DisposeAsync() ?? default;
}

/// <summary>A uniquely owned semaphore permit with optional Redis expiry.</summary>
public sealed class RespireSemaphorePermit : IAsyncDisposable
{
    private readonly IRespireClient _client;
    private readonly RespireLockToken _owner;
    private readonly int _capacity;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _validUntil;
    private long _expiryTicks;
    private int _released;

    internal RespireSemaphorePermit(
        IRespireClient client, RespireKey key, RespireLockToken owner, int capacity,
        TimeSpan? expiry, TimeSpan? remaining, long completed)
    {
        _client = client;
        Key = key;
        _owner = owner;
        _capacity = capacity;
        _expiryTicks = expiry?.Ticks ?? 0;
        _validUntil = remaining.HasValue ? AddTimestampDuration(completed, remaining.Value) : long.MaxValue;
    }

    /// <summary>The semaphore key before the client's configured prefix.</summary>
    public RespireKey Key { get; }

    /// <summary>Capacity configured when this permit was acquired.</summary>
    public int Capacity => _capacity;

    /// <summary>Current server-side expiry duration, or null for an owner-released permit.</summary>
    public TimeSpan? Expiry
    {
        get
        {
            var ticks = Interlocked.Read(ref _expiryTicks);
            return ticks == 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>Conservative local estimate; null means permit has no expiry.</summary>
    public TimeSpan? RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _released) != 0) return TimeSpan.Zero;
            var validUntil = Interlocked.Read(ref _validUntil);
            if (validUntil == long.MaxValue) return null;
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), validUntil);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether this permit was released, lost, or locally expired.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Checks whether this owner still holds an active permit on Redis.</summary>
    public async ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
    {
        if (IsReleased) return false;
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(
                RespireSemaphore.VerifyScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
            var held = response.AsInteger() == 1;
            if (!held) Interlocked.Exchange(ref _released, 1);
            return held;
        }
        catch
        {
            Interlocked.Exchange(ref _released, 1);
            await ReleaseBestEffortAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Renews this permit or changes it between expiring and owner-released modes.</summary>
    /// <param name="expiry">Expiry of at least one millisecond, truncated to whole milliseconds, or null for owner-only release.</param>
    /// <param name="cancellationToken">Cancels waiting and the Redis command.</param>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan? expiry, CancellationToken cancellationToken = default)
    {
        var milliseconds = expiry?.Ticks / TimeSpan.TicksPerMillisecond ?? 0;
        if (expiry.HasValue && milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Permit expiry must be at least one millisecond.");
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReleased) return false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _client.Scripts.ExecuteAsync(
                    RespireSemaphore.RenewScript, [Key], [_owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
                var completed = Stopwatch.GetTimestamp();
                TimeSpan? requestedExpiry = milliseconds == 0 ? null : TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
                var remaining = requestedExpiry - Stopwatch.GetElapsedTime(started, completed);
                if (response.AsInteger() == 1 && (!remaining.HasValue || remaining.Value > TimeSpan.Zero))
                {
                    Interlocked.Exchange(ref _expiryTicks, requestedExpiry?.Ticks ?? 0);
                    Interlocked.Exchange(ref _validUntil, remaining.HasValue ? AddTimestampDuration(completed, remaining.Value) : long.MaxValue);
                    return true;
                }
            }
            catch
            {
                Interlocked.Exchange(ref _released, 1);
                await ReleaseBestEffortAsync().ConfigureAwait(false);
                throw;
            }

            Interlocked.Exchange(ref _released, 1);
            await ReleaseBestEffortAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit only; repeated calls return false.</summary>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return false;
            try
            {
                using var response = await _client.Scripts.ExecuteAsync(
                    RespireSemaphore.ReleaseScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
                return response.AsInteger() == 1;
            }
            catch
            {
                await ReleaseBestEffortAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit on a best-effort basis.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _ = await ReleaseAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async ValueTask ReleaseBestEffortAsync()
    {
        try
        {
            using var _ = await _client.Scripts.ExecuteAsync(
                RespireSemaphore.ReleaseScript, [Key], [_owner.Bytes], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private static long AddTimestampDuration(long timestamp, TimeSpan duration)
        => checked(timestamp + (long)((decimal)duration.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond));
}
