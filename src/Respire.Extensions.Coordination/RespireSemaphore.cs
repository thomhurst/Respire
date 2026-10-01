using System.Diagnostics;
using Respire.Internal;

namespace Respire.Extensions.Coordination;

/// <summary>Runs immediate, bounded permit acquisition against one Redis key.</summary>
/// <remarks>
/// <para>
/// Every contender for a key must use the same capacity. Acquisitions with a different capacity
/// fail with <see cref="RespireSemaphoreCapacityMismatchException"/> while any permit remains
/// active. Acquisitions do not queue and provide no fairness guarantee.
/// </para>
/// <para>
/// The scripts read Redis <c>TIME</c> before they write, so they need effects-based script
/// replication: Redis 5 or later, or a compatible server. Each script requests effects
/// replication first, so Redis 5 and 6 work even with <c>lua-replicate-commands</c> disabled.
/// </para>
/// <para>
/// With <see cref="RespireClient"/>, acquisitions under a command timeout or cancellation require
/// Redis ACL permission for <c>CLIENT ID</c> and <c>CLIENT KILL</c> to fence an uncertain acquire
/// before cleanup. Other <see cref="IRespireClient"/> implementations cannot fence, so cleanup of
/// an uncertain acquire can overtake the delayed acquire. Finite expiry then bounds how long that
/// permit stays held; a permit without expiry stays held until it is removed manually.
/// </para>
/// </remarks>
public sealed class RespireSemaphore
{
    // The acquire script replies with this error code, and TryAcquireCoreAsync maps exactly this
    // code to RespireSemaphoreCapacityMismatchException, so no other error text can match it.
    internal const string CapacityMismatchCode = "SEMCAPACITY";
    internal const string CapacityMismatchDetail = "semaphore capacity cannot change while permits are active";
    internal static readonly TimeSpan BestEffortCleanupTimeout = TimeSpan.FromSeconds(1);
    // Background cleanup is bounded even for owner-only permits. If Redis keeps rejecting the fence
    // or release, stop retrying after this window instead of retaining the client forever.
    internal static readonly TimeSpan CleanupRetryLimit = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan CleanupRetryInitialDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan CleanupRetryMaxDelay = TimeSpan.FromSeconds(5);
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
    /// <exception cref="RespireSemaphoreCapacityMismatchException">
    /// Active permits on this key were acquired with a different capacity.
    /// </exception>
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
        var concreteClient = _client as RespireClient;
        // Only a client that can fence gets one; the others still use a tracked execution for its
        // send timestamp.
        var trackedWire = concreteClient is null
            ? null
            : await concreteClient.GetCorrectionTrackingClientAsync(cancellationToken).ConfigureAwait(false);
        // Sampled after connection preflight: the permit cannot exist before the script is sent,
        // so only the acquisition itself counts against a short expiry.
        var started = Stopwatch.GetTimestamp();
        RespireClient.TrackedScriptExecution? trackedExecution = null;
        bool acquired;
        try
        {
            RespireValue[] args = [Capacity, owner.Bytes, milliseconds];
            if (concreteClient is null)
            {
                using var response = await _client.Scripts.ExecuteAsync(
                    AcquireScript, [Key], args, cancellationToken).ConfigureAwait(false);
                acquired = response.AsInteger() == 1;
            }
            else
            {
                trackedExecution = await concreteClient.StartTrackedScriptExecutionAsync(
                    AcquireScript, [Key], args, cancellationToken,
                    requireReliableCorrectionOrdering: trackedWire is not null,
                    captureSendTimestampOnly: trackedWire is null).ConfigureAwait(false);
                using var response = await trackedExecution.Response.ConfigureAwait(false);
                acquired = response.AsInteger() == 1;
            }
        }
        catch (RespireServerException error)
        {
            // An error reply is definite, so no delayed acquire can follow and no fence is needed.
            // The capacity check refuses before the script writes anything.
            if (error.Code == CapacityMismatchCode)
                throw new RespireSemaphoreCapacityMismatchException(Capacity, error);
            // Any other error may come from a later command, for example PERSIST rejected by an
            // ACL after ZADD already added this owner. Lua does not roll back earlier writes, so
            // release the owner token; a permit without expiry keeps retrying in the background.
            await ReleaseAcquiredAsync(owner, retryInBackground: milliseconds == 0).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CleanupUncertainAcquisitionAsync(trackedWire, trackedExecution, owner).ConfigureAwait(false);
            throw;
        }
        if (!acquired) return default;

        // The tracked send can wait for a routed connection, for in-flight capacity, or follow
        // redirects; the permit cannot exist before the final send, which StartedTimestamp records.
        if (trackedExecution is { StartedTimestamp: > 0 } sent) started = Math.Max(started, sent.StartedTimestamp);
        var completed = Stopwatch.GetTimestamp();
        var expiry = FromMilliseconds(milliseconds);
        // Conservative: Redis applies the expiry no earlier than the send, and the whole round
        // trip is deducted from it.
        var remaining = expiry - Stopwatch.GetElapsedTime(started, completed);
        if (remaining is { } left && left <= TimeSpan.Zero)
        {
            // The finite expiry is the fallback if this release attempt fails.
            await ReleaseAcquiredAsync(owner, retryInBackground: false).ConfigureAwait(false);
            return default;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // A permit without expiry has no server-side fallback, so its release keeps retrying.
            await ReleaseAcquiredAsync(owner, retryInBackground: milliseconds == 0).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new RespireSemaphorePermitAttempt(
            new RespireSemaphorePermit(_client, Key, owner, Capacity, expiry, remaining, completed));
    }

    // The acquire reply (success or error) has arrived, so no delayed acquire can follow this
    // release and no fence is needed. The caller waits at most BestEffortCleanupTimeout.
    private async ValueTask ReleaseAcquiredAsync(RespireLockToken owner, bool retryInBackground)
    {
        if (!retryInBackground)
        {
            await TryReleaseOnceAsync(_client, Key, owner).ConfigureAwait(false);
            return;
        }

        await WaitForCleanupAsync(RetryCleanupAsync(
            Stopwatch.GetTimestamp(), () => TryReleaseOnceAsync(_client, Key, owner),
            onAbandoned: ReportAbandoned(_client, "release"))).ConfigureAwait(false);
    }

    // A release that overtakes a delayed acquire would let that acquire recreate the permit, so
    // cleanup must follow the CLIENT KILL barrier. The caller waits at most
    // BestEffortCleanupTimeout; the rest of the cleanup continues in the background.
    private ValueTask CleanupUncertainAcquisitionAsync(
        RespireClient? wire, RespireClient.TrackedScriptExecution? execution, RespireLockToken owner)
        => WaitForCleanupAsync(FenceThenReleaseAsync(wire, execution, owner));

    private async Task FenceThenReleaseAsync(
        RespireClient? wire, RespireClient.TrackedScriptExecution? execution, RespireLockToken owner)
    {
        var started = Stopwatch.GetTimestamp();
        // If the barrier never succeeds within CleanupRetryLimit, ordering remains uncertain, so
        // no release is sent; finite expiry remains the fallback.
        if (!await RetryCleanupAsync(
                started, () => TryFenceAsync(wire, execution), onAbandoned: ReportAbandoned(_client, "fence"))
            .ConfigureAwait(false))
            return;
        // A fenced connection is retired, so the first release may wait for a replacement.
        await RetryCleanupAsync(
                started, () => TryReleaseOnceAsync(_client, Key, owner), onAbandoned: ReportAbandoned(_client, "release"))
            .ConfigureAwait(false);
    }

    private static async ValueTask WaitForCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.WaitAsync(BestEffortCleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    // Without a tracked connection identity there is nothing to fence, and the release proceeds
    // unordered; see the type remarks.
    private static async ValueTask<SemaphoreCleanupAttempt> TryFenceAsync(
        RespireClient? wire, RespireClient.TrackedScriptExecution? execution)
    {
        if (wire is null || execution is not { ConnectionIdentity.ServerClientId: > 0 })
            return SemaphoreCleanupAttempt.Succeeded;
        using var timeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        try
        {
            await wire.FenceCorrectionConnectionAsync(execution.ConnectionIdentity, timeout.Token).ConfigureAwait(false);
            return SemaphoreCleanupAttempt.Succeeded;
        }
        catch (ObjectDisposedException)
        {
            // The client is gone, so no release can be sent either.
            return SemaphoreCleanupAttempt.Abandoned;
        }
        catch (Exception)
        {
            // Includes NOPERM and generic ERR replies: only an acknowledged kill proves ordering.
            return SemaphoreCleanupAttempt.Failed;
        }
    }

    /// <summary>Sends one owner-checked release bounded by <see cref="BestEffortCleanupTimeout"/>.</summary>
    internal static async ValueTask<SemaphoreCleanupAttempt> TryReleaseOnceAsync(
        IRespireClient client, RespireKey key, RespireLockToken owner)
    {
        using var timeout = new CancellationTokenSource(BestEffortCleanupTimeout);
        try
        {
            using var _ = await client.Scripts.ExecuteAsync(
                ReleaseScript, [key], [owner.Bytes], timeout.Token).ConfigureAwait(false);
            return SemaphoreCleanupAttempt.Succeeded;
        }
        catch (ObjectDisposedException)
        {
            return SemaphoreCleanupAttempt.Abandoned;
        }
        catch (Exception)
        {
            // Every other failure, including server error replies, is retried within
            // CleanupRetryLimit by callers that retry at all.
            return SemaphoreCleanupAttempt.Failed;
        }
    }

    /// <summary>
    /// The one retry policy for background cleanup: capped, jittered exponential backoff until
    /// an attempt succeeds, the client is disposed, <paramref name="shouldContinue"/> returns false,
    /// or <see cref="CleanupRetryLimit"/> has elapsed since <paramref name="started"/>.
    /// </summary>
    /// <param name="started">Timestamp at which the retry window began.</param>
    /// <param name="attempt">One bounded fence or release attempt.</param>
    /// <param name="shouldContinue">Returns false once the cleanup is no longer needed.</param>
    /// <param name="onAbandoned">
    /// Called with "exhausted" or "client_disposed" when the cleanup stops while still needed.
    /// </param>
    /// <returns>True when an attempt succeeded.</returns>
    internal static async Task<bool> RetryCleanupAsync(
        long started, Func<ValueTask<SemaphoreCleanupAttempt>> attempt, Func<bool>? shouldContinue = null,
        Action<string>? onAbandoned = null)
    {
        var delay = CleanupRetryInitialDelay;
        while (shouldContinue is null || shouldContinue())
        {
            var outcome = await attempt().ConfigureAwait(false);
            if (outcome == SemaphoreCleanupAttempt.Succeeded) return true;
            if (outcome == SemaphoreCleanupAttempt.Abandoned)
            {
                onAbandoned?.Invoke("client_disposed");
                return false;
            }
            if (Stopwatch.GetElapsedTime(started) >= CleanupRetryLimit)
            {
                onAbandoned?.Invoke("exhausted");
                return false;
            }
            await Task.Delay(WithJitter(delay)).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, CleanupRetryMaxDelay.Ticks));
        }

        return false;
    }

    // Reports a cleanup that gave up as a counter and, when the client has a logger, a warning.
    internal static Action<string> ReportAbandoned(IRespireClient client, string stage)
        => reason => RespireTelemetry.RecordCoordinationCleanupAbandoned(
            "semaphore", stage, reason, (client as RespireClient)?.Core.Logger);

    // Spreads retries from clients that lost the same connection at the same moment.
    private static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    // Shared by every script: reads Redis server time, prunes expired permits, and defines the key
    // maintenance helpers. The capacity marker is the only member scored -inf and persistent
    // permits score +inf, so each lookup touches one end of the sorted set in O(log N).
    // Scores reach Redis as Lua numbers, which redis.call formats with 17 significant digits, so
    // epoch-millisecond scores stay exact.
    // Redis 5 and 6 replicate scripts by effects by default, but lua-replicate-commands no would
    // reject a write after TIME. Requesting effects replication first covers that setting; Redis 7
    // always replicates effects and keeps the call as a no-op, and the guard skips servers without it.
    private const string ScriptPrelude = """
        if redis.replicate_commands then redis.replicate_commands() end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], 1, now)
        -- Sets the key TTL to the latest permit expiry, or removes it when any permit has none.
        -- Only called while a permit remains; a marker-only key goes through cleanupIfEmpty.
        local function refreshTtl()
            local top = redis.call('ZREVRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            local latest = tonumber(top[2])
            if latest == math.huge then
                redis.call('PERSIST', KEYS[1])
            elseif latest and latest > 0 then
                redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', math.ceil(latest)))
            end
        end
        -- Deletes a key that holds only the capacity marker. Dropping the marker once permits
        -- drain is what lets a later acquisition choose a new capacity.
        local function cleanupIfEmpty()
            if redis.call('ZCARD', KEYS[1]) <= 1 then
                redis.call('DEL', KEYS[1])
                return true
            end
            return false
        end

        """;

    // Capacity is compared as a string: the marker stores 'C:' plus the canonical decimal integer
    // that RespireValue writes for an int, so every client formats the same capacity identically.
    internal static readonly RespireScript AcquireScript = RespireScript.Create(ScriptPrelude + $$"""
        local requestedCapacity = 'C:' .. ARGV[1]
        local storedCapacity = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', '-inf', 'LIMIT', 0, 1)[1]
        if storedCapacity and storedCapacity ~= requestedCapacity then
            if redis.call('ZCARD', KEYS[1]) > 1 then
                return redis.error_reply('{{CapacityMismatchCode}} {{CapacityMismatchDetail}}')
            end
            redis.call('ZREM', KEYS[1], storedCapacity)
            storedCapacity = nil
        end
        if not storedCapacity then redis.call('ZADD', KEYS[1], '-inf', requestedCapacity) end

        local active = redis.call('ZCARD', KEYS[1]) - 1
        if active >= tonumber(ARGV[1]) then return 0 end
        local expiry = tonumber(ARGV[3])
        local score = expiry == 0 and math.huge or now + expiry
        -- NX returns 0 only if this 128-bit owner token already exists, which random tokens make
        -- practically impossible; it reads as "full" and leaves no marker-only key behind.
        local added = redis.call('ZADD', KEYS[1], 'NX', score, 'P:' .. ARGV[2])
        if added == 0 then
            cleanupIfEmpty()
            return 0
        end
        refreshTtl()
        return 1
        """);

    internal static readonly RespireScript RenewScript = RespireScript.Create(ScriptPrelude + """
        local member = 'P:' .. ARGV[1]
        if not redis.call('ZSCORE', KEYS[1], member) then
            cleanupIfEmpty()
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
        if not cleanupIfEmpty() then refreshTtl() end
        return removed
        """);

    internal static readonly RespireScript VerifyScript = RespireScript.Create(ScriptPrelude + """
        local held = redis.call('ZSCORE', KEYS[1], 'P:' .. ARGV[1])
        if not held then cleanupIfEmpty() end
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

/// <summary>The outcome of one background cleanup attempt.</summary>
internal enum SemaphoreCleanupAttempt
{
    /// <summary>The attempt completed; stop retrying.</summary>
    Succeeded,
    /// <summary>The attempt failed and may succeed later.</summary>
    Failed,
    /// <summary>The client was disposed, so no later attempt can succeed.</summary>
    Abandoned,
}

/// <summary>
/// A semaphore acquisition used a capacity that differs from the capacity recorded on its key
/// while permits are still active.
/// </summary>
/// <remarks>
/// No permit was added. Every contender for a key must use the same capacity; the key accepts a
/// new capacity once every permit has been released or has expired.
/// </remarks>
public sealed class RespireSemaphoreCapacityMismatchException : RespireException
{
    internal RespireSemaphoreCapacityMismatchException(int requestedCapacity, RespireServerException innerException)
        : base(
            $"Semaphore capacity {requestedCapacity} does not match the capacity of the permits active on this key. " +
            "Capacity can change only after every permit is released or expires.",
            innerException)
        => RequestedCapacity = requestedCapacity;

    /// <summary>The capacity this acquisition requested.</summary>
    public int RequestedCapacity { get; }
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
    // Serializes renewal and release. It is never disposed: only WaitAsync is used, so no wait
    // handle is ever allocated and there is nothing to free.
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    // Expiry and local validity change together, so readers see both through one snapshot.
    private Lease _lease;
    // PermitState flags. Flags are only ever set or cleared through Set and Clear.
    private int _state;
    private int _disposeRetryRunning;
    private int _disposeRetryRestartRequested;

    internal RespireSemaphorePermit(
        IRespireClient client, RespireKey key, RespireLockToken owner, int capacity,
        TimeSpan? expiry, TimeSpan? remaining, long completed)
    {
        _client = client;
        Key = key;
        _owner = owner;
        _capacity = capacity;
        _lease = new Lease(
            expiry?.Ticks ?? 0,
            remaining.HasValue ? AddTimestampDuration(completed, remaining.Value) : long.MaxValue);
    }

    [Flags]
    private enum PermitState
    {
        None = 0,
        // A release command for this owner completed, or Redis reported the owner absent.
        Released = 1,
        // Disposal handed release to the background retry; the permit is no longer usable locally.
        DisposeReleaseScheduled = 2,
        // A renewal to owner-only release may have run on Redis without its reply being observed.
        NonExpiringOutcomeUncertain = 4,
        // A finite renewal may have run on Redis without its reply being observed.
        FiniteOutcomeUncertain = 8,
        OutcomeUncertain = NonExpiringOutcomeUncertain | FiniteOutcomeUncertain,
        // A renewal failed and surrendered the permit, so its local lease can no longer be relied
        // on, even when the surrender release has not been confirmed yet.
        RenewalFailed = 16,
    }

    // ExpiryTicks is 0 for an owner-released permit; ValidUntil is long.MaxValue in that case.
    private sealed class Lease(long expiryTicks, long validUntil)
    {
        public long ExpiryTicks { get; } = expiryTicks;
        public long ValidUntil { get; } = validUntil;
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
            var ticks = Volatile.Read(ref _lease).ExpiryTicks;
            return ticks == 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>Conservative local estimate; null means permit has no expiry.</summary>
    /// <remarks>
    /// The estimate is conservative because it counts from when the acquire or renewal command was
    /// sent, before Redis applied the expiry, and subtracts the whole round trip. It is zero after a
    /// release, after a failed or canceled renewal, or after disposal schedules its background
    /// release.
    /// </remarks>
    public TimeSpan? RemainingEstimate
    {
        get
        {
            if (Has(PermitState.Released | PermitState.RenewalFailed | PermitState.DisposeReleaseScheduled))
                return TimeSpan.Zero;
            var validUntil = Volatile.Read(ref _lease).ValidUntil;
            if (validUntil == long.MaxValue) return null;
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), validUntil);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether this permit can no longer be relied on.</summary>
    /// <remarks>
    /// True after a release, after Redis reported the permit gone, after a failed or canceled
    /// renewal, after disposal schedules background release, or once <see cref="RemainingEstimate"/>
    /// reaches zero. It does not mean
    /// <see cref="ReleaseAsync"/> was called or confirmed: a locally expired or surrendered permit can
    /// still be on Redis until it is released or its server-side expiry passes.
    /// </remarks>
    public bool IsReleased
        => Has(PermitState.Released | PermitState.RenewalFailed | PermitState.DisposeReleaseScheduled)
            || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Checks whether this owner still holds an active permit on Redis.</summary>
    /// <remarks>
    /// <para>
    /// Returns false once the permit is known to be released, lost, or locally expired. The check does
    /// not give up ownership: when it fails or is canceled, the exception propagates and the permit
    /// stays held, so callers can retry. It is a point-in-time answer and does not extend
    /// <see cref="RemainingEstimate"/>.
    /// </para>
    /// <para>
    /// The script prunes expired permits, so it writes: it runs on the primary and needs write ACL
    /// permission for the key.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
    {
        if (IsReleased) return false;
        // Deliberately outside the operation gate, so a stuck renewal cannot block a health check.
        // A concurrent renewal or release cannot disagree with the result: this only ever marks the
        // permit released after Redis reports the owner absent, and an absent owner cannot return
        // because renewal updates existing members only (ZADD XX).
        using var response = await _client.Scripts.ExecuteAsync(
            RespireSemaphore.VerifyScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
        var held = response.AsInteger() == 1;
        if (!held) Set(PermitState.Released);
        return held;
    }

    /// <summary>Renews this permit or changes it between expiring and owner-released modes.</summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="VerifyStillHeldAsync"/>, a failed or canceled renewal surrenders the permit:
    /// the renewal may still run on Redis and change the permit's lifetime, so this method attempts a
    /// bounded owner-token release before the exception propagates.
    /// </para>
    /// <para>
    /// Cancellation while waiting for another permit operation to finish does not start this renewal
    /// and leaves the permit unchanged. The surrender behavior applies after this call enters the gate.
    /// </para>
    /// <para>
    /// After any failed renewal, including a Redis error reply, <see cref="IsReleased"/> is true and
    /// every later call returns false without contacting Redis, even when that cleanup failed. A
    /// canceled or timed-out renewal could still execute and overwrite any newer expiry, and a script
    /// error does not roll back writes made before it, so the permit's lifetime on Redis is unknown.
    /// Call <see cref="ReleaseAsync"/> or dispose the permit, and acquire a new one instead.
    /// </para>
    /// <para>
    /// Returns false, after attempting release, when Redis confirms the renewal only after the
    /// requested expiry has already elapsed locally, or when disposal has started.
    /// </para>
    /// </remarks>
    /// <param name="expiry">Expiry of at least one millisecond, truncated to whole milliseconds, or null for owner-only release.</param>
    /// <param name="cancellationToken">Cancels waiting and the Redis command.</param>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan? expiry, CancellationToken cancellationToken = default)
    {
        var milliseconds = RespireSemaphore.ToMilliseconds(expiry, nameof(expiry));
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An earlier renewal may still execute and overwrite any newer score, so no later
            // renewal can be confirmed. Fail closed; disposal still retries the owner release.
            if (IsReleased || Has(PermitState.DisposeReleaseScheduled | PermitState.OutcomeUncertain)) return false;

            // Recorded before the send: until a reply arrives, this renewal may already have changed
            // the permit's lifetime on Redis, so disposal cleanup must not stop at the old expiry.
            // The early return above guarantees no other uncertainty flag is set at this point.
            var pending = milliseconds == 0
                ? PermitState.NonExpiringOutcomeUncertain
                : PermitState.FiniteOutcomeUncertain;
            Set(pending);
            var started = Stopwatch.GetTimestamp();
            RespireClient.TrackedScriptExecution? trackedExecution = null;
            bool renewed;
            long completed;
            try
            {
                // Renewal only updates an existing member, so a delayed renewal that executes after
                // a cleanup release cannot recreate the permit; no CLIENT KILL fence is needed.
                RespireResult response;
                if (_client is RespireClient concreteClient)
                {
                    trackedExecution = await concreteClient.StartTrackedScriptExecutionAsync(
                        RespireSemaphore.RenewScript, [Key], [_owner.Bytes, milliseconds], cancellationToken,
                        requireReliableCorrectionOrdering: false, captureSendTimestampOnly: true).ConfigureAwait(false);
                    response = await trackedExecution.Response.ConfigureAwait(false);
                }
                else
                {
                    response = await _client.Scripts.ExecuteAsync(
                        RespireSemaphore.RenewScript, [Key], [_owner.Bytes, milliseconds], cancellationToken).ConfigureAwait(false);
                }

                using (response)
                {
                    completed = Stopwatch.GetTimestamp();
                    renewed = response.AsInteger() == 1;
                }
            }
            catch (Exception)
            {
                // Keep the pending flag even for a Redis error reply: Lua does not roll back, so a
                // script that failed after its ZADD (for example an ACL rejecting PERSIST) may have
                // changed the permit's lifetime. Disposal cleanup must not stop at the old expiry.
                Set(PermitState.RenewalFailed);
                var failedRenewalCleanupOutcome = await TryReleaseAndMarkAsync().ConfigureAwait(false);
                if (failedRenewalCleanupOutcome == SemaphoreCleanupAttempt.Failed
                    && Has(PermitState.DisposeReleaseScheduled))
                    ScheduleDisposeReleaseRetry();
                throw;
            }

            if (!renewed)
            {
                // Redis confirmed this owner holds no permit; no release is needed to prove it.
                Set(PermitState.Released);
                Clear(pending);
                return false;
            }

            if (trackedExecution is { StartedTimestamp: > 0 } sent) started = Math.Max(started, sent.StartedTimestamp);
            var requestedExpiry = RespireSemaphore.FromMilliseconds(milliseconds);
            var remaining = requestedExpiry - Stopwatch.GetElapsedTime(started, completed);
            var stillValid = remaining is not { } left || left > TimeSpan.Zero;
            // Record the confirmed lifetime before clearing the pending flag, so disposal cleanup
            // never sees the old lifetime without the flag.
            Volatile.Write(ref _lease, new Lease(
                requestedExpiry?.Ticks ?? 0,
                remaining is not { } validRemaining ? long.MaxValue
                    : validRemaining > TimeSpan.Zero ? AddTimestampDuration(completed, validRemaining)
                    : completed));
            Clear(pending);
            if (stillValid && !Has(PermitState.DisposeReleaseScheduled)) return true;

            // The renewal was confirmed after its expiry elapsed locally, or disposal started meanwhile.
            var lateRenewalCleanupOutcome = await TryReleaseAndMarkAsync().ConfigureAwait(false);
            if (lateRenewalCleanupOutcome == SemaphoreCleanupAttempt.Failed
                && Has(PermitState.DisposeReleaseScheduled))
                ScheduleDisposeReleaseRetry();
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit only.</summary>
    /// <param name="cancellationToken">
    /// Cancels waiting and the Redis command. When the command fails or is canceled, one
    /// owner-token release bounded to one second still runs before the exception propagates.
    /// </param>
    /// <returns>
    /// True when this call removed the permit from Redis. False on repeated calls, when the permit
    /// already expired, or when a verification found it gone. After a failed renewal, this can still
    /// return true if this call removes the permit during cleanup.
    /// </returns>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReleaseUnderGateAsync(cleanupOnFailure: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this permit on a best-effort basis.</summary>
    /// <remarks>
    /// Disposal waits about one second at most and never throws. When release is not confirmed in
    /// that time, for example because a renewal is in flight or Redis is unreachable, owner-token
    /// release continues in the background with capped backoff for up to one minute. Failures are not
    /// thrown; a cleanup that gives up increments the <c>respire.coordination.cleanup.abandoned</c>
    /// counter and logs a warning through the client's logger. A permit without expiry that is still on Redis after that window stays
    /// there until it is removed manually, so prefer a finite expiry when clients can lose
    /// connectivity.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(DisposeReleaseTimeout);
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            // The background retry is the cleanup for a failed disposal release, so skip the extra
            // bounded attempt that ReleaseAsync makes and keep disposal within its one-second bound.
            _ = await ReleaseUnderGateAsync(cleanupOnFailure: false, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal does not throw. A busy gate, timeout or failure hands release to the background retry.
            ScheduleDisposeReleaseRetry();
        }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    private void ScheduleDisposeReleaseRetry()
    {
        Set(PermitState.DisposeReleaseScheduled);
        if (!NeedsDisposeCleanup()) return;
        if (Interlocked.CompareExchange(ref _disposeRetryRunning, 1, 0) == 0)
            _ = RunDisposeReleaseRetryAsync();
        else
            Interlocked.Exchange(ref _disposeRetryRestartRequested, 1);
    }

    private async Task RunDisposeReleaseRetryAsync()
    {
        try
        {
            await RespireSemaphore.RetryCleanupAsync(
                Stopwatch.GetTimestamp(), TryReleaseAndMarkAsync, NeedsDisposeCleanup,
                RespireSemaphore.ReportAbandoned(_client, "release")).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _disposeRetryRunning, 0);
            if (Interlocked.Exchange(ref _disposeRetryRestartRequested, 0) != 0 && NeedsDisposeCleanup())
                ScheduleDisposeReleaseRetry();
        }
    }

    // Disposal cleanup continues while Redis may still hold the permit: it has no expiry, a renewal
    // may have changed its lifetime without the reply being observed, or its local estimate has
    // not run out. The retry deliberately bypasses the operation gate, so a renewal or release
    // stuck behind an unanswered reply cannot block it. The owner-checked release is idempotent,
    // and a renewal that lands afterwards finds no member and cannot recreate the permit.
    private bool NeedsDisposeCleanup()
    {
        if (Has(PermitState.Released)) return false;
        var lease = Volatile.Read(ref _lease);
        return lease.ExpiryTicks == 0
            || Has(PermitState.OutcomeUncertain)
            || lease.ValidUntil == long.MaxValue
            || Stopwatch.GetTimestamp() < lease.ValidUntil;
    }

    private async ValueTask<bool> ReleaseUnderGateAsync(bool cleanupOnFailure, CancellationToken cancellationToken)
    {
        if (Has(PermitState.Released)) return false;
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(
                RespireSemaphore.ReleaseScript, [Key], [_owner.Bytes], cancellationToken).ConfigureAwait(false);
            var removed = response.AsInteger() == 1;
            Set(PermitState.Released);
            return removed;
        }
        catch when (cleanupOnFailure)
        {
            await TryReleaseAndMarkAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The single place that turns a completed release command into local released state.
    private async ValueTask<SemaphoreCleanupAttempt> TryReleaseAndMarkAsync()
    {
        var outcome = await RespireSemaphore.TryReleaseOnceAsync(_client, Key, _owner).ConfigureAwait(false);
        if (outcome == SemaphoreCleanupAttempt.Succeeded) Set(PermitState.Released);
        return outcome;
    }

    private bool Has(PermitState flags) => ((PermitState)Volatile.Read(ref _state) & flags) != 0;

    // Returns true when this call set at least one flag that was clear.
    private bool Set(PermitState flags)
    {
        var current = Volatile.Read(ref _state);
        while (true)
        {
            var updated = current | (int)flags;
            if (updated == current) return false;
            var observed = Interlocked.CompareExchange(ref _state, updated, current);
            if (observed == current) return true;
            current = observed;
        }
    }

    private void Clear(PermitState flags)
    {
        var current = Volatile.Read(ref _state);
        while (true)
        {
            var updated = current & ~(int)flags;
            if (updated == current) return;
            var observed = Interlocked.CompareExchange(ref _state, updated, current);
            if (observed == current) return;
            current = observed;
        }
    }

    // Saturates below long.MaxValue, which means "no expiry", so a centuries-long expiry cannot
    // overflow after Redis has already accepted the permit.
    internal static long AddTimestampDuration(long timestamp, TimeSpan duration)
    {
        var result = timestamp + (decimal)duration.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        return result >= long.MaxValue ? long.MaxValue - 1 : (long)result;
    }
}
