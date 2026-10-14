namespace Respire.Coordination;

/// <summary>A Redis set of denied identities with a bounded, short-lived local membership cache.</summary>
/// <remarks>Share an instance across limiters. Updates invalidate this instance's cache; other instances
/// observe changes after cache expiry or explicit invalidation. Reads and admission are not atomic.
/// Redis failures propagate and prevent admission; cached entries remain usable until expiry.</remarks>
public sealed class RespireRateLimitDenyList
{
    private readonly Func<string, CancellationToken, ValueTask<bool>> _read;
    private readonly Func<string, bool, CancellationToken, ValueTask> _write;
    private readonly TimeProvider _time;
    private readonly TimeSpan _cacheDuration;
    private readonly int _cacheCapacity;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private long _generation;

    /// <summary>Creates a deny list stored in a dedicated Redis set, respecting client key prefixes.</summary>
    /// <param name="client">The client used for membership reads and updates; remains caller-owned.</param>
    /// <param name="key">The dedicated Redis set key.</param>
    /// <param name="cacheDuration">Cache lifetime, from zero (disabled) to one minute; defaults to one second.</param>
    /// <param name="cacheCapacity">Maximum locally cached identities; must be positive.</param>
    public RespireRateLimitDenyList(IRespireClient client, RespireKey key,
        TimeSpan? cacheDuration = null, int cacheCapacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (key.IsEmpty) throw new ArgumentException("The deny-list key must not be empty.", nameof(key));
        _cacheDuration = ValidateDuration(cacheDuration ?? TimeSpan.FromSeconds(1));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);
        _cacheCapacity = cacheCapacity;
        _time = TimeProvider.System;
        var snapshot = key.Snapshot();
        var coordination = new RespireCoordination(client);
        _read = async (identity, token) =>
        {
            using var result = await coordination.ExecuteRateLimitScriptAsync(ReadScript, snapshot,
                [identity], token).ConfigureAwait(false);
            return result.AsInteger() == 1;
        };
        _write = async (identity, denied, token) =>
        {
            using var result = await coordination.ExecuteRateLimitScriptAsync(WriteScript, snapshot,
                [identity, denied ? 1 : 0], token).ConfigureAwait(false);
        };
    }

    internal RespireRateLimitDenyList(Func<string, CancellationToken, ValueTask<bool>> read,
        Func<string, bool, CancellationToken, ValueTask> write, TimeProvider time,
        TimeSpan cacheDuration, int cacheCapacity)
    {
        _read = read;
        _write = write;
        _time = time;
        _cacheDuration = ValidateDuration(cacheDuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheCapacity);
        _cacheCapacity = cacheCapacity;
    }

    private static readonly RespireScript ReadScript = RespireScript.Create("return redis.call('SISMEMBER', KEYS[1], ARGV[1])");
    private static readonly RespireScript WriteScript = RespireScript.Create("""
        if ARGV[2] == '1' then return redis.call('SADD', KEYS[1], ARGV[1]) end
        return redis.call('SREM', KEYS[1], ARGV[1])
        """);

    /// <summary>Reads membership, using the local cache until its fixed lifetime expires.</summary>
    public async ValueTask<bool> IsDeniedAsync(string identity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        long started;
        lock (_gate)
        {
            started = _time.GetTimestamp();
            if (_cache.TryGetValue(identity, out var entry)
                && _time.GetElapsedTime(entry.Started, started) < _cacheDuration)
                return entry.Denied;
            _cache.Remove(identity);
            generation = _generation;
        }
        var denied = await _read(identity, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Never republish a read that overlapped invalidation, or extend staleness by network latency.
            if (generation == _generation && _time.GetElapsedTime(started) < _cacheDuration)
            {
                // Clearing on overflow bounds both entries and bookkeeping without an unbounded eviction queue.
                if (_cache.Count >= _cacheCapacity && !_cache.ContainsKey(identity)) _cache.Clear();
                _cache[identity] = new Entry(denied, started);
            }
        }
        return denied;
    }

    /// <summary>Adds or removes an identity in Redis, then invalidates this instance's cache even on failure.</summary>
    /// <remarks>Membership has no automatic expiry. Concurrent updates follow Redis execution order.
    /// A cancelled or failed update may have reached Redis; the next read resolves its outcome.</remarks>
    public async ValueTask SetDeniedAsync(string identity, bool denied, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        cancellationToken.ThrowIfCancellationRequested();
        Invalidate();
        try { await _write(identity, denied, cancellationToken).ConfigureAwait(false); }
        finally { Invalidate(); }
    }

    /// <summary>Clears all locally cached memberships and prevents in-flight reads from repopulating them.</summary>
    /// <remarks>Does not cancel admissions already in progress and does not notify other instances.</remarks>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
        }
    }

    private static TimeSpan ValidateDuration(TimeSpan duration)
        => duration >= TimeSpan.Zero && duration <= TimeSpan.FromMinutes(1)
            ? duration : throw new ArgumentOutOfRangeException(nameof(duration));

    private readonly record struct Entry(bool Denied, long Started);
}
