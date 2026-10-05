using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Respire.OutputCaching;

/// <summary>Output cache with Microsoft-compatible storage or opt-in generation-aware tagging.</summary>
/// <remarks>
/// The supplied client remains owned by the caller. Values are stored unchanged, so instances using
/// Microsoft.AspNetCore.OutputCaching.StackExchangeRedis can share the same InstanceName in MicrosoftCompatible mode.
/// That mode has the Microsoft store's non-transactional tagging boundary. GenerationAware uses a separate
/// hash format and compares generations atomically before deleting a value; it does not interoperate with Microsoft writers.
/// MicrosoftCompatible tagged writes require Redis 6.2+ for a shared absolute value/tag expiry deadline.
/// GenerationAware requires Redis 7+ and an InstanceName with a nonempty Redis hash tag.
/// </remarks>
public sealed partial class RespireOutputCacheStore : IOutputCacheBufferStore
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
    private readonly ILogger<RespireOutputCacheStore> _logger;
    private readonly bool _generationAware;

    /// <summary>Creates a store over an existing client without taking ownership of it.</summary>
    public RespireOutputCacheStore(IRespireClient client, RespireOutputCacheOptions? options = null,
        ILogger<RespireOutputCacheStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!options.HasValidTaggingMode) throw new ArgumentOutOfRangeException(nameof(options), "TaggingMode must be a defined value.");
        if (!options.HasValidGenerationNamespace)
            throw new ArgumentException("GenerationAware tagging requires a nonempty Redis hash tag in InstanceName.", nameof(options));
        _client = client;
        _generationAware = options.TaggingMode == RespireOutputCacheTaggingMode.GenerationAware;
        _valuePrefix = options.InstanceName + (_generationAware ? "__RPOCV2_" : "__MSOCV_");
        _tagPrefix = options.InstanceName + (_generationAware ? "__RPOCT2_" : "__MSOCT_");
        _tagMaster = options.InstanceName + (_generationAware ? "__RPOCT2" : "__MSOCT");
        _cleanupLock = options.InstanceName + (_generationAware ? "__RPOCT2GC" : "__MSOCTGC");
        _clock = options.TimeProvider;
        _logger = logger ?? NullLogger<RespireOutputCacheStore>.Instance;
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        return _generationAware
            ? _client.Hashes.GetBytesAsync(_valuePrefix + key, PayloadField, cancellationToken)
            : _client.GetBytesAsync(_valuePrefix + key, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryGetAsync(string key, PipeWriter destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (_generationAware)
        {
            var bytes = await _client.Hashes.GetBytesAsync(_valuePrefix + key, PayloadField, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return false;
            destination.Write(bytes);
        }
        else
        {
            using var lease = await _client.Strings.GetLeaseAsync(_valuePrefix + key, cancellationToken).ConfigureAwait(false);
            if (lease.IsNull) return false;
            destination.Write(lease.Span);
        }
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
        if (_generationAware)
        {
            await SetGenerationAsync(key, value, tags, expires, cancellationToken).ConfigureAwait(false);
            return;
        }
        for (var start = 0; start < tags.Length;)
        {
            using var batch = _client.CreateBatch();
            var end = start + Math.Min(125, tags.Length - start);
            for (var index = start; index < end; index++)
            {
                var tag = tags.Span[index];
                _ = batch.Scripts.Evaluate(RecordTagExpiry, [_tagMaster], [tag, expires]);
                // Preserve the longest potentially published value's membership too.
                _ = batch.Scripts.Evaluate(RecordTagExpiry, [_tagPrefix + tag], [key, expires]);
            }
            // Single-key scripts remain Cluster-safe. Every registration must succeed before SET.
            await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            start = end;
        }
        cancellationToken.ThrowIfCancellationRequested();
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
        if (_generationAware)
        {
            await EvictGenerationsAsync(tagKey, members, cancellationToken).ConfigureAwait(false);
            return;
        }
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
    /// <remarks>MicrosoftCompatible shares the Microsoft store's cleanup lock; GenerationAware uses an isolated lock.
    /// Each pass removes only scores at or before its captured cutoff.
    /// Cancellable lock renewal requires Redis CLIENT ID and CLIENT KILL permissions.</remarks>
    public async ValueTask CollectExpiredTagsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lockLifetime = TimeSpan.FromMinutes(5);
        await using var attempt = await _client.Locks.AcquireAsync(_cleanupLock, lockLifetime, cancellationToken).ConfigureAwait(false);
        if (!attempt.Acquired) return;
        var cutoff = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        List<RespireValue> tags = new(250);
        await foreach (var tag in _client.SortedSets.ScanAsync(_tagMaster, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            tags.Add(tag.Member);
            if (tags.Count == 250)
            {
                await RemoveExpiredMembersAsync(tags, cutoff, cancellationToken).ConfigureAwait(false);
                tags.Clear();
                if (!await attempt.Lock.ResetExpiryAsync(lockLifetime, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogDebug("Respire output-cache cleanup lost its lock; skipping the remaining sweep and master purge.");
                    return;
                }
            }
        }
        if (tags.Count != 0) await RemoveExpiredMembersAsync(tags, cutoff, cancellationToken).ConfigureAwait(false);
        // Both passes use the same cutoff. Concurrent writes with later deadlines survive the
        // per-tag removal and increase the master score, so the final purge preserves that tag.
        await _client.SortedSets.RemoveRangeByScoreAsync(_tagMaster, 0, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveExpiredMembersAsync(List<RespireValue> tags, long cutoff, CancellationToken cancellationToken)
    {
        using var batch = _client.CreateBatch();
        foreach (var tag in tags)
            _ = batch.SortedSets.RemoveRangeByScore(_tagPrefix + tag, 0, cutoff);
        await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }
}
