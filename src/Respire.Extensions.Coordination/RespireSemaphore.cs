using System.Diagnostics;

namespace Respire.Extensions.Coordination;

/// <summary>Runs immediate, bounded permit acquisition against one Redis key.</summary>
/// <remarks>
/// Every contender for a key must use the same capacity. Capacity changes fail while any
/// permit remains active. Acquisitions do not queue and provide no fairness guarantee.
/// </remarks>
public sealed class RespireSemaphore
{
    internal static readonly TimeSpan BestEffortCleanupTimeout = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DisposeReleaseRetryLimit = TimeSpan.FromMinutes(1);
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
        var milliseconds = ToMilliseconds(expiry, nameof(expiry));
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
        var expiry = FromMilliseconds(milliseconds);
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
            _ = await ReleaseBestEffortAsync(owner).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask<bool> ReleaseBestEffortAsync(RespireLockToken owner)
        => TryReleaseBestEffortAsync(_client, Key, owner);

    internal static async ValueTask<bool> TryReleaseBestEffortAsync(
        IRespireClient client, RespireKey key, RespireLockToken owner)
    {
        using var timeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        try
        {
            using var _ = await client.Scripts.ExecuteAsync(
                ReleaseScript, [key], [owner.Bytes], timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    // Shared by every script: reads Redis server time, prunes expired permits, and defines
    // the key-TTL refresh. The capacity marker is the only member scored -inf and persistent
    // permits score +inf, so each lookup touches one end of the sorted set in O(log N).
    private const string ScriptPrelude = """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)
        local function refreshTtl()
            local top = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            local latest = tonumber(top[2])
            if latest == math.huge then
                redis.call('PERSIST', KEYS[1])
            elseif latest and latest > 0 then
                redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', math.ceil(latest)))
            end
        end

        """;

    internal static readonly RespireScript AcquireScript = RespireScript.Create(ScriptPrelude + """
        local requestedCapacity = 'C:' .. ARGV[1]
        local storedCapacity = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '-inf', 'LIMIT', 0, 1)[1]
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
        refreshTtl()
        return 1
        """);

    internal static readonly RespireScript RenewScript = RespireScript.Create(ScriptPrelude + """
        local member = 'P:' .. ARGV[1]
        if not redis.call('ZSCORE', KEYS[1], member) then
            if redis.call('ZCARD', KEYS[1]) <= 1 then redis.call('DEL', KEYS[1]) end
            return 0
        end
        local expiry = tonumber(ARGV[2])
        local score = expiry == 0 and math.huge or now + expiry
        redis.call('ZADD', KEYS[1], 'XX', score, member)
        refreshTtl()
        return 1
        """);

    internal static readonly RespireScript ReleaseScript = RespireScript.Create(ScriptPrelude + """
        local removed = redis.call('ZREM', KEYS[1], 'P:' .. ARGV[1])
        if redis.call('ZCARD', KEYS[1]) <= 1 then
            redis.call('DEL', KEYS[1])
        else
            refreshTtl()
        end
        return removed
        """);

    internal static readonly RespireScript VerifyScript = RespireScript.Create(ScriptPrelude + """
        local held = redis.call('ZSCORE', KEYS[1], 'P:' .. ARGV[1])
        if not held and redis.call('ZCARD', KEYS[1]) <= 1 then redis.call('DEL', KEYS[1]) end
        return held and 1 or 0
        """);

    internal static long ToMilliseconds(TimeSpan? expiry, string parameterName)
    {
        var milliseconds = expiry?.Ticks / TimeSpan.TicksPerMillisecond ?? 0;
        if (expiry.HasValue && milliseconds <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Permit expiry must be at least one millisecond.");
        return milliseconds;
    }

    internal static TimeSpan? FromMilliseconds(long milliseconds)
        => milliseconds == 0 ? null : TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
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
    private static readonly TimeSpan DisposeReleaseTimeout = TimeSpan.FromSeconds(1);
    private readonly IRespireClient _client;
    private readonly RespireLockToken _owner;
    private readonly int _capacity;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _validUntil;
    private long _expiryTicks;
    private int _released;
    private int _disposeReleaseScheduled;

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
    /// <remarks>
    /// Returns false once the permit is known to be released, lost, or locally expired. The check does
    /// not give up ownership: when it fails or is canceled, the exception propagates and the permit
    /// stays held, so callers can retry.
    /// </remarks>
    public async ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
    {
        if (IsReleased) return false;
        using var response = await _client.Scripts.ExecuteAsync(
            RespireSemaphore.VerifyScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
        var held = response.AsInteger() == 1;
        if (!held) Interlocked.Exchange(ref _released, 1);
        return held;
    }

    /// <summary>Renews this permit or changes it between expiring and owner-released modes.</summary>
    /// <param name="expiry">Expiry of at least one millisecond, truncated to whole milliseconds, or null for owner-only release.</param>
    /// <param name="cancellationToken">Cancels waiting and the Redis command.</param>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan? expiry, CancellationToken cancellationToken = default)
    {
        var milliseconds = RespireSemaphore.ToMilliseconds(expiry, nameof(expiry));
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
                var requestedExpiry = RespireSemaphore.FromMilliseconds(milliseconds);
                var remaining = requestedExpiry - Stopwatch.GetElapsedTime(started, completed);
                if (response.AsInteger() == 1 && (!remaining.HasValue || remaining.Value > TimeSpan.Zero)
                    && Volatile.Read(ref _disposeReleaseScheduled) == 0)
                {
                    Interlocked.Exchange(ref _expiryTicks, requestedExpiry?.Ticks ?? 0);
                    Interlocked.Exchange(ref _validUntil, remaining.HasValue ? AddTimestampDuration(completed, remaining.Value) : long.MaxValue);
                    return Volatile.Read(ref _disposeReleaseScheduled) == 0;
                }
            }
            catch
            {
                if (await ReleaseBestEffortAsync().ConfigureAwait(false))
                    Interlocked.Exchange(ref _released, 1);
                throw;
            }

            if (await ReleaseBestEffortAsync().ConfigureAwait(false))
                Interlocked.Exchange(ref _released, 1);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit only.</summary>
    /// <returns>
    /// True when this call removed the permit from Redis. False on repeated calls, when the permit
    /// already expired, or when a failed renewal or a verification that found it gone marked it lost.
    /// </returns>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReleaseUnderGateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit on a best-effort basis.</summary>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(DisposeReleaseTimeout);
        var entered = false;
        try
        {
            try
            {
                await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                ScheduleDisposeReleaseRetry();
                return;
            }
            _ = await ReleaseUnderGateAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { ScheduleDisposeReleaseRetry(); }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    private void ScheduleDisposeReleaseRetry()
    {
        if (Interlocked.Exchange(ref _disposeReleaseScheduled, 1) == 0)
            _ = RetryDisposeReleaseAsync();
    }

    private async Task RetryDisposeReleaseAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromMilliseconds(100);
        while (Volatile.Read(ref _released) == 0 && Stopwatch.GetElapsedTime(started) < RespireSemaphore.DisposeReleaseRetryLimit
            && RemainingEstimate != TimeSpan.Zero)
        {
            var released = await TryReleaseAfterDisposeAsync().ConfigureAwait(false);
            if (released is null or true) return;

            if (Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(delay).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }
    }

    private async ValueTask<bool?> TryReleaseAfterDisposeAsync()
    {
        using var timeout = new CancellationTokenSource(RespireSemaphore.BestEffortCleanupTimeout);
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(
                RespireSemaphore.ReleaseScript, [Key], [_owner.Bytes], timeout.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _released, 1);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async ValueTask<bool> ReleaseUnderGateAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _released) != 0) return false;
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(
                RespireSemaphore.ReleaseScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
            var removed = response.AsInteger() == 1;
            Interlocked.Exchange(ref _released, 1);
            return removed;
        }
        catch
        {
            if (await ReleaseBestEffortAsync().ConfigureAwait(false))
                Interlocked.Exchange(ref _released, 1);
            throw;
        }
    }

    private ValueTask<bool> ReleaseBestEffortAsync()
        => RespireSemaphore.TryReleaseBestEffortAsync(_client, Key, _owner);

    // Saturates below long.MaxValue, which means "no expiry", so a centuries-long expiry cannot
    // overflow after Redis has already accepted the permit.
    private static long AddTimestampDuration(long timestamp, TimeSpan duration)
    {
        var result = timestamp + (decimal)duration.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        return result >= long.MaxValue ? long.MaxValue - 1 : (long)result;
    }
}
