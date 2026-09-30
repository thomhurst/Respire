using System.Diagnostics;
using System.Globalization;

namespace Respire.Extensions.Coordination;

/// <summary>Coordination primitives using a caller-owned Redis client.</summary>
public sealed class RespireCoordination
{
    private readonly IRespireClient _client;

    /// <summary>Creates coordination operations without taking ownership of <paramref name="client"/>.</summary>
    public RespireCoordination(IRespireClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

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
        var milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        if (key == fencingCounterKey) throw new ArgumentException("Lock and fencing counter keys must differ.", nameof(fencingCounterKey));
        // Both inputs may wrap caller-owned binary buffers. Snapshot before the first await.
        return AcquireAsync(key.Snapshot(), fencingCounterKey.Snapshot(), milliseconds, cancellationToken);
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
