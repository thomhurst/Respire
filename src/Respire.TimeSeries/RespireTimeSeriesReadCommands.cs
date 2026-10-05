using System.Runtime.CompilerServices;

namespace Respire.TimeSeries;

public sealed partial class RespireTimeSeriesClient
{
    /// <summary>Reads timestamp-aligned rows from explicit keys in ascending order. Requires Redis 8.10.</summary>
    /// <remarks>Keys must share a Cluster hash slot. Duplicate keys retain separate value columns.</remarks>
    public ValueTask<IReadOnlyList<RespireTimeSeriesRow>> RangeKeysAsync(
        IReadOnlyList<RespireKey> keys, RespireTimeSeriesRange range, RespireTimeSeriesKeyRangeOptions? options = null,
        CancellationToken cancellationToken = default)
        => ReadKeyRangeAsync(keys, range, options, reverse: false, cancellationToken);

    /// <summary>Reads timestamp-aligned rows from explicit keys in descending order. Requires Redis 8.10.</summary>
    /// <remarks>Keys must share a Cluster hash slot. Duplicate keys retain separate value columns.</remarks>
    public ValueTask<IReadOnlyList<RespireTimeSeriesRow>> ReverseRangeKeysAsync(
        IReadOnlyList<RespireKey> keys, RespireTimeSeriesRange range, RespireTimeSeriesKeyRangeOptions? options = null,
        CancellationToken cancellationToken = default)
        => ReadKeyRangeAsync(keys, range, options, reverse: true, cancellationToken);

    private async ValueTask<IReadOnlyList<RespireTimeSeriesRow>> ReadKeyRangeAsync(
        IReadOnlyList<RespireKey> keys, RespireTimeSeriesRange range, RespireTimeSeriesKeyRangeOptions? options,
        bool reverse, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) throw new ArgumentException("At least one series key is required.", nameof(keys));
        var (from, to) = range.ToTokens(nameof(range));
        var (extra, width) = options?.ToArguments(keys.Count) ?? (Array.Empty<RespireValue>(), keys.Count);
        var arguments = new RespireValue[checked(keys.Count + 3 + extra.Length)];
        arguments[0] = keys.Count;
        for (var index = 0; index < keys.Count; index++) arguments[index + 1] = keys[index];
        arguments[keys.Count + 1] = from;
        arguments[keys.Count + 2] = to;
        extra.CopyTo(arguments, keys.Count + 3);
        using var result = reverse
            ? await _commands.ReverseRangeKeysAsync(arguments, cancellationToken).ConfigureAwait(false)
            : await _commands.RangeKeysAsync(arguments, cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseRows(result, width);
    }

    /// <summary>Lists distinct label names, optionally restricted by label filters. Requires Redis 8.10.</summary>
    /// <remarks>Order is unspecified. Key-prefixed clients reject this keyless query.</remarks>
    public ValueTask<string[]> QueryLabelsAsync(IReadOnlyList<string>? filters = null, CancellationToken cancellationToken = default)
        => QueryLabelTokensAsync(null, filters, cancellationToken);

    /// <summary>Lists distinct values of one label. Requires Redis 8.10.</summary>
    /// <remarks>Order is unspecified. Key-prefixed clients reject this keyless query.</remarks>
    public ValueTask<string[]> QueryLabelValuesAsync(string label, IReadOnlyList<string>? filters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return QueryLabelTokensAsync(label, filters, cancellationToken);
    }

    private async ValueTask<string[]> QueryLabelTokensAsync(string? label, IReadOnlyList<string>? filters, CancellationToken cancellationToken)
    {
        var arguments = new List<RespireValue> { label is null ? "LABELS" : "VALUES" };
        if (label is not null) arguments.Add(label);
        if (filters is not null) RespireTimeSeriesRangeOptions.AppendFilters(arguments, filters, nameof(filters));
        using var result = await _commands.QueryLabelsAsync([.. arguments], cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseLabelTokens(result);
    }

    /// <summary>Reads samples at or after an inclusive timestamp, optionally blocking. Requires Redis 8.10.</summary>
    /// <remarks>
    /// Minimum, Maximum, and New resolve on the server. A blocking timeout or key deletion can return an empty list.
    /// Blocking reads use the dedicated blocking pool; cancellation releases the rented connection.
    /// </remarks>
    public async ValueTask<RespireTimeSeriesRangeResult> ReadAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp,
        RespireTimeSeriesReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var token = timestamp.RequireRead(nameof(timestamp));
        var arguments = options?.ToArguments() ?? [];
        using var result = await _commands.ReadAsync(key, token, arguments, cancellationToken).ConfigureAwait(false);
        return TimeSeriesReplyParser.ParseRange(result);
    }

    /// <summary>Reads existing and future samples, advancing past each emitted timestamp. Requires Redis 8.10.</summary>
    /// <remarks>
    /// Use Minimum to include history, Maximum to include the latest sample, or New for only future samples.
    /// Reads block indefinitely between samples and stop on cancellation. Backfilled samples or updates at already
    /// emitted timestamps are not replayed. New is resolved once with a nonblocking latest-sample read before
    /// following a numeric cursor, so empty-reply backoff cannot re-resolve it and skip intervening samples.
    /// An empty reply after deletion retries the same cursor. Enumeration ends after long.MaxValue.
    /// Each page returns its blocking connection before yielding owned samples; paused consumers retain no pool lease.
    /// </remarks>
    public IAsyncEnumerable<RespireTimeSeriesSample> FollowAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp,
        int batchSize = 256, CancellationToken cancellationToken = default)
        => FollowCoreAsync(key, timestamp, batchSize, null, cancellationToken);

    /// <summary>Follows samples with an optional consecutive-empty-reply limit. Reaching the limit ends enumeration.</summary>
    /// <remarks>The limit counts empty server replies, not elapsed idle time. A quiet BLOCK 0 read can still wait indefinitely.</remarks>
    public IAsyncEnumerable<RespireTimeSeriesSample> FollowAsync(RespireTimeSeriesFollowOptions options,
        RespireKey key, RespireTimeSeriesTimestamp timestamp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FollowCoreAsync(key, timestamp, options.BatchSize, options.MaximumConsecutiveEmptyReads, cancellationToken);
    }

    private async IAsyncEnumerable<RespireTimeSeriesSample> FollowCoreAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp,
        int batchSize, int? maximumConsecutiveEmptyReads, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (maximumConsecutiveEmptyReads is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConsecutiveEmptyReads));
        timestamp.RequireRead(nameof(timestamp));
        if (timestamp == RespireTimeSeriesTimestamp.New)
        {
            var latest = await ReadAsync(key, RespireTimeSeriesTimestamp.Maximum,
                new RespireTimeSeriesReadOptions { MaximumCount = 1 }, cancellationToken).ConfigureAwait(false);
            if (latest.Samples.Count == 0) timestamp = 0;
            else
            {
                var last = latest.Samples[^1].Timestamp;
                if (last == long.MaxValue) yield break;
                timestamp = last + 1;
            }
        }
        var options = new RespireTimeSeriesReadOptions { BlockMilliseconds = 0, MaximumCount = batchSize };
        var emptyDelayMilliseconds = 100;
        var emptyReads = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadAsync(key, timestamp, options, cancellationToken).ConfigureAwait(false);
            if (result.Samples.Count == 0)
            {
                if (maximumConsecutiveEmptyReads is { } maximum && ++emptyReads >= maximum) yield break;
                // Deletion can unblock an indefinite read without samples. Bound retries even if
                // a server returns empty immediately, and keep cancellation responsive while waiting.
                await Task.Delay(emptyDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                emptyDelayMilliseconds = Math.Min(emptyDelayMilliseconds * 2, 1000);
                continue;
            }
            emptyDelayMilliseconds = 100;
            emptyReads = 0;
            foreach (var sample in result.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return sample;
                if (sample.Timestamp == long.MaxValue) yield break;
                timestamp = sample.Timestamp + 1;
            }
        }
    }
}
