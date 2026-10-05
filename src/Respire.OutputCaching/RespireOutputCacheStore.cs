using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.OutputCaching;

namespace Respire.OutputCaching;

/// <summary>Output cache using the Microsoft Redis output cache's value and tag storage format.</summary>
/// <remarks>
/// The supplied client remains owned by the caller. Values are stored unchanged, so instances using
/// Microsoft.AspNetCore.OutputCaching.StackExchangeRedis can share the same InstanceName.
/// Tag updates and eviction have the same non-transactional concurrency boundary as that store.
/// Tagged writes require Redis 6.2+ for a shared absolute value/tag expiry deadline.
/// </remarks>
public sealed class RespireOutputCacheStore : IOutputCacheBufferStore
{
    private static readonly RespireScript RecordTagExpiry = RespireScript.Create("""
        local previous = redis.call('ZSCORE', KEYS[1], ARGV[1])
        if not previous or tonumber(previous) < tonumber(ARGV[2]) then
          redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
        end
        return 1
        """);

    private readonly IRespireClient _client;
    private readonly string _valuePrefix;
    private readonly string _tagPrefix;
    private readonly RespireKey _tagMaster;
    private readonly RespireKey _cleanupLock;
    private readonly TimeProvider _clock;

    /// <summary>Creates a store over an existing client without taking ownership of it.</summary>
    public RespireOutputCacheStore(IRespireClient client, RespireOutputCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        _client = client;
        _valuePrefix = options.InstanceName + "__MSOCV_";
        _tagPrefix = options.InstanceName + "__MSOCT_";
        _tagMaster = options.InstanceName + "__MSOCT";
        _cleanupLock = options.InstanceName + "__MSOCTGC";
        _clock = options.TimeProvider;
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return _client.GetBytesAsync(_valuePrefix + key, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryGetAsync(string key, PipeWriter destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = await _client.Strings.GetLeaseAsync(_valuePrefix + key, cancellationToken).ConfigureAwait(false);
        if (lease.IsNull) return false;
        destination.Write(lease.Span);
        var flush = await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (flush.IsCanceled) throw new OperationCanceledException(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetAsync(key, new ReadOnlySequence<byte>(value),
            tags is null ? ReadOnlyMemory<string>.Empty : tags.AsMemory(), validFor, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, ReadOnlyMemory<string> tags,
        TimeSpan validFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (validFor < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(validFor), "The lifetime must be at least one millisecond.");
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var tag in tags.Span) ArgumentNullException.ThrowIfNull(tag);
        // Register every tag before publishing the value. A failed registration must not publish
        // a response that its tags cannot invalidate. One absolute deadline keeps a delayed SET
        // from outliving its tag references; it also validates timestamp overflow before any I/O.
        var expiresAt = _clock.GetUtcNow() + validFor;
        var expires = expiresAt.ToUnixTimeMilliseconds();
        for (var index = 0; index < tags.Length; index++)
        {
            var tag = tags.Span[index];
            using var result = await _client.Scripts.ExecuteAsync(RecordTagExpiry,
                [_tagMaster], [tag, expires], cancellationToken).ConfigureAwait(false);
            await _client.SortedSets.AddAsync(_tagPrefix + tag, (RespireValue)key, expires, cancellationToken).ConfigureAwait(false);
        }
        var expiry = tags.IsEmpty ? RespireExpiry.In(validFor) : RespireExpiry.At(expiresAt);
        if (value.IsSingleSegment)
            await _client.SetAsync(_valuePrefix + key, (RespireValue)value.First, expiry, cancellationToken: cancellationToken).ConfigureAwait(false);
        else
            await _client.Strings.SetAsync(_valuePrefix + key, value, expiry, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        cancellationToken.ThrowIfCancellationRequested();
        var tagKey = (RespireKey)(_tagPrefix + tag);
        List<string> members = new(250);
        await foreach (var entry in _client.SortedSets.ScanAsync(tagKey, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            members.Add(entry.Member);
            if (members.Count == 250)
            {
                await EvictMembersAsync(tagKey, members, cancellationToken).ConfigureAwait(false);
                members.Clear();
            }
        }
        if (members.Count != 0) await EvictMembersAsync(tagKey, members, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EvictMembersAsync(RespireKey tagKey, List<string> members, CancellationToken cancellationToken)
    {
        if (members.Count == 1)
        {
            await _client.DeleteAsync([_valuePrefix + members[0]], cancellationToken).ConfigureAwait(false);
            await _client.SortedSets.RemoveAsync(tagKey, [members[0]], cancellationToken).ConfigureAwait(false);
            return;
        }
        // Pipeline separate DELs so values in different Cluster slots still work. Only remove
        // tag memberships after every delete succeeds, leaving failed deletes retryable.
        using var batch = _client.CreateBatch();
        var values = new RespireValue[members.Count];
        for (var index = 0; index < members.Count; index++)
        {
            _ = batch.Keys.Delete(_valuePrefix + members[index]);
            values[index] = members[index];
        }
        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        await _client.SortedSets.RemoveAsync(tagKey, values, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes expired tag references. DI registration runs this periodically in a hosted service.</summary>
    /// <remarks>Shares the Microsoft store's cleanup lock. Each pass removes only scores at or before its captured cutoff.
    /// Cancellable lock renewal requires Redis CLIENT ID and CLIENT KILL permissions.</remarks>
    public async ValueTask CollectExpiredTagsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockLifetime = TimeSpan.FromMinutes(5);
        await using var attempt = await _client.Locks.AcquireAsync(_cleanupLock, lockLifetime, cancellationToken).ConfigureAwait(false);
        if (!attempt.Acquired) return;
        var cutoff = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var untilRenewal = 250;
        await foreach (var tag in _client.SortedSets.ScanAsync(_tagMaster, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await _client.SortedSets.RemoveRangeByScoreAsync(_tagPrefix + tag.Member, 0, cutoff, cancellationToken).ConfigureAwait(false);
            if (--untilRenewal == 0)
            {
                if (!await attempt.Lock.ResetExpiryAsync(lockLifetime, cancellationToken).ConfigureAwait(false)) return;
                untilRenewal = 250;
            }
        }
        // Both passes use the same cutoff. Concurrent writes with later deadlines survive the
        // per-tag removal and increase the master score, so the final purge preserves that tag.
        await _client.SortedSets.RemoveRangeByScoreAsync(_tagMaster, 0, cutoff, cancellationToken).ConfigureAwait(false);
    }
}
