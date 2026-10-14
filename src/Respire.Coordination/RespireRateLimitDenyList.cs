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
    private readonly LinkedList<string> _recency = new();
    private readonly SortedSet<(long Started, string Identity)> _expiry = new(
        Comparer<(long Started, string Identity)>.Create(static (left, right) =>
        {
            var age = left.Started.CompareTo(right.Started);
            return age != 0 ? age : StringComparer.Ordinal.Compare(left.Identity, right.Identity);
        }));
    private readonly Dictionary<string, PendingRead> _pending = new(StringComparer.Ordinal);
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
        if (_cacheDuration == TimeSpan.Zero)
            return await ReadUncachedAsync(identity, cancellationToken).ConfigureAwait(false);

        PendingRead? pending;
        long generation;
        long started;
        bool ownsRead;
        lock (_gate)
        {
            started = _time.GetTimestamp();
            if (_cache.TryGetValue(identity, out var entry))
            {
                if (_time.GetElapsedTime(entry.Started, started) < _cacheDuration)
                {
                    _recency.Remove(entry.Node);
                    _recency.AddLast(entry.Node);
                    return entry.Denied;
                }
                RemoveEntry(identity);
            }
            generation = _generation;
            if (_pending.TryGetValue(identity, out pending)
                && _time.GetElapsedTime(pending.Started, started) >= _cacheDuration)
            {
                // Existing waiters keep their read, but later callers need a fresh membership decision.
                _pending.Remove(identity);
                pending = null;
            }
            ownsRead = pending is null;
            if (ownsRead)
            {
                // Bound bookkeeping under distinct-identity floods. Excess reads remain caller-owned.
                if (_pending.Count < _cacheCapacity)
                {
                    pending = new(started);
                    _pending.Add(identity, pending);
                }
            }
        }
        if (pending is null)
            return await ReadUncachedAsync(identity, cancellationToken).ConfigureAwait(false);
        // Start outside the gate: custom clients may run synchronous code before returning a ValueTask.
        if (ownsRead) _ = ReadAndPublishAsync(identity, pending, generation, started);
        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ReadUncachedAsync(string identity, CancellationToken cancellationToken)
    {
        var denied = await _read(identity, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return denied;
    }

    private async Task ReadAndPublishAsync(string identity, PendingRead pending,
        long generation, long started)
    {
        try
        {
            // Cancelling one waiter must not cancel the read used by other waiters.
            var denied = await _read(identity, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                // Never republish a read that overlapped invalidation or extend freshness by network latency.
                if (generation == _generation && _time.GetElapsedTime(started) < _cacheDuration)
                {
                    if (_cache.Count >= _cacheCapacity)
                    {
                        // Reads can finish out of order; expiry follows their start time, not publication or usage.
                        var now = _time.GetTimestamp();
                        while (_expiry.Count > 0)
                        {
                            var oldest = _expiry.Min;
                            if (_time.GetElapsedTime(oldest.Started, now) < _cacheDuration) break;
                            RemoveEntry(oldest.Identity);
                        }
                        if (_cache.Count >= _cacheCapacity) RemoveEntry(_recency.First!.Value);
                    }
                    var added = _recency.AddLast(identity);
                    _cache.Add(identity, new Entry(denied, started, added));
                    _expiry.Add((started, identity));
                }
                RemovePending(identity, pending);
            }
            pending.Completion.TrySetResult(denied);
        }
        catch (Exception exception)
        {
            lock (_gate) RemovePending(identity, pending);
            pending.Completion.TrySetException(exception);
            // Observe failures even when every waiter has cancelled.
            _ = pending.Completion.Task.Exception;
        }
    }

    private void RemovePending(string identity, PendingRead pending)
    {
        if (_pending.TryGetValue(identity, out var current) && ReferenceEquals(current, pending))
            _pending.Remove(identity);
    }

    private void RemoveEntry(string identity)
    {
        if (_cache.Remove(identity, out var entry))
        {
            _recency.Remove(entry.Node);
            _expiry.Remove((entry.Started, identity));
        }
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
            _recency.Clear();
            _expiry.Clear();
            _pending.Clear();
        }
    }

    private static TimeSpan ValidateDuration(TimeSpan cacheDuration)
        => cacheDuration >= TimeSpan.Zero && cacheDuration <= TimeSpan.FromMinutes(1)
            ? cacheDuration : throw new ArgumentOutOfRangeException(nameof(cacheDuration));

    private sealed class PendingRead(long started)
    {
        public long Started { get; } = started;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct Entry(bool Denied, long Started, LinkedListNode<string> Node);
}
