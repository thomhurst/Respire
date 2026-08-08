using System.Buffers;
using System.Diagnostics;
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

    // Staleness a set may accumulate before its TTL gets corrected. One second, because that is
    // the expiry granularity the Microsoft implementation already truncates to (EXPIRE seconds).
    private static readonly TimeSpan SendDelayTolerance = TimeSpan.FromSeconds(1);

    // A correction pass legitimately queues behind the very stall it chases, so its wait must
    // outlast any stall worth chasing — but not a reply that is never coming (a wedged server,
    // a blackholed connection). Past this bound a tracked original connection is fenced and the
    // correction retried; without a tracked original, the queued shrink-only pass is left to
    // land in the background. Mutable so tests can shrink the bound.
    internal TimeSpan CorrectionWaitBound = TimeSpan.FromSeconds(10);

    // A non-RespireClient wrapper exposes only token-less key deletion. Start each removal with
    // brief temporary authority, then grow later attempts from observed backend latency. Fast
    // cancellation therefore stays prompt while slow backends can complete within the separate
    // operation timeout. Mutable so tests can shrink the safety window.
    internal TimeSpan WrappedRemovalMinimumLeaseTtl = TimeSpan.FromMilliseconds(250);
    internal TimeSpan WrappedRemovalLeaseExpiryMargin = TimeSpan.FromMilliseconds(50);
    internal TimeSpan WrappedRemovalTimeout = TimeSpan.FromSeconds(30);

    // ARGV: [1] absolute expiration (UTC ticks, -1 none), [2] sliding expiration (ticks, -1 none),
    // [3] relative expiry (ms, -1 none), [4] payload. PERSIST clears a leftover TTL when an
    // existing entry is overwritten without one (HSET alone keeps the old TTL). Expiry stays on
    // the app's clock — the server clock is never consulted (see CapDelayedTtlAsync for why, and
    // for how a delayed send is corrected).
    internal static readonly RespireScript SetScript = RespireScript.Create("""
        redis.call('HSET', KEYS[1], 'absexp', ARGV[1], 'sldexp', ARGV[2], 'data', ARGV[4])
        if ARGV[3] ~= '-1' then
          redis.call('PEXPIRE', KEYS[1], ARGV[3])
        else
          redis.call('PERSIST', KEYS[1])
        end
        return 1
        """);

    // ARGV: [1] '1' to return the payload ('0' for refresh-only), [2] current UTC ticks (the
    // app's clock, sampled just before the send — a delayed send re-arms from a "now" stale by
    // the delay; RunGetScriptAsync measures the delay on that same clock once the reply arrives
    // and corrects the over-extension with CapRefreshedTtlScript, so the residual overshoot is
    // bounded by SendDelayTolerance).
    // Returns nil for a missing key, otherwise {capped, payload?}: capped is 1 only when the
    // re-arm ran with an absolute deadline in play — the one shape a delayed send can
    // over-extend — so the caller knows whether a correction round trip can matter at all
    // (sliding-only re-arms to the same window regardless of staleness; absolute-only never
    // re-arms).
    // For sliding entries the TTL is re-armed to min(sliding, time left until absolute
    // expiration), atomically with the read. Ticks (100ns) to Redis milliseconds is a divide
    // by 10000.
    // The TTL Redis already holds is the authority on expiry — absexp caps re-arming and never
    // deletes. The metadata can carry a writer's clock-offset quirk (the Microsoft
    // implementation stores DateTimeOffset.Ticks with the caller's offset baked in, then reads
    // them back as UTC), skewing the remainder by the writer's offset in either direction:
    // - Negative offset: a live key's absexp reads as already past. That contradiction is
    //   detectable, and since the true deadline is unknowable no re-arm can be proven safe — a
    //   non-positive remainder skips the re-arm and lets the write-time TTL run out. The entry
    //   loses sliding refresh but is never served past its deadline nor deleted early (the
    //   Microsoft reader deletes it on sight, via KeyExpire with a negative TimeSpan).
    // - Positive offset: the remainder reads inflated, so the sliding window re-arms in full
    //   and the entry can outlive its real deadline — exactly as the Microsoft reader treats
    //   the same entry. This skew is undetectable (an inflated absexp is indistinguishable
    //   from a genuinely distant one), so the only cap that could contain it is refusing to
    //   ever extend the TTL, which would disable sliding refresh for every honest entry.
    internal static readonly RespireScript GetAndRefreshScript = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'absexp', 'sldexp', 'data')
        if entry[1] == false and entry[3] == false then
          return nil
        end
        local capped = 0
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
            if absexp ~= -1 then
              capped = 1
            end
          end
        end
        if ARGV[1] == '1' then
          return {capped, entry[3]}
        end
        return {capped}
        """);

    // Shrink-only TTL correction for a set whose send was delayed (see CapDelayedTtlAsync).
    // ARGV: [1] absexp and [2] sldexp exactly as the delayed set stored them — a mismatch on
    // either means a concurrent overwrite won the race and the correction no longer applies
    // (absexp alone is not enough: another writer can share the explicit absolute deadline yet
    // carry a different sliding window, and must not be shrunk to ours); [3] the re-derived
    // remaining ms (<= 0: the deadline has already passed, remove the entry). The TTL is only
    // ever shortened (PTTL -1, a lost TTL, counts as infinite), so racing reads or writers can
    // never be extended by it.
    internal static readonly RespireScript CapTtlScript = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'absexp', 'sldexp')
        if entry[1] ~= ARGV[1] or entry[2] ~= ARGV[2] then
          return 0
        end
        local remaining = tonumber(ARGV[3])
        if remaining <= 0 then
          redis.call('UNLINK', KEYS[1])
          return 1
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl == -1 or ttl > remaining then
          redis.call('PEXPIRE', KEYS[1], remaining)
        end
        return 1
        """);

    // Shrink-only TTL correction for a sliding read whose send was delayed (see
    // RunGetScriptAsync). ARGV: [1] a fresh app-clock "now" (UTC ticks). Unlike CapTtlScript
    // this needs no ownership check: it re-derives min(sliding, remaining) from whatever
    // metadata the key currently holds — the same formula any honest read applies — and the
    // PTTL guard keeps it strictly shrink-only, so a concurrently overwritten entry is at
    // worst a no-op. A non-positive remainder skips rather than unlinks: absexp here is wire
    // metadata that can carry a writer's clock offset (see GetAndRefreshScript), so deletion
    // cannot be proven safe; the stale re-arm it leaves behind is bounded by the send delay.
    internal static readonly RespireScript CapRefreshedTtlScript = RespireScript.Create("""
        local entry = redis.call('HMGET', KEYS[1], 'absexp', 'sldexp')
        local absexp = tonumber(entry[1]) or -1
        local sldexp = tonumber(entry[2]) or -1
        if absexp == -1 or sldexp == -1 then
          return 0
        end
        local ttl = math.floor(sldexp / 10000)
        local remaining = math.floor((absexp - tonumber(ARGV[1])) / 10000)
        if remaining < ttl then
          ttl = remaining
        end
        if ttl > 0 then
          local pttl = redis.call('PTTL', KEYS[1])
          if pttl == -1 or pttl > ttl then
            redis.call('PEXPIRE', KEYS[1], ttl)
          end
        end
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly RespireClient? _ownedClient;

    // The real client (a key-prefixed view is still a RespireClient), which exposes wire-level
    // guarantees the interface cannot: exact Redis client-ID tracking, reply waits immune to
    // CommandTimeout, and all-connection sends whose per-connection FIFO orders a correction
    // after a still-buffered original. Null only for a caller-supplied mock or decorator, which
    // degrades to the plain command path and keeps only its weaker ordering.
    private readonly RespireClient? _wireClient;

    /// <summary>Wraps an existing client; the caller keeps ownership of it.</summary>
    public RespireDistributedCache(IRespireClient client, RespireCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = ApplyInstanceName(client, options);
        _wireClient = _client as RespireClient;
    }

    /// <summary>Owns <paramref name="ownedClient"/> — disposes it with the cache.</summary>
    internal RespireDistributedCache(RespireClient ownedClient, RespireCacheOptions? options)
    {
        _ownedClient = ownedClient;
        _client = ApplyInstanceName(ownedClient, options);
        _wireClient = _client as RespireClient;
    }

    private static IRespireClient ApplyInstanceName(IRespireClient client, RespireCacheOptions? options)
        => string.IsNullOrEmpty(options?.InstanceName) ? client : client.WithKeyPrefix(options.InstanceName);

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        using var result = await RunGetScriptAsync(key, returnData: true, token).ConfigureAwait(false);
        if (result.IsNull)
        {
            return null;
        }

        var payload = result[1];
        return payload.IsNull ? null : payload.AsBytes();
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

        var payload = result[1];
        if (payload.IsNull)
        {
            return false;
        }

        destination.Write(payload.AsSpan());
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

        var trackedWire = await GetTrackedWireAsync(token).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var absoluteExpiration = GetAbsoluteExpiration(now, options);
        var originalServerClientId = 0L;
        try
        {
            RespireResult result;
            var args = new RespireValue[]
            {
                absoluteExpiration?.UtcTicks ?? NotPresent,
                options.SlidingExpiration?.Ticks ?? NotPresent,
                GetExpirationMilliseconds(now, absoluteExpiration, options),
                value,
            };
            if (trackedWire is not null)
            {
                var execution = trackedWire.StartTrackedScriptExecution(SetScript, [key], args, token);
                originalServerClientId = execution.ServerClientId;
                result = await execution.Response.ConfigureAwait(false);
            }
            else
            {
                result = await _client.Scripts.ExecuteAsync(SetScript, [key], args, token).ConfigureAwait(false);
            }

            result.Dispose();
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
        {
            // An abandoned wait or lost connection does not prove the send was rejected — a
            // queued set can still reach Redis, and if that send was delayed it stores a TTL
            // computed before the delay. With no reply to measure the delay against, correct
            // unconditionally: a correction that beats a still-queued set no-ops on the
            // ownership check. When the original Redis client ID was captured, it is fenced
            // before correction retries so latent bytes cannot overtake the final pass.
            // Untracked and mock/decorator corrections remain best-effort.
            if (absoluteExpiration is { } cancelledDeadline)
            {
                try
                {
                    await CapDelayedTtlAsync(
                        key, cancelledDeadline, options, originalServerClientId).ConfigureAwait(false);
                }
                catch (RespireException correctionFailure) when (
                    originalServerClientId == 0 && correctionFailure is not RespireServerException)
                {
                }
            }
            else if (trackedWire is not null && originalServerClientId > 0)
            {
                // No TTL needs correction, but the write itself still must not land after the
                // caller observes failure and writes a replacement.
                await trackedWire.FenceCorrectionConnectionAsync(originalServerClientId).ConfigureAwait(false);
            }

            throw;
        }

        if (absoluteExpiration is { } absolute && DateTimeOffset.UtcNow - now >= SendDelayTolerance)
        {
            await CapDelayedTtlAsync(key, absolute, options).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The TTL a set arms is computed on the app's clock before the send, but PEXPIRE starts
    /// counting only when the script executes — a delayed send (lazy first connect, reconnect)
    /// would let the entry outlive its absolute deadline by the delay. The staleness is measured
    /// here on the same clock once the reply arrives, and a shrink-only correction re-arms the
    /// true remainder (or removes an entry whose deadline already passed in transit). Deadlines
    /// are deliberately never checked against the Redis server clock: that would shift every TTL
    /// by the hosts' skew and reject valid entries outright once the skew exceeds their lifetime.
    /// Runs without the caller's token — once the entry is stored, enforcing its deadline must
    /// not be skippable by cancellation.
    /// </summary>
    private ValueTask CapDelayedTtlAsync(
        string key,
        DateTimeOffset absolute,
        DistributedCacheEntryOptions options,
        long originalServerClientId = 0)
        => RunCorrectionAsync(
            CapTtlScript,
            key,
            () =>
            [
                absolute.UtcTicks,
                options.SlidingExpiration?.Ticks ?? NotPresent,
                GetExpirationMilliseconds(DateTimeOffset.UtcNow, absolute, options),
            ],
            originalServerClientId);

    /// <summary>
    /// Runs a shrink-only correction script. Corrections chase a command whose reply was never
    /// seen (cancelled or timed-out wait), and round-robin can put a follow-up on a different
    /// connection where it would execute before the still-buffered original and no-op — so the
    /// correction is sent on every connection: the copy sharing the original's connection is
    /// FIFO-ordered after it, and the scripts are idempotent and guarded, so the other copies
    /// are harmless wherever they land. If that connection dies, CLIENT KILL by its captured
    /// Redis client ID establishes a server-side barrier before the correction retries: the
    /// original either ran before the barrier or was discarded with the server-side client.
    /// A correction usually queues behind the same stall that
    /// delayed the command it chases, which would leave its own remainder stale by its own send
    /// delay — so the args are re-derived from a fresh clock and re-sent until one pass
    /// round-trips inside the tolerance; shrink-only makes every extra pass safe, and each pass
    /// costs one server round trip, so the loop paces itself and converges as soon as the stall
    /// clears. A pass earns a retry only by at least halving the previous round trip: the
    /// remainder a pass arms goes stale by exactly its own round trip, so under latency that
    /// never clears (a slow backend, one persistently slow connection in the broadcast) that
    /// round trip is the irreducible floor — a pass that no longer improves proves further
    /// passes cannot either, and the loop stops with a residual bounded by that floor instead
    /// of retrying forever. The halving requirement also caps the pass count at a logarithm of
    /// the initial stall. Each pass's wait is itself bounded by <see cref="CorrectionWaitBound"/>.
    /// When the original command's Redis client ID is known, an overdue pass kills and retires
    /// that exact connection before retrying on its replacement. Otherwise the queued
    /// shrink-only pass — safe whenever it lands — finishes in the background.
    /// A correction chasing an abandoned wait requires the wire client for ordering; after an
    /// observed reply, or with a mocked client, one ordinary send is sufficient.
    /// </summary>
    private async ValueTask RunCorrectionAsync(
        RespireScript script,
        string key,
        Func<RespireValue[]> args,
        long originalServerClientId = 0)
    {
        var previous = TimeSpan.MaxValue;
        var requiresOrdering = originalServerClientId > 0;
        while (true)
        {
            var sent = DateTimeOffset.UtcNow;
            var pass = RunCorrectionPassAsync(script, key, args(), requiresOrdering);
            try
            {
                await pass.WaitAsync(CorrectionWaitBound).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Reply draining may be abandoned, but the exact original CLIENT KILL barrier
                // may not: without its server acknowledgement, flushed bytes could still
                // execute after the original failure is surfaced.
                if (_wireClient is { } wire && originalServerClientId > 0)
                {
                    await wire.FenceCorrectionConnectionAsync(originalServerClientId).ConfigureAwait(false);
                    originalServerClientId = 0;
                    _ = ObservePassAsync(pass);
                    previous = TimeSpan.MaxValue;
                    continue;
                }

                // The pass stays queued and is shrink-only, idempotent, and ownership-guarded,
                // so it corrects whenever it lands; retrying against the same wedge would only
                // queue more copies behind it.
                _ = ObservePassAsync(pass);
                return;
            }

            // A completed broadcast proves its copy on the original connection ran after the
            // abandoned command (or that connection was fenced). Later freshness-only passes
            // need one ordinary send and must not kill a connection unnecessarily.
            if (requiresOrdering)
            {
                requiresOrdering = false;
                originalServerClientId = 0;
            }

            var roundTrip = DateTimeOffset.UtcNow - sent;
            if (roundTrip < SendDelayTolerance || roundTrip > previous / 2)
            {
                return;
            }

            previous = roundTrip;
        }
    }

    private async Task RunCorrectionPassAsync(
        RespireScript script,
        string key,
        RespireValue[] args,
        bool requiresOrdering)
    {
        if (requiresOrdering && _wireClient is { } wire)
        {
            await wire.ExecuteOnAllConnectionsAsync(script, [key], args).ConfigureAwait(false);
        }
        else
        {
            var result = await _client.Scripts.ExecuteAsync(script, [key], args, CancellationToken.None).ConfigureAwait(false);
            result.Dispose();
        }
    }

    private static async Task ObservePassAsync(Task pass)
    {
        try
        {
            await pass.ConfigureAwait(false);
        }
        catch
        {
            // Detached correction is best-effort; observe any late fault so it cannot become an
            // unhandled task exception.
        }
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
        // Deletion carries no identity to fence on in the shared hash layout, so an UNLINK left
        // executable after an abandoned wait could later delete a replacement the caller wrote
        // after observing the failure. The guarded send solves all three hazards: the wait stays
        // bounded (caller token and CommandTimeout are honored, or a finite operation timeout
        // supplies the default bound, and the failure path is bounded by one brief lease);
        // abandoning it discards the dedicated connection so a still-queued command dies with
        // the socket; and
        // the delete itself is leased — it only runs while a TTL'd lease key placed beforehand
        // is still alive, and no failure surfaces until that lease is revoked or has certainly
        // expired, so a copy already flushed to the server cannot delete anything written after
        // the caller saw the failure. A mocked/decorated client gets the same lease invariant
        // through public client APIs; its token-less lease revocation is bounded by expiry.
        if (_wireClient is { } wire)
        {
            await wire.UnlinkGuardedAsync(key, token).ConfigureAwait(false);
        }
        else
        {
            await UnlinkWrappedGuardedAsync(key, token).ConfigureAwait(false);
        }
    }

    private async Task UnlinkWrappedGuardedAsync(string key, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(WrappedRemovalTimeout);
        try
        {
            await UnlinkWrappedLeasedAsync(key, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException("UNLINK", WrappedRemovalTimeout);
        }
    }

    private async Task UnlinkWrappedLeasedAsync(string key, CancellationToken cancellationToken)
    {
        var leaseTtl = WrappedRemovalMinimumLeaseTtl;
        while (true)
        {
            RespireKey lease = "respire-rm-lease:" + Guid.NewGuid().ToString("N");

            // If this wait is abandoned, only an unused expiring lease can land late; the
            // deletion script is not sent until placement is confirmed.
            var placementStart = Stopwatch.GetTimestamp();
            await _client.SetAsync(
                    lease,
                    1,
                    leaseTtl,
                    cancellationToken: cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var leaseStart = Stopwatch.GetTimestamp();
            var placementElapsed = Stopwatch.GetElapsedTime(placementStart);
            if (placementElapsed >= leaseTtl / 2)
            {
                // The reply consumed a material part of this lease. No delete has been sent,
                // so retry with enough authority for the latency already observed.
                leaseTtl = GrowWrappedRemovalLease(leaseTtl, placementElapsed);
                continue;
            }

            Task<RespireResult>? removal = null;
            try
            {
                removal = _client.Scripts.ExecuteAsync(
                        RespireClient.LeasedUnlinkScript,
                        [key, lease],
                        cancellationToken: cancellationToken)
                    .AsTask();
                using var result = await removal.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (result.AsInteger() == 1)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                leaseTtl = GrowWrappedRemovalLease(
                    leaseTtl, Stopwatch.GetElapsedTime(leaseStart));
            }
            catch (Exception ex) when (ex is not RespireServerException)
            {
                if (removal is not null)
                {
                    _ = ObserveWrappedRemovalAsync(removal);
                }

                await MakeWrappedRemovalHarmlessAsync(lease, leaseStart, leaseTtl).ConfigureAwait(false);
                throw;
            }
        }
    }

    private TimeSpan GrowWrappedRemovalLease(TimeSpan current, TimeSpan observedLatency)
    {
        var maximumTicks = WrappedRemovalTimeout.Ticks;
        var doubledCurrent = current.Ticks >= maximumTicks / 2
            ? maximumTicks
            : current.Ticks * 2;
        var quadrupledLatency = observedLatency.Ticks >= maximumTicks / 4
            ? maximumTicks
            : observedLatency.Ticks * 4;
        return TimeSpan.FromTicks(Math.Max(doubledCurrent, quadrupledLatency));
    }

    private async Task MakeWrappedRemovalHarmlessAsync(
        RespireKey lease,
        long leaseStart,
        TimeSpan leaseTtl)
    {
        var remaining = leaseTtl - Stopwatch.GetElapsedTime(leaseStart);
        if (remaining > TimeSpan.Zero)
        {
            Task<long>? revoke = null;
            try
            {
                revoke = _client.Keys.UnlinkAsync(lease).AsTask();
                await revoke.WaitAsync(remaining).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                if (revoke is not null)
                {
                    _ = ObserveWrappedRevokeAsync(revoke);
                }
            }
            catch (Exception)
            {
                // Expiry below remains the independent safety boundary.
            }
        }

        var wait = leaseTtl + WrappedRemovalLeaseExpiryMargin -
            Stopwatch.GetElapsedTime(leaseStart);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
    }

    private static async Task ObserveWrappedRemovalAsync(Task<RespireResult> removal)
    {
        try
        {
            (await removal.ConfigureAwait(false)).Dispose();
        }
        catch
        {
            // Detached late execution is best-effort; observation and result disposal are the
            // only remaining responsibilities after lease expiry made deletion harmless.
        }
    }

    private static async Task ObserveWrappedRevokeAsync(Task<long> revoke)
    {
        try
        {
            await revoke.ConfigureAwait(false);
        }
        catch
        {
            // Lease expiry is the safety boundary; this only observes the abandoned revocation.
        }
    }

    private async ValueTask<RespireResult> RunGetScriptAsync(string key, bool returnData, CancellationToken token)
    {
        var trackedWire = await GetTrackedWireAsync(token).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        RespireResult result;
        var originalServerClientId = 0L;
        try
        {
            var args = new RespireValue[] { returnData ? "1" : "0", now.UtcTicks };
            if (trackedWire is not null)
            {
                var execution = trackedWire.StartTrackedScriptExecution(GetAndRefreshScript, [key], args, token);
                originalServerClientId = execution.ServerClientId;
                result = await execution.Response.ConfigureAwait(false);
            }
            else
            {
                result = await _client.Scripts.ExecuteAsync(
                    GetAndRefreshScript, [key], args, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
        {
            // Same hazard as the set path: an abandoned or failed reply wait can leave the
            // script's execution uncertain, and a delayed send would re-arm the sliding TTL
            // from a stale "now". The real client completes its exact-connection safety
            // barrier when an ID was captured; untracked and mock/decorator corrections remain
            // best-effort as in SetCoreAsync.
            try
            {
                await CapRefreshedTtlAsync(key, originalServerClientId).ConfigureAwait(false);
            }
            catch (RespireException correctionFailure) when (
                originalServerClientId == 0 && correctionFailure is not RespireServerException)
            {
            }

            throw;
        }

        // The re-arm the script just performed used a "now" stale by however long the send
        // stalled; past the tolerance, shrink the TTL back to a fresh-clock remainder — but
        // only when the script says the re-arm had an absolute deadline in play, the one
        // shape staleness can over-extend. Runs without the caller's token — the entry was
        // already extended, so undoing the over-extension must not be skippable by
        // cancellation.
        if (DateTimeOffset.UtcNow - now >= SendDelayTolerance && !result.IsNull && result[0].AsInteger() == 1)
        {
            try
            {
                await CapRefreshedTtlAsync(key).ConfigureAwait(false);
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        return result;
    }

    private async ValueTask<RespireClient?> GetTrackedWireAsync(CancellationToken cancellationToken)
    {
        if (_wireClient is not { } wire)
        {
            return null;
        }

        if (wire.RequiresReliableCorrectionOrdering(cancellationToken))
        {
            await wire.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return wire;
        }

        return await wire.TryEnsureReliableCorrectionOrderingAsync().ConfigureAwait(false)
            ? wire
            : null;
    }

    private ValueTask CapRefreshedTtlAsync(string key, long originalServerClientId = 0)
        => RunCorrectionAsync(
            CapRefreshedTtlScript,
            key,
            () => [DateTimeOffset.UtcNow.UtcTicks],
            originalServerClientId);

    private static DateTimeOffset? GetAbsoluteExpiration(DateTimeOffset now, DistributedCacheEntryOptions options)
    {
        // AbsoluteExpirationRelativeToNow takes precedence, so a stale AbsoluteExpiration left
        // behind on a reused options instance is dead data — only the effective deadline is
        // validated.
        if (options.AbsoluteExpirationRelativeToNow is { } relative)
        {
            return now + relative;
        }

        if (options.AbsoluteExpiration is { } absolute && absolute <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistributedCacheEntryOptions.AbsoluteExpiration), absolute,
                "The absolute expiration value must be in the future.");
        }

        return options.AbsoluteExpiration;
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
