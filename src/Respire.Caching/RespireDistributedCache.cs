using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Distributed;
using Respire.Compression;
using Respire.Internal;

namespace Respire.Caching;

/// <summary>
/// An <see cref="IDistributedCache"/> (and <see cref="IBufferDistributedCache"/>, so HybridCache
/// takes its buffer-oriented path) backed by Redis through Respire. Entries use the same hash
/// layout as Microsoft.Extensions.Caching.StackExchangeRedis — fields <c>absexp</c>,
/// <c>sldexp</c> and <c>data</c> — so the two implementations can read each other's entries
/// when no value codec is configured.
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
    // brief temporary authority, then grow later attempts from observed backend latency up to a
    // one-second safety cap. Cancellation therefore stays bounded even if lease revocation is
    // blackholed, while the separate operation timeout permits repeated attempts. Mutable so
    // tests can shrink the safety window.
    internal TimeSpan WrappedRemovalMinimumLeaseTtl = TimeSpan.FromMilliseconds(250);
    internal TimeSpan WrappedRemovalMaximumLeaseTtl = TimeSpan.FromSeconds(1);
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
    private readonly IRespireValueCodec? _valueCodec;
    private readonly RespireClient? _ownedClient;

    // The real client (a key-prefixed view is still a RespireClient), which exposes wire-level
    // guarantees the interface cannot: exact Redis client-ID tracking, reply waits immune to
    // CommandTimeout, and all-connection sends whose per-connection FIFO orders a correction
    // after a still-buffered original. Null only for a caller-supplied mock or decorator, which
    // degrades to the plain command path and keeps only its weaker ordering.
    private readonly RespireClient? _wireClient;

    // HybridCache observes physical L2 keys on a separate tracking connection. The Lua
    // connection retains its existing cache, codec, and atomic expiration behavior.
    internal RespireKey ResolveCoherenceKey(string key) => _client.ResolveKey(key);

    internal RespireClient CreateCoherenceTrackingClient(RespireClientSideCacheOptions trackingOptions)
    {
        if (_wireClient is null)
            throw new InvalidOperationException("HybridCache coherence requires a RespireClient-backed distributed cache.");
        return RespireClient.Create(_wireClient.Core.Options with
        {
            KeyPrefix = default,
            // Coherence channels are explicitly configured physical names, independent
            // of pub/sub prefixes on the application client's options or views.
            PubSubPrefix = default,
            Connections = 1,
            ReadFrom = RespireReadFrom.Primary,
            ClientSideCache = trackingOptions,
        });
    }

    internal IRespireClientSideCache? CoherenceSourceCache => _client.ClientSideCache;

    internal static bool CanTrackCoherenceKey(RespireClient client, in RespireKey key)
        => client.Core.ClientCache?.CanTrack(in key) == true;

    internal bool CanObserveCoherenceSourceKey(in RespireKey key)
        => _wireClient is not null && CanTrackCoherenceKey(_wireClient, in key);

    /// <summary>Wraps an existing client; the caller keeps ownership of it.</summary>
    public RespireDistributedCache(IRespireClient client, RespireCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = ApplyInstanceName(client, options);
        _valueCodec = options?.ValueCodec;
        _wireClient = _client as RespireClient;
    }

    /// <summary>Owns <paramref name="ownedClient"/> — disposes it with the cache.</summary>
    internal RespireDistributedCache(RespireClient ownedClient, RespireCacheOptions? options)
    {
        _ownedClient = ownedClient;
        _client = ApplyInstanceName(ownedClient, options);
        _valueCodec = options?.ValueCodec;
        _wireClient = _client as RespireClient;
    }

    private static IRespireClient ApplyInstanceName(IRespireClient client, RespireCacheOptions? options)
        => string.IsNullOrEmpty(options?.InstanceName) ? client : client.WithKeyPrefix(options.InstanceName);

    /// <inheritdoc/>
    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        => await DispatchResponseSource<byte[]?>.Run((Cache: this, Key: key, Token: token),
            static (state, owner) => state.Cache.GetCoreAsync(state.Key, state.Token, owner)).ConfigureAwait(false);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<byte[]?> GetCoreAsync(string key, CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        using var result = await RunGetScriptAsync(key, returnData: true, token, observation).ConfigureAwait(false);
        if (result.IsNull)
        {
            return null;
        }

        var payload = result[1];
        if (payload.IsNull) return null;
        token.ThrowIfCancellationRequested();
        return _valueCodec is null ? payload.AsBytes() : _valueCodec.Decode(payload.AsSpan());
    }

    /// <inheritdoc/>
    public bool TryGet(string key, IBufferWriter<byte> destination)
        => TryGetAsync(key, destination).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
        => DispatchResponseSource<bool>.Run((Cache: this, Key: key, Destination: destination, Token: token),
            static (state, owner) => state.Cache.TryGetCoreAsync(state.Key, state.Destination, state.Token, owner));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> TryGetCoreAsync(string key, IBufferWriter<byte> destination,
        CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        token.ThrowIfCancellationRequested();
        using var result = await RunGetScriptAsync(key, returnData: true, token, observation).ConfigureAwait(false);
        if (result.IsNull)
        {
            return false;
        }

        var payload = result[1];
        if (payload.IsNull)
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        if (_valueCodec is null) destination.Write(payload.AsSpan());
        else _valueCodec.Decode(payload.AsSpan(), destination);
        return true;
    }

    /// <inheritdoc/>
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            ArgumentNullException.ThrowIfNull(value);
            return DispatchResponseSource.Complete(owner.Attach(DispatchResponseSource.Await(
                SetCoreAsync(key, value, options, token, owner.Observation)))).AsTask();
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    /// <inheritdoc/>
    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        var owner = DispatchResponseSource<bool>.Start();
        try
        {
            // Coalesce segments for the span-based codec; configured encoding then creates its own
            // frame array, which remains owned through the send. The two buffers are intentional.
            return DispatchResponseSource.Complete(owner.Attach(DispatchResponseSource.Await(
                SetCoreAsync(key, value.IsSingleSegment ? value.First : value.ToArray(), options, token, owner.Observation))));
        }
        catch (Exception error) { owner.Fail(error); throw; }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SetCoreAsync(string key, ReadOnlyMemory<byte> value, DistributedCacheEntryOptions options,
        CancellationToken token, RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();
        if (_valueCodec is not null)
        {
            // Own the encoded bytes through the asynchronous send, including abandoned waits.
            // Complete compression before sampling expiry so it cannot inflate the stored TTL.
            value = _valueCodec.Encode(value.Span);
            token.ThrowIfCancellationRequested();
        }

        var trackedWire = await GetTrackedWireAsync(token).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var absoluteExpiration = GetAbsoluteExpiration(now, options);
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
                var execution = await trackedWire.StartTrackedScriptExecutionAsync(
                    SetScript, [key], args, token, errorObservation: observation).ConfigureAwait(false);
                result = await trackedWire.ExecuteWithCorrectionAsync(
                    execution,
                    // TTL correction first uses FIFO ordering and fences only if that pass stalls.
                    // An uncertain write without a TTL correction still needs an explicit fence.
                    ordering: absoluteExpiration.HasValue
                        ? RespireClient.CorrectionOrdering.OrderedCorrection : RespireClient.CorrectionOrdering.FenceFirst,
                    state: (Cache: this, Key: key, Deadline: absoluteExpiration.GetValueOrDefault(), Options: options, Observation: observation),
                    correct: absoluteExpiration.HasValue
                        ? static async (state, identity) =>
                        {
                            try
                            {
                                await state.Cache.CapDelayedTtlAsync(state.Key, state.Deadline, state.Options, identity, state.Observation)
                                    .ConfigureAwait(false);
                            }
                            catch (RespireException correctionFailure) when (
                                identity.ServerClientId == 0 && correctionFailure is not RespireServerException)
                            {
                                state.Observation.Handled(correctionFailure);
                            }
                        }
                        : null).ConfigureAwait(false);
            }
            else
            {
                result = await (_wireClient is { } wire
                    ? wire.ExecuteScriptBorrowedAsync(SetScript, [key], args, token, observation)
                    : ExecuteWrappedScriptAsync(SetScript, [key], args, token, observation)).ConfigureAwait(false);
            }

            result.Dispose();
        }
        catch (Exception ex) when (
            trackedWire is null &&
            ex is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
        {
            // An abandoned wait or lost connection does not prove the send was rejected — a
            // queued set can still reach Redis, and if that send was delayed it stores a TTL
            // computed before the delay. With no reply to measure the delay against, correct
            // unconditionally: a correction that beats a still-queued set no-ops on the
            // ownership check. This untracked/mock/decorator path remains best-effort;
            // tracked operations use the explicit ordering policy above.
            if (absoluteExpiration is { } cancelledDeadline)
            {
                try
                {
                    await CapDelayedTtlAsync(
                        key, cancelledDeadline, options, observation: observation).ConfigureAwait(false);
                }
                catch (RespireException correctionFailure) when (
                    correctionFailure is not RespireServerException)
                {
                    observation.Handled(correctionFailure);
                }
            }

            throw;
        }

        if (absoluteExpiration is { } absolute && DateTimeOffset.UtcNow - now >= SendDelayTolerance)
        {
            await CapDelayedTtlAsync(key, absolute, options, observation: observation).ConfigureAwait(false);
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
        RespireClient.TrackedConnectionIdentity originalConnection = default,
        RespireTelemetry.ErrorObservation observation = default)
        => RunCorrectionAsync(
            CapTtlScript,
            key,
            () =>
            [
                absolute.UtcTicks,
                options.SlidingExpiration?.Ticks ?? NotPresent,
                GetExpirationMilliseconds(DateTimeOffset.UtcNow, absolute, options),
            ],
            originalConnection, observation);

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
        RespireClient.TrackedConnectionIdentity originalConnection = default,
        RespireTelemetry.ErrorObservation observation = default)
    {
        // Convergence can leave an idempotent pass running after its bounded foreground
        // wait. Such passes must never retain the caller's pooled observation.
        var errors = observation.IsEmpty ? null : new CorrectionErrors(observation.Attempts);
        Exception? failure = null;
        try
        {
            await CorrectionCoordinator.ConvergeAsync(originalConnection,
                (Cache: this, Script: script, Key: key, Args: args, Errors: errors),
                static (state, identity) => state.Cache._wireClient is { } wire
                    ? wire.Core.Corrections.CreateFence(wire, identity) : null,
                static (state, ordered, identity) => state.Cache.RunCorrectionPassAsync(
                    state.Script, state.Key, state.Args(), ordered, identity, state.Errors),
                CorrectionWaitBound, SendDelayTolerance).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { errors?.CompleteForeground(observation, failure); }
    }

    private async Task RunCorrectionPassAsync(
        RespireScript script,
        string key,
        RespireValue[] args,
        bool requiresOrdering,
        RespireClient.TrackedConnectionIdentity originalConnection,
        CorrectionErrors? errors)
    {
        errors?.StartPass();
        Exception? failure = null;
        try
        {
            if (requiresOrdering && _wireClient is { } wire)
            {
                await wire.ExecuteOnAllConnectionsAsync(script, [key], args, originalConnection,
                    errors?.Observation ?? default).ConfigureAwait(false);
            }
            else
            {
                using var result = await (_wireClient is { } client && errors is not null
                    ? client.ExecuteScriptBorrowedAsync(script, [key], args, CancellationToken.None, errors.Observation)
                    : ExecuteWrappedScriptAsync(script, [key], args, CancellationToken.None,
                        errors?.Observation ?? default)).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { errors?.CompletePass(failure); }
    }

    // Delayed corrections and interface-based removals allocate this scope. Foreground and outstanding
    // passes each own a reference; the last completion returns the independent failure-only owner.
    private sealed class CorrectionErrors
    {
        private readonly Lock _gate = new();
        private readonly DispatchResponseSource<bool> _owner = DispatchResponseSource<bool>.Start();
        internal RespireTelemetry.ErrorObservation Observation => _owner.Observation;
        private int _owners = 1;
        private bool _foregroundEnded;
        private List<Exception>? _failures;

        internal CorrectionErrors(int attempts) => Observation.SetAttempts(attempts);

        internal void StartPass() { lock (_gate) _owners++; }

        internal void CompletePass(Exception? failure)
        {
            try
            {
                if (failure is null) return;
                lock (_gate)
                {
                    if (!_foregroundEnded)
                    {
                        (_failures ??= []).Add(failure);
                        return;
                    }
                }
                Observation.Handled(failure);
            }
            finally { Release(); }
        }

        internal void CompleteForeground(RespireTelemetry.ErrorObservation caller, Exception? propagated)
        {
            try
            {
                List<Exception>? failures;
                lock (_gate)
                {
                    _foregroundEnded = true;
                    failures = _failures;
                    _failures = null;
                }
                if (failures is not null)
                    foreach (var error in failures)
                        if (!ReferenceEquals(error, propagated)) Observation.Handled(error);
                caller.SetAttempts(Observation.Attempts);
            }
            finally { Release(); }
        }

        private void Release()
        {
            bool completed;
            lock (_gate) completed = --_owners == 0;
            if (completed) _owner.CompleteInternal();
        }
    }

    /// <inheritdoc/>
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task RefreshAsync(string key, CancellationToken token = default)
        => await DispatchResponseSource.Complete(DispatchResponseSource<bool>.Run(
            (Cache: this, Key: key, Token: token),
            static (state, owner) => state.Cache.RefreshCoreAsync(state.Key, state.Token, owner))).ConfigureAwait(false);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> RefreshCoreAsync(string key, CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(key);
        token.ThrowIfCancellationRequested();
        var result = await RunGetScriptAsync(key, returnData: false, token, observation).ConfigureAwait(false);
        result.Dispose();
        return true;
    }

    /// <inheritdoc/>
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async Task RemoveAsync(string key, CancellationToken token = default)
        => await DispatchResponseSource.Run((Cache: this, Key: key, Token: token),
            static (state, owner) => state.Cache.RemoveCoreAsync(state.Key, state.Token, owner)).ConfigureAwait(false);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask RemoveCoreAsync(string key, CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
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
            await wire.UnlinkGuardedAsync(key, token, observation).ConfigureAwait(false);
        }
        else
        {
            await UnlinkWrappedGuardedAsync(key, token, observation).ConfigureAwait(false);
        }
    }

    private async Task UnlinkWrappedGuardedAsync(string key, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        using var timeoutSource = CommandTimeoutCancellation.Create(
            cancellationToken,
            WrappedRemovalTimeout);
        var errors = new CorrectionErrors(observation.Attempts);
        Exception? failure = null;
        try
        {
            await UnlinkWrappedLeasedAsync(key, timeoutSource.Token, errors).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            // The cache owner publishes the normalized timeout; suppress the underlying
            // cancellation as the propagated foreground failure, rather than a retry.
            failure = error;
            throw new RespireTimeoutException("UNLINK", WrappedRemovalTimeout);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally { errors.CompleteForeground(observation, failure); }
    }

    private async Task UnlinkWrappedLeasedAsync(string key, CancellationToken cancellationToken,
        CorrectionErrors errors)
    {
        var leaseTtl = WrappedRemovalMinimumLeaseTtl;
        while (true)
        {
            var effectiveKey = _client.ResolveKey(key);
            var lease = RespireClient.CreateClusterRemovalLeaseKey(effectiveKey.ClusterSlot);

            // If this wait is abandoned, only an unused expiring lease can land late; the
            // deletion script is not sent until placement is confirmed.
            var placementStart = Stopwatch.GetTimestamp();
            await ExecuteWrappedCommandAsync(
                    (Client: _client, Lease: lease, Ttl: leaseTtl, Token: cancellationToken), errors,
                    static state => state.Client.SetAsync(state.Lease, (RespireValue)1, state.Ttl,
                        cancellationToken: state.Token))
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var leaseStart = Stopwatch.GetTimestamp();
            var placementElapsed = Stopwatch.GetElapsedTime(placementStart);
            if (placementElapsed >= leaseTtl / 2)
            {
                // The reply consumed a material part of this lease. No delete has been sent,
                // so retry with more authority when possible. At the safety cap, attempt the
                // leased delete instead of repeating an identical placement until timeout.
                var grownLeaseTtl = GrowWrappedRemovalLease(leaseTtl, placementElapsed);
                if (grownLeaseTtl > leaseTtl)
                {
                    leaseTtl = grownLeaseTtl;
                    continue;
                }
            }

            Task<RespireResult>? removal = null;
            try
            {
                removal = ExecuteWrappedCommandAsync(
                        (Client: _client, Key: key, Lease: lease, Token: cancellationToken), errors,
                        static state => state.Client.Scripts.ExecuteAsync(
                            RespireClient.LeasedUnlinkScript, [state.Key, state.Lease],
                            cancellationToken: state.Token))
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

                await MakeWrappedRemovalHarmlessAsync(lease, leaseStart, leaseTtl, errors).ConfigureAwait(false);
                throw;
            }
        }
    }

    private TimeSpan GrowWrappedRemovalLease(TimeSpan current, TimeSpan observedLatency)
    {
        var maximumTicks = Math.Min(
            WrappedRemovalMaximumLeaseTtl.Ticks,
            WrappedRemovalTimeout.Ticks);
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
        TimeSpan leaseTtl,
        CorrectionErrors errors)
    {
        var remaining = leaseTtl - Stopwatch.GetElapsedTime(leaseStart);
        if (remaining > TimeSpan.Zero)
        {
            Task<long>? revoke = null;
            try
            {
                revoke = ExecuteWrappedCommandAsync((Client: _client, Lease: lease), errors,
                    static state => state.Client.Keys.UnlinkAsync(state.Lease)).AsTask();
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

    private ValueTask<RespireResult> ExecuteWrappedScriptAsync(RespireScript script,
        RespireKey[] keys, RespireValue[] args, CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
        => DispatchResponseSource.InvokeDecorated(
            (Client: _client, Script: script, Keys: keys, Args: args, Token: token), observation,
            static state => state.Client.Scripts.ExecuteAsync(state.Script, state.Keys, state.Args, state.Token));

    // A bounded removal wait can leave its command running. Retain the independent owner
    // until that command finishes, then report its failure internally if foreground ended.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<TResult> ExecuteWrappedCommandAsync<TState, TResult>(
        TState state, CorrectionErrors errors, Func<TState, ValueTask<TResult>> send)
    {
        errors.StartPass();
        Exception? failure = null;
        try { return await DispatchResponseSource.InvokeDecorated(state, errors.Observation, send).ConfigureAwait(false); }
        catch (Exception error) { failure = error; throw; }
        finally { errors.CompletePass(failure); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireResult> RunGetScriptAsync(string key, bool returnData, CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
    {
        var trackedWire = await GetTrackedWireAsync(token).ConfigureAwait(false);
        return await RunGetScriptCoreAsync(key, returnData, token, trackedWire, observation).ConfigureAwait(false);
    }

    private async ValueTask<RespireResult> RunGetScriptCoreAsync(string key, bool returnData, CancellationToken token,
        RespireClient? trackedWire, RespireTelemetry.ErrorObservation observation)
    {
        var now = DateTimeOffset.UtcNow;
        RespireResult result;
        RespireClient.TrackedConnectionIdentity originalConnection = default;
        RespireClient.TrackedScriptExecution? trackedExecution = null;
        try
        {
            var args = new RespireValue[] { returnData ? "1" : "0", now.UtcTicks };
            if ((trackedWire ?? _wireClient) is { } wire)
            {
                // Real-client fallback still borrows this owner. Timestamp-only tracking
                // preserves best-effort correction when CLIENT ID is unavailable.
                trackedExecution = await wire.StartTrackedScriptExecutionAsync(
                        GetAndRefreshScript, [key], args, token,
                        captureSendTimestampOnly: trackedWire is null, errorObservation: observation)
                    .ConfigureAwait(false);
                result = await ((RespireClient.ITrackedCorrectionExecution<RespireResult>)trackedExecution)
                    .Response.ConfigureAwait(false);
            }
            else
            {
                result = await ExecuteWrappedScriptAsync(
                    GetAndRefreshScript, [key], args, token, observation).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
        {
            originalConnection = trackedExecution?.ConnectionIdentity ?? default;
            // Same hazard as the set path: an abandoned or failed reply wait can leave the
            // script's execution uncertain, and a delayed send would re-arm the sliding TTL
            // from a stale "now". The real client completes its exact-connection safety
            // barrier when an ID was captured; untracked and mock/decorator corrections remain
            // best-effort as in SetCoreAsync.
            try
            {
                await CapRefreshedTtlAsync(key, originalConnection, observation).ConfigureAwait(false);
            }
            catch (RespireException correctionFailure) when (
                originalConnection.ServerClientId == 0 && correctionFailure is not RespireServerException)
            {
                observation.Handled(correctionFailure);
            }

            throw;
        }

        // The re-arm the script just performed used a "now" stale by however long the send
        // stalled; past the tolerance, shrink the TTL back to a fresh-clock remainder — but
        // only when the script says the re-arm had an absolute deadline in play, the one
        // shape staleness can over-extend. Runs without the caller's token — the entry was
        // already extended, so undoing the over-extension must not be skippable by
        // cancellation.
        try
        {
            if (DateTimeOffset.UtcNow - now >= SendDelayTolerance && !result.IsNull && result[0].AsInteger() == 1)
            {
                await CapRefreshedTtlAsync(key, observation: observation).ConfigureAwait(false);
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private ValueTask<RespireClient?> GetTrackedWireAsync(CancellationToken cancellationToken)
        => _wireClient is { } wire
            ? wire.GetCorrectionTrackingClientAsync(cancellationToken)
            : default;

    private ValueTask CapRefreshedTtlAsync(
        string key,
        RespireClient.TrackedConnectionIdentity originalConnection = default,
        RespireTelemetry.ErrorObservation observation = default)
        => RunCorrectionAsync(
            CapRefreshedTtlScript,
            key,
            () => [DateTimeOffset.UtcNow.UtcTicks],
            originalConnection, observation);

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
