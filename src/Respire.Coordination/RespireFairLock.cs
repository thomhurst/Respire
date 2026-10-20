using System.Diagnostics;
using System.Globalization;

namespace Respire.Coordination;

/// <summary>A non-reentrant FIFO distributed lock with bounded waiter lifetimes and fenced leases.</summary>
/// <remarks>
/// Arrival means enqueue order on Redis, not local invocation order. A waiter leaves the queue when
/// its wait timeout expires, even if it is still alive. All participants must use the same three
/// dedicated keys, in one Cluster slot. Requires RESP3 client-side caching and tracking. Redis
/// failover or counter rollback can invalidate fencing monotonicity; protected resources must
/// atomically reject stale fencing tokens. The caller retains ownership of the client.
/// </remarks>
public sealed class RespireFairLock
{
    private readonly IRespireClient _client;

    internal RespireFairLock(IRespireClient client, RespireKey key, RespireKey counter, RespireKey queue)
    {
        if (key == counter || key == queue || counter == queue)
            throw new ArgumentException("Lease, fencing counter and queue keys must differ.");
        _client = client;
        Key = key.Snapshot();
        FencingCounterKey = counter.Snapshot();
        QueueKey = queue.Snapshot();
    }

    /// <summary>The lease key before the client's prefix.</summary>
    public RespireKey Key { get; }
    /// <summary>The persistent counter key before the client's prefix.</summary>
    public RespireKey FencingCounterKey { get; }
    /// <summary>The expiring FIFO queue hash key before the client's prefix.</summary>
    public RespireKey QueueKey { get; }

    // A doubly linked list in one hash permits O(1) cancellation without scanning all waiters.
    // The whole hash expires at the latest waiter deadline, including when every client crashes.
    private const string QueuePrelude = """
        local function remove(owner)
            if not redis.call('HGET', KEYS[3], 'd' .. owner) then return end
            local previous = redis.call('HGET', KEYS[3], 'p' .. owner)
            local next = redis.call('HGET', KEYS[3], 'n' .. owner)
            if previous then
                if next then redis.call('HSET', KEYS[3], 'n' .. previous, next)
                else redis.call('HDEL', KEYS[3], 'n' .. previous) end
            elseif next then redis.call('HSET', KEYS[3], 'head', next)
            else redis.call('HDEL', KEYS[3], 'head') end
            if next then
                if previous then redis.call('HSET', KEYS[3], 'p' .. next, previous)
                else redis.call('HDEL', KEYS[3], 'p' .. next) end
            elseif previous then redis.call('HSET', KEYS[3], 'tail', previous)
            else redis.call('HDEL', KEYS[3], 'tail') end
            redis.call('HDEL', KEYS[3], 'd' .. owner, 'p' .. owner, 'n' .. owner)
        end

        """;

    internal static readonly RespireScript RemoveWaiter = RespireScript.Create(QueuePrelude + """
        if ARGV[2] == 'release' and redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('DEL', KEYS[1])
        end
        remove(ARGV[1])
        return 1
        """);

    internal static readonly RespireScript Acquire = RespireScript.Create(QueuePrelude + """
        if KEYS[1] == KEYS[2] or KEYS[1] == KEYS[3] or KEYS[2] == KEYS[3] then
            return redis.error_reply('ERR fair lock keys must differ')
        end
        local queueType = redis.call('TYPE', KEYS[3]).ok
        if queueType ~= 'none' and queueType ~= 'hash' then
            return redis.error_reply('WRONGTYPE fair lock queue must be a hash')
        end
        local counter = redis.call('GET', KEYS[2])
        if counter and (not string.match(counter, '^%d+$') or (#counter > 1 and string.sub(counter, 1, 1) == '0')) then
            return redis.error_reply('ERR fencing counter must be a nonnegative canonical integer')
        end
        if redis.call('PTTL', KEYS[2]) >= 0 then
            return redis.error_reply('ERR fencing counter must not expire')
        end
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local owner = ARGV[1]
        local head = redis.call('HGET', KEYS[3], 'head')
        local cleaned = 0
        while head and cleaned < 64 and tonumber(redis.call('HGET', KEYS[3], 'd' .. head)) <= now do
            remove(head)
            cleaned = cleaned + 1
            head = redis.call('HGET', KEYS[3], 'head')
        end
        local deadline = redis.call('HGET', KEYS[3], 'd' .. owner)
        if not deadline then
            if ARGV[4] ~= 'join' then
                return {-1, '', 0, now, 0, redis.call('PTTL', KEYS[3]), redis.call('HLEN', KEYS[3])}
            end
            deadline = now + tonumber(ARGV[3])
            local tail = redis.call('HGET', KEYS[3], 'tail')
            if tail then
                redis.call('HSET', KEYS[3], 'n' .. tail, owner, 'p' .. owner, tail)
            else
                redis.call('HSET', KEYS[3], 'head', owner)
                head = owner
            end
            redis.call('HSET', KEYS[3], 'tail', owner, 'd' .. owner, string.format('%.0f', deadline))
            if redis.call('PTTL', KEYS[3]) < tonumber(ARGV[3]) then
                redis.call('PEXPIRE', KEYS[3], ARGV[3])
            end
        end
        deadline = tonumber(deadline)
        if deadline <= now then
            local failure = {-1, '', 0, now, deadline, redis.call('PTTL', KEYS[3]), redis.call('HLEN', KEYS[3])}
            remove(owner)
            return failure
        end
        -- Enqueue before yielding so cleanup continuations preserve arrival order and deadline.
        if head and tonumber(redis.call('HGET', KEYS[3], 'd' .. head)) <= now then
            return {2, '', 0, now, deadline}
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl == -1 then return redis.error_reply('ERR fair lock lease must expire') end
        if head == owner and ttl == -2 then
            -- GET preserves every Int64 bit; Lua's INCR return value does not.
            redis.call('INCR', KEYS[2])
            local fence = redis.call('GET', KEYS[2])
            redis.call('SET', KEYS[1], owner, 'PX', ARGV[2])
            remove(owner)
            return {1, fence, 0}
        end
        local delay = math.min(deadline, tonumber(redis.call('HGET', KEYS[3], 'd' .. head))) - now
        if ttl >= 0 then delay = math.min(delay, math.max(1, ttl)) end
        return {0, '', math.max(1, delay), now, deadline}
        """);

    /// <summary>Enqueues once and waits for a fenced lease, throwing <see cref="TimeoutException"/> when the queue deadline expires.</summary>
    /// <param name="duration">Lease duration, at least one millisecond.</param>
    /// <param name="waitTimeout">Maximum server queue lifetime, from one millisecond through 24 days.</param>
    /// <param name="cancellationToken">Cancels waiting and best-effort removes this waiter.</param>
    /// <remarks>
    /// Notifications wake waiters on release or queue changes. Expiry deadlines also wake waiters,
    /// so lost notifications and crashed clients cannot stall the queue indefinitely. Uncertain
    /// commands can leave an unreturned lease until its bounded expiry. Do not mix this protocol
    /// with ordinary lock acquisitions on the same keys.
    /// </remarks>
    public ValueTask<RespireFencedLock> AcquireAsync(TimeSpan duration, TimeSpan waitTimeout,
        CancellationToken cancellationToken = default)
        => CoordinationCommandHooks.RunAsync(_client, "LOCK WAIT FAIR", cancellationToken,
            (Owner: this, Duration: duration, Timeout: waitTimeout, Token: cancellationToken),
            static s => s.Owner.AcquireUnhookedAsync(s.Duration, s.Timeout, s.Token));

    private async ValueTask<RespireFencedLock> AcquireUnhookedAsync(TimeSpan duration, TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var milliseconds = RespireCoordination.ValidateAcquisition(Key, FencingCounterKey, duration);
        var waitMilliseconds = waitTimeout.Ticks / TimeSpan.TicksPerMillisecond;
        if (waitMilliseconds <= 0 || waitTimeout > TimeSpan.FromDays(24))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout), "Wait timeout must be between one millisecond and 24 days.");
        var cache = _client.ClientSideCache ?? throw new RespireConfigurationException(
            "Fair locks require RESP3 client-side caching and tracking.");
        var physicalLeaseKey = _client.ResolveKey(Key).Snapshot();
        var physicalQueueKey = _client.ResolveKey(QueueKey).Snapshot();
        if (_client is RespireClient concrete)
        {
            if (concrete.GetBatchReadFromPolicy() != RespireReadFrom.Primary)
                throw new RespireConfigurationException("Fair locks require primary reads so tracking observes lease and queue writes.");
            if (concrete.Core.Options.ClientSideCache?.NoLoop == true)
                throw new RespireConfigurationException("Fair locks require ClientSideCache.NoLoop=false so same-client releases wake waiters.");
            if (concrete.ReadCache is not { } readCache || !readCache.CanTrack(physicalLeaseKey) || !readCache.CanTrack(physicalQueueKey))
                throw new RespireConfigurationException("Fair locks require cache tracking coverage for both lease and queue keys.");
        }
        using var signal = new SemaphoreSlim(0, 1);
        void OnInvalidation(RespireClientCacheInvalidation invalidation)
        {
            if ((invalidation.Reasons & ~RespireClientCacheInvalidationReason.LocalMutation) == 0) return;
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
        using var leaseSubscription = cache.SubscribeInvalidations(physicalLeaseKey, OnInvalidation, cancellationToken);
        using var queueSubscription = cache.SubscribeInvalidations(physicalQueueKey, OnInvalidation, cancellationToken);
        if (!leaseSubscription.Stopped.CanBeCanceled || !queueSubscription.Stopped.CanBeCanceled)
            throw new NotSupportedException("Fair lock subscriptions must expose cancellable Stopped tokens.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseSubscription.Stopped, queueSubscription.Stopped);
        var owner = RespireLock.NewToken();
        var acquisitionUncertain = false;
        long started = 0;
        long previousServerTime = 0, previousDeadline = 0, previousStarted = 0;
        var transferred = false;
        try
        {
            var join = true;
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                // Subscribe and track both keys before the atomic predicate to avoid lost wakeups.
                await CoordinationCommandHooks.TrackAsync(_client, Key, null, linked.Token).ConfigureAwait(false);
                await CoordinationCommandHooks.TrackAsync(_client, QueueKey, "head", linked.Token).ConfigureAwait(false);
                started = Stopwatch.GetTimestamp();
                acquisitionUncertain = true;
                using var response = await CoordinationCommandHooks.ExecuteAsync(_client, Acquire,
                    [Key, FencingCounterKey, QueueKey], [owner.Bytes, milliseconds, waitMilliseconds, join ? "join" : "retry"], linked.Token).ConfigureAwait(false);
                join = false;
                var status = response[0].AsInteger();
                acquisitionUncertain = status == 1;
                if (status < 0)
                {
                    // Capture the script's atomic observation before finally removes this waiter.
                    // Redis deadlines use wall-clock TIME; local elapsed time is monotonic.
                    var serverTime = response[3].AsInteger();
                    var deadline = response[4].AsInteger();
                    var reason = deadline == 0 ? "is no longer queued" : "reached its deadline";
                    throw new TimeoutException(FormattableString.Invariant(
                        $"The fair lock waiter {reason}. Redis TIME={serverTime}ms, deadline={deadline}ms, previous TIME={previousServerTime}ms, previous deadline={previousDeadline}ms, elapsed since previous attempt={(previousStarted == 0 ? 0 : Stopwatch.GetElapsedTime(previousStarted).TotalMilliseconds):F3}ms, queue PTTL={response[5].AsInteger()}ms, queue fields={response[6].AsInteger()}."));
                }
                if (status == 1)
                {
                    var lease = new RespireLock(_client.Locks, Key, owner,
                        TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond), started, TimeProvider.System);
                    if (!long.TryParse(response[1].AsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var fence) || fence <= 0)
                        throw new RespireProtocolException("Fair lock acquisition did not return a positive Int64 fencing token.");
                    linked.Token.ThrowIfCancellationRequested();
                    if (lease.IsReleased) throw new TimeoutException("The fair lock lease expired before acquisition completed.");
                    transferred = true;
                    return new RespireFencedLock(lease, FencingCounterKey, fence);
                }
                previousServerTime = response[3].AsInteger();
                previousDeadline = response[4].AsInteger();
                previousStarted = started;
                if (status == 2) continue; // Bounded cleanup has more expired heads to remove.
                var delay = TimeSpan.FromMilliseconds(response[2].AsInteger()) - Stopwatch.GetElapsedTime(started);
                if (delay > TimeSpan.Zero)
                    _ = await signal.WaitAsync(delay, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            (leaseSubscription.IsDisposed || queueSubscription.IsDisposed))
        {
            throw new ObjectDisposedException(nameof(IRespireClientSideCache), "The client cache stopped its fair lock subscription.");
        }
        finally
        {
            if (!transferred)
            {
                // Bound both owner-checked lease release and waiter removal, even when command
                // timeouts are disabled. Lease/queue expiry remain the fallback on lost replies.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    using var ignored = await CoordinationCommandHooks.ExecuteAsync(_client, RemoveWaiter,
                        [Key, FencingCounterKey, QueueKey], [owner.Bytes, acquisitionUncertain ? "release" : "remove"], cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }
    }
}
