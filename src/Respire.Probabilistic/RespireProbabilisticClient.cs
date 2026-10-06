using Respire.Protocol;

namespace Respire.Probabilistic;

/// <summary>Typed operations for Redis Bloom, Cuckoo, Count-Min, Top-K, and t-digest structures.</summary>
public sealed class RespireProbabilisticClient
{
    private readonly IRespireProbabilisticCommandsImplementation _commands;

    /// <summary>Creates probabilistic operations over a caller-owned Respire client.</summary>
    public RespireProbabilisticClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _commands = new IRespireProbabilisticCommandsImplementation(client);
    }

    /// <summary>Creates a Bloom filter.</summary>
    public async ValueTask<bool> BloomReserveAsync(RespireKey key, double errorRate, long capacity, RespireBloomReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateErrorRate(errorRate, nameof(errorRate));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var result = await _commands.BloomReserveAsync(key, errorRate, capacity, (options ?? new()).ToArguments(), cancellationToken).ConfigureAwait(false);
        return IsOk(result);
    }

    /// <summary>Adds one item to a Bloom filter.</summary>
    public async ValueTask<bool> BloomAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.BloomAddAsync(key, item, cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Checks whether a Bloom filter may contain an item.</summary>
    public async ValueTask<bool> BloomExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.BloomExistsAsync(key, item, cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Adds multiple items to a Bloom filter.</summary>
    public async ValueTask<bool[]> BloomMultiAddAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.BloomMultiAddAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Checks multiple items against a Bloom filter.</summary>
    public async ValueTask<bool[]> BloomMultiExistsAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.BloomMultiExistsAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Creates a Bloom filter when needed and inserts multiple items.</summary>
    public async ValueTask<bool[]> BloomInsertAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireBloomInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = (options ?? new()).ToArguments(items);
        using var result = await _commands.BloomInsertAsync(key, arguments, cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Returns the raw BF.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> BloomInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.BloomInfoAsync(key, cancellationToken);

    /// <summary>Returns Bloom filter cardinality.</summary>
    public async ValueTask<long> BloomCardinalityAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.BloomCardinalityAsync(key, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Reads the next Bloom filter dump chunk.</summary>
    public async ValueTask<RespireProbabilisticDumpChunk> BloomScanDumpAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.BloomScanDumpAsync(key, iterator, cancellationToken).ConfigureAwait(false);
        return ParseDumpChunk(result);
    }

    /// <summary>Loads a Bloom filter dump chunk.</summary>
    public async ValueTask BloomLoadChunkAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(chunk.Data);
        using var result = await _commands.BloomLoadChunkAsync(key, chunk.Iterator, chunk.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a Cuckoo filter.</summary>
    public async ValueTask<bool> CuckooReserveAsync(RespireKey key, long capacity, RespireCuckooReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var result = await _commands.CuckooReserveAsync(key, capacity, (options ?? new()).ToArguments(), cancellationToken).ConfigureAwait(false);
        return IsOk(result);
    }

    /// <summary>Adds an item to a Cuckoo filter.</summary>
    public async ValueTask<bool> CuckooAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.CuckooAddAsync(key, item, cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Adds an item only when it is not already present.</summary>
    public async ValueTask<bool> CuckooAddIfAbsentAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.CuckooAddIfAbsentAsync(key, item, cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Creates a Cuckoo filter when needed and inserts multiple items, reporting per item whether it was inserted or the filter was full.</summary>
    public async ValueTask<RespireCuckooInsertResult[]> CuckooInsertAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.CuckooInsertAsync(key, (options ?? new()).ToArguments(items), cancellationToken).ConfigureAwait(false);
        return ReadCuckooInsertResults(result);
    }

    /// <summary>Creates a Cuckoo filter when needed and inserts only absent items, reporting per item whether it was inserted, already present, or the filter was full.</summary>
    public async ValueTask<RespireCuckooInsertResult[]> CuckooInsertIfAbsentAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.CuckooInsertIfAbsentAsync(key, (options ?? new()).ToArguments(items), cancellationToken).ConfigureAwait(false);
        return ReadCuckooInsertResults(result);
    }

    /// <summary>Deletes one item from a Cuckoo filter.</summary>
    public async ValueTask<bool> CuckooDeleteAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.CuckooDeleteAsync(key, item, cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Checks whether a Cuckoo filter may contain an item.</summary>
    public async ValueTask<bool> CuckooExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.CuckooExistsAsync(key, item, cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Checks whether a Cuckoo filter may contain each item.</summary>
    public async ValueTask<bool[]> CuckooMultiExistsAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.CuckooMultiExistsAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Counts copies of one item in a Cuckoo filter.</summary>
    public async ValueTask<long> CuckooCountAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await _commands.CuckooCountAsync(key, item, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Returns the raw CF.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> CuckooInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.CuckooInfoAsync(key, cancellationToken);

    /// <summary>Reads the next Cuckoo filter dump chunk.</summary>
    public async ValueTask<RespireProbabilisticDumpChunk> CuckooScanDumpAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.CuckooScanDumpAsync(key, iterator, cancellationToken).ConfigureAwait(false);
        return ParseDumpChunk(result);
    }

    /// <summary>Loads a Cuckoo filter dump chunk.</summary>
    public async ValueTask CuckooLoadChunkAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(chunk.Data);
        using var result = await _commands.CuckooLoadChunkAsync(key, chunk.Iterator, chunk.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Initializes Count-Min Sketch with explicit dimensions.</summary>
    public async ValueTask CountMinInitializeByDimensionsAsync(RespireKey key, long width, long depth, CancellationToken cancellationToken = default)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        using var result = await _commands.CountMinInitializeByDimensionsAsync(key, width, depth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Initializes Count-Min Sketch for the selected error rate and probability.</summary>
    public async ValueTask CountMinInitializeByProbabilityAsync(RespireKey key, double errorRate, double probability, CancellationToken cancellationToken = default)
    {
        ValidateErrorRate(errorRate, nameof(errorRate));
        ValidateErrorRate(probability, nameof(probability));
        using var result = await _commands.CountMinInitializeByProbabilityAsync(key, errorRate, probability, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Increments one or more Count-Min Sketch item estimates.</summary>
    public async ValueTask<long[]> CountMinIncrementAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
    {
        var args = BuildIncrementArguments(increments);
        using var result = await _commands.CountMinIncrementAsync(key, args, cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Queries estimated Count-Min Sketch frequencies.</summary>
    public async ValueTask<long[]> CountMinQueryAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.CountMinQueryAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Returns the raw CMS.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> CountMinInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.CountMinInfoAsync(key, cancellationToken);

    /// <summary>Merges Count-Min Sketches into a destination sketch.</summary>
    public async ValueTask CountMinMergeAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireCountMinMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKeys(sources);
        var optionArguments = (options ?? new()).ToArguments(sources.Count);
        var args = new RespireValue[checked(sources.Count + optionArguments.Length)];
        for (var index = 0; index < sources.Count; index++) args[index] = sources[index];
        optionArguments.CopyTo(args, sources.Count);
        using var result = await _commands.CountMinMergeAsync(destination, sources.Count, args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a Top-K sketch.</summary>
    public async ValueTask TopKReserveAsync(RespireKey key, long count, RespireTopKReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        using var result = await _commands.TopKReserveAsync(key, count, (options ?? new()).ToArguments(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds items to a Top-K sketch and returns items that were evicted.</summary>
    public async ValueTask<byte[]?[]> TopKAddAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.TopKAddAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadNullableBytes(result);
    }

    /// <summary>Increments item counts in a Top-K sketch.</summary>
    public async ValueTask<byte[]?[]> TopKIncrementAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
    {
        var args = BuildIncrementArguments(increments);
        using var result = await _commands.TopKIncrementAsync(key, args, cancellationToken).ConfigureAwait(false);
        return ReadNullableBytes(result);
    }

    /// <summary>Checks whether each item is currently in the Top-K list.</summary>
    public async ValueTask<bool[]> TopKQueryAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.TopKQueryAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Returns estimated Top-K counts for each item.</summary>
    public async ValueTask<long[]> TopKCountAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await _commands.TopKCountAsync(key, [.. items], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Returns the raw TOPK.LIST response: items, or item/count pairs when <paramref name="withCount"/> is true. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TopKListAsync(RespireKey key, bool withCount = false, CancellationToken cancellationToken = default)
        => _commands.TopKListAsync(key, withCount ? ["WITHCOUNT"] : [], cancellationToken);

    /// <summary>Returns the raw TOPK.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TopKInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.TopKInfoAsync(key, cancellationToken);

    /// <summary>Creates a t-digest sketch.</summary>
    public async ValueTask TDigestCreateAsync(RespireKey key, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.TDigestCreateAsync(key, (options ?? new()).ToArguments(allowOverride: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets an existing t-digest sketch.</summary>
    public async ValueTask TDigestResetAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.TDigestResetAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges t-digest sketches into a destination sketch.</summary>
    public async ValueTask TDigestMergeAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKeys(sources);
        var args = new List<RespireValue>(checked(sources.Count + 4));
        foreach (var source in sources) args.Add(source);
        args.AddRange((options ?? new()).ToArguments(allowOverride: true));
        using var result = await _commands.TDigestMergeAsync(destination, sources.Count, [.. args], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds observations to an existing t-digest sketch.</summary>
    public async ValueTask TDigestAddAsync(RespireKey key, IReadOnlyList<double> observations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) throw new ArgumentException("At least one observation is required.", nameof(observations));
        foreach (var observation in observations) if (!double.IsFinite(observation)) throw new ArgumentOutOfRangeException(nameof(observations));
        using var result = await _commands.TDigestAddAsync(key, observations.Select(static value => (RespireValue)value).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the minimum observation or NaN for an empty sketch.</summary>
    public async ValueTask<double> TDigestMinimumAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.TDigestMinimumAsync(key, cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Returns the maximum observation or NaN for an empty sketch.</summary>
    public async ValueTask<double> TDigestMaximumAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.TDigestMaximumAsync(key, cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Estimates values at quantile fractions.</summary>
    public ValueTask<double[]> TDigestQuantileAsync(RespireKey key, IReadOnlyList<double> quantiles, CancellationToken cancellationToken = default)
        => TDigestValuesAsync(key, quantiles, ValidateQuantiles, _commands.TDigestQuantileAsync, cancellationToken);

    /// <summary>Estimates cumulative distribution fractions for values.</summary>
    public ValueTask<double[]> TDigestCdfAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestValuesAsync(key, values, ValidateFiniteValues, _commands.TDigestCdfAsync, cancellationToken);

    /// <summary>Estimates ascending ranks for values.</summary>
    public ValueTask<long[]> TDigestRankAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestIntegerValuesAsync(key, values, ValidateFiniteValues, _commands.TDigestRankAsync, cancellationToken);

    /// <summary>Estimates descending ranks for values.</summary>
    public ValueTask<long[]> TDigestReverseRankAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestIntegerValuesAsync(key, values, ValidateFiniteValues, _commands.TDigestReverseRankAsync, cancellationToken);

    /// <summary>Estimates values at ascending ranks.</summary>
    public ValueTask<double[]> TDigestByRankAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => TDigestRankValuesAsync(key, ranks, _commands.TDigestByRankAsync, cancellationToken);

    /// <summary>Estimates values at descending ranks.</summary>
    public ValueTask<double[]> TDigestByReverseRankAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => TDigestRankValuesAsync(key, ranks, _commands.TDigestByReverseRankAsync, cancellationToken);

    /// <summary>Estimates the mean after trimming lower and upper fractions.</summary>
    public async ValueTask<double> TDigestTrimmedMeanAsync(RespireKey key, double lowCut, double highCut, CancellationToken cancellationToken = default)
    {
        ValidateQuantile(lowCut, nameof(lowCut)); ValidateQuantile(highCut, nameof(highCut));
        if (lowCut >= highCut) throw new ArgumentOutOfRangeException(nameof(lowCut));
        using var result = await _commands.TDigestTrimmedMeanAsync(key, lowCut, highCut, cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Returns the raw TDIGEST.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TDigestInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.TDigestInfoAsync(key, cancellationToken);

    private static async ValueTask<double[]> TDigestValuesAsync(RespireKey key, IReadOnlyList<double> values, Action<IReadOnlyList<double>> validate, Func<RespireKey, RespireValue[], CancellationToken, ValueTask<RespireResult>> execute, CancellationToken cancellationToken)
    {
        validate(values);
        using var result = await execute(key, values.Select(static value => (RespireValue)value).ToArray(), cancellationToken).ConfigureAwait(false);
        var output = new double[result.Count];
        for (var index = 0; index < output.Length; index++) output[index] = ReadDouble(result[index]);
        return output;
    }

    private static async ValueTask<long[]> TDigestIntegerValuesAsync(RespireKey key, IReadOnlyList<double> values, Action<IReadOnlyList<double>> validate, Func<RespireKey, RespireValue[], CancellationToken, ValueTask<RespireResult>> execute, CancellationToken cancellationToken)
    {
        validate(values);
        using var result = await execute(key, values.Select(static value => (RespireValue)value).ToArray(), cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    private static async ValueTask<double[]> TDigestRankValuesAsync(RespireKey key, IReadOnlyList<long> ranks, Func<RespireKey, RespireValue[], CancellationToken, ValueTask<RespireResult>> execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        if (ranks.Count == 0 || ranks.Any(static rank => rank < 0)) throw new ArgumentOutOfRangeException(nameof(ranks));
        using var result = await execute(key, ranks.Select(static rank => (RespireValue)rank).ToArray(), cancellationToken).ConfigureAwait(false);
        var output = new double[result.Count];
        for (var index = 0; index < output.Length; index++) output[index] = ReadDouble(result[index]);
        return output;
    }

    private static RespireProbabilisticDumpChunk ParseDumpChunk(RespireResult result)
    {
        if (result.Type != RespDataType.Array || result.Count != 2)
            throw new InvalidOperationException("Unexpected SCANDUMP reply: expected an iterator and a data chunk.");
        return new(result[0].AsInteger(), result[1].IsNull ? null : result[1].AsBytes());
    }

    private static RespireValue[] BuildIncrementArguments(IReadOnlyDictionary<RespireValue, long> increments)
    {
        ArgumentNullException.ThrowIfNull(increments);
        if (increments.Count == 0) throw new ArgumentException("At least one item is required.", nameof(increments));
        var args = new RespireValue[checked(increments.Count * 2)];
        var index = 0;
        foreach (var (item, amount) in increments)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(increments));
            ProbabilisticValueValidation.ThrowIfNull(item, nameof(increments));
            args[index++] = item;
            args[index++] = amount;
        }

        return args;
    }

    private static byte[]?[] ReadNullableBytes(RespireResult result)
    {
        var values = new byte[]?[result.Count];
        for (var index = 0; index < values.Length; index++) values[index] = result[index].IsNull ? null : result[index].AsBytes();
        return values;
    }

    /// <summary>
    /// Reads a t-digest double. RESP2 replies spell infinities as <c>inf</c>/<c>-inf</c>, which
    /// <see cref="double.Parse(string)"/> does not accept, so they are mapped explicitly.
    /// </summary>
    private static double ReadDouble(RespireResult result)
    {
        if (result.Type != RespDataType.Double)
        {
            var text = result.AsString();
            if (string.Equals(text, "inf", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "+inf", StringComparison.OrdinalIgnoreCase))
                return double.PositiveInfinity;
            if (string.Equals(text, "-inf", StringComparison.OrdinalIgnoreCase))
                return double.NegativeInfinity;
        }
        return result.AsDouble();
    }

    private static bool IsOk(RespireResult result) => result.Type == RespDataType.SimpleString ? result.AsString() == "OK" : result.AsBoolean();
    private static bool ReadBoolean(RespireResult result)
        => result.Type == RespDataType.Boolean ? result.AsBoolean() : result.AsInteger() == 1;
    private static bool[] ReadBooleans(RespireResult result) { var values = new bool[result.Count]; for (var i = 0; i < values.Length; i++) values[i] = ReadBoolean(result[i]); return values; }
    private static RespireCuckooInsertResult[] ReadCuckooInsertResults(RespireResult result)
    {
        var values = new RespireCuckooInsertResult[result.Count];
        for (var i = 0; i < values.Length; i++)
        {
            // RESP3 replies with booleans for success/presence and keeps -1 for a full filter.
            var item = result[i];
            values[i] = (item.Type == RespDataType.Boolean ? (item.AsBoolean() ? 1 : 0) : item.AsInteger()) switch
            {
                1 => RespireCuckooInsertResult.Inserted,
                0 => RespireCuckooInsertResult.AlreadyExists,
                -1 => RespireCuckooInsertResult.FilterFull,
                var other => throw new InvalidOperationException($"Unexpected Cuckoo insert reply {other}."),
            };
        }
        return values;
    }
    private static long[] ReadIntegers(RespireResult result) { var values = new long[result.Count]; for (var i = 0; i < values.Length; i++) values[i] = result[i].AsInteger(); return values; }

    private static void ValidateErrorRate(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0 || value >= 1) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateItems(IReadOnlyList<RespireValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one item is required.", nameof(items));
        foreach (var item in items) ProbabilisticValueValidation.ThrowIfNull(item, nameof(items));
    }

    private static void ValidateKeys(IReadOnlyList<RespireKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) throw new ArgumentException("At least one source key is required.", nameof(keys));
    }

    private static void ValidateQuantiles(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("At least one quantile is required.", nameof(values));
        foreach (var value in values) ValidateQuantile(value, nameof(values));
    }

    private static void ValidateFiniteValues(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("At least one value is required.", nameof(values));
        foreach (var value in values) if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(values));
    }

    private static void ValidateQuantile(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(parameterName);
    }
}
