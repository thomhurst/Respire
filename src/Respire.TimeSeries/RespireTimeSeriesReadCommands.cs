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
    /// emitted timestamps are not replayed. An empty reply after deletion retries the same cursor; a sentinel that
    /// has not yet returned samples is resolved again by the server. Enumeration ends after long.MaxValue.
    /// </remarks>
    public async IAsyncEnumerable<RespireTimeSeriesSample> FollowAsync(RespireKey key, RespireTimeSeriesTimestamp timestamp,
        int batchSize = 256, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        timestamp.RequireRead(nameof(timestamp));
        var options = new RespireTimeSeriesReadOptions { BlockMilliseconds = 0, MaximumCount = batchSize };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadAsync(key, timestamp, options, cancellationToken).ConfigureAwait(false);
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
