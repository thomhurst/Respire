using Respire.Internal;
using Respire.Protocol;

namespace Respire.Probabilistic;

/// <summary>Typed operations for Redis Bloom, Cuckoo, Count-Min, Top-K, and t-digest structures.</summary>
public sealed class RespireProbabilisticClient
{
    private readonly RespireProbabilisticCommandsImplementation _commands;
    private readonly IRespireClient _client;

    /// <summary>Creates probabilistic operations over a caller-owned Respire client.</summary>
    public RespireProbabilisticClient(IRespireClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _commands = new RespireProbabilisticCommandsImplementation(client);
        _client = client;
    }

    /// <summary>Creates a Bloom filter.</summary>
    public ValueTask<bool> BloomReserveAsync(RespireKey key, double errorRate, long capacity, RespireBloomReserveOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.RESERVE", cancellationToken,
                (Owner: this, key, errorRate, capacity, options, cancellationToken),
                static s => s.Owner.BloomReserveUnhookedAsync(s.key, s.errorRate, s.capacity, s.options, s.cancellationToken))
            : BloomReserveUnhookedAsync(key, errorRate, capacity, options, cancellationToken);

    private async ValueTask<bool> BloomReserveUnhookedAsync(RespireKey key, double errorRate, long capacity, RespireBloomReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateErrorRate(errorRate, nameof(errorRate));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var result = await DispatchAsync(BloomReserveCommand, [key, errorRate, capacity, .. (options ?? new()).ToArguments()], cancellationToken).ConfigureAwait(false);
        return IsOk(result);
    }

    /// <summary>Adds one item to a Bloom filter.</summary>
    public ValueTask<bool> BloomAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.ADD", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.BloomAddUnhookedAsync(s.key, s.item, s.cancellationToken))
            : BloomAddUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> BloomAddUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(BloomAddCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Checks whether a Bloom filter may contain an item.</summary>
    public ValueTask<bool> BloomExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.EXISTS", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.BloomExistsUnhookedAsync(s.key, s.item, s.cancellationToken))
            : BloomExistsUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> BloomExistsUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(BloomExistsCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Adds multiple items to a Bloom filter.</summary>
    public ValueTask<bool[]> BloomMultiAddAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.MADD", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.BloomMultiAddUnhookedAsync(s.key, s.items, s.cancellationToken))
            : BloomMultiAddUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<bool[]> BloomMultiAddUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(BloomMultiAddCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Checks multiple items against a Bloom filter.</summary>
    public ValueTask<bool[]> BloomMultiExistsAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.MEXISTS", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.BloomMultiExistsUnhookedAsync(s.key, s.items, s.cancellationToken))
            : BloomMultiExistsUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<bool[]> BloomMultiExistsUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(BloomMultiExistsCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Creates a Bloom filter when needed and inserts multiple items.</summary>
    public ValueTask<bool[]> BloomInsertAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireBloomInsertOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.INSERT", cancellationToken,
                (Owner: this, key, items, options, cancellationToken),
                static s => s.Owner.BloomInsertUnhookedAsync(s.key, s.items, s.options, s.cancellationToken))
            : BloomInsertUnhookedAsync(key, items, options, cancellationToken);

    private async ValueTask<bool[]> BloomInsertUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireBloomInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        var arguments = (options ?? new()).ToArguments(items);
        using var result = await DispatchAsync(BloomInsertCommand, [key, .. arguments], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Returns the raw BF.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> BloomInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.BloomInfoAsync(key, cancellationToken);

    /// <summary>Returns Bloom filter cardinality.</summary>
    public ValueTask<long> BloomCardinalityAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.CARD", cancellationToken,
                (Owner: this, key, cancellationToken),
                static s => s.Owner.BloomCardinalityUnhookedAsync(s.key, s.cancellationToken))
            : BloomCardinalityUnhookedAsync(key, cancellationToken);

    private async ValueTask<long> BloomCardinalityUnhookedAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(BloomCardinalityCommand, [key], cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Reads the next Bloom filter dump chunk.</summary>
    public ValueTask<RespireProbabilisticDumpChunk> BloomScanDumpAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.SCANDUMP", cancellationToken,
                (Owner: this, key, iterator, cancellationToken),
                static s => s.Owner.BloomScanDumpUnhookedAsync(s.key, s.iterator, s.cancellationToken))
            : BloomScanDumpUnhookedAsync(key, iterator, cancellationToken);

    private async ValueTask<RespireProbabilisticDumpChunk> BloomScanDumpUnhookedAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(BloomScanDumpCommand, [key, iterator], cancellationToken).ConfigureAwait(false);
        return ParseDumpChunk(result);
    }

    /// <summary>Loads a Bloom filter dump chunk.</summary>
    public ValueTask BloomLoadChunkAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "BF.LOADCHUNK", cancellationToken,
                (Owner: this, key, chunk, cancellationToken),
                static s => s.Owner.BloomLoadChunkUnhookedAsync(s.key, s.chunk, s.cancellationToken))
            : BloomLoadChunkUnhookedAsync(key, chunk, cancellationToken);

    private async ValueTask BloomLoadChunkUnhookedAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(chunk.Data);
        using var result = await DispatchAsync(BloomLoadChunkCommand, [key, chunk.Iterator, chunk.Data], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a Cuckoo filter.</summary>
    public ValueTask<bool> CuckooReserveAsync(RespireKey key, long capacity, RespireCuckooReserveOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.RESERVE", cancellationToken,
                (Owner: this, key, capacity, options, cancellationToken),
                static s => s.Owner.CuckooReserveUnhookedAsync(s.key, s.capacity, s.options, s.cancellationToken))
            : CuckooReserveUnhookedAsync(key, capacity, options, cancellationToken);

    private async ValueTask<bool> CuckooReserveUnhookedAsync(RespireKey key, long capacity, RespireCuckooReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        using var result = await DispatchAsync(CuckooReserveCommand, [key, capacity, .. (options ?? new()).ToArguments()], cancellationToken).ConfigureAwait(false);
        return IsOk(result);
    }

    /// <summary>Adds an item to a Cuckoo filter.</summary>
    public ValueTask<bool> CuckooAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.ADD", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.CuckooAddUnhookedAsync(s.key, s.item, s.cancellationToken))
            : CuckooAddUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> CuckooAddUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(CuckooAddCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Adds an item only when it is not already present.</summary>
    public ValueTask<bool> CuckooAddIfAbsentAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.ADDNX", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.CuckooAddIfAbsentUnhookedAsync(s.key, s.item, s.cancellationToken))
            : CuckooAddIfAbsentUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> CuckooAddIfAbsentUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(CuckooAddIfAbsentCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return ReadBoolean(result);
    }

    /// <summary>Creates a Cuckoo filter when needed and inserts multiple items, reporting per item whether it was inserted or the filter was full.</summary>
    public ValueTask<RespireCuckooInsertResult[]> CuckooInsertAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.INSERT", cancellationToken,
                (Owner: this, key, items, options, cancellationToken),
                static s => s.Owner.CuckooInsertUnhookedAsync(s.key, s.items, s.options, s.cancellationToken))
            : CuckooInsertUnhookedAsync(key, items, options, cancellationToken);

    private async ValueTask<RespireCuckooInsertResult[]> CuckooInsertUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(CuckooInsertCommand, [key, .. (options ?? new()).ToArguments(items)], cancellationToken).ConfigureAwait(false);
        return ReadCuckooInsertResults(result);
    }

    /// <summary>Creates a Cuckoo filter when needed and inserts only absent items, reporting per item whether it was inserted, already present, or the filter was full.</summary>
    public ValueTask<RespireCuckooInsertResult[]> CuckooInsertIfAbsentAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.INSERTNX", cancellationToken,
                (Owner: this, key, items, options, cancellationToken),
                static s => s.Owner.CuckooInsertIfAbsentUnhookedAsync(s.key, s.items, s.options, s.cancellationToken))
            : CuckooInsertIfAbsentUnhookedAsync(key, items, options, cancellationToken);

    private async ValueTask<RespireCuckooInsertResult[]> CuckooInsertIfAbsentUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, RespireCuckooInsertOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(CuckooInsertIfAbsentCommand, [key, .. (options ?? new()).ToArguments(items)], cancellationToken).ConfigureAwait(false);
        return ReadCuckooInsertResults(result);
    }

    /// <summary>Deletes one item from a Cuckoo filter.</summary>
    public ValueTask<bool> CuckooDeleteAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.DEL", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.CuckooDeleteUnhookedAsync(s.key, s.item, s.cancellationToken))
            : CuckooDeleteUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> CuckooDeleteUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(CuckooDeleteCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Checks whether a Cuckoo filter may contain an item.</summary>
    public ValueTask<bool> CuckooExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.EXISTS", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.CuckooExistsUnhookedAsync(s.key, s.item, s.cancellationToken))
            : CuckooExistsUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<bool> CuckooExistsUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(CuckooExistsCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return result.AsBoolean();
    }

    /// <summary>Checks whether a Cuckoo filter may contain each item.</summary>
    public ValueTask<bool[]> CuckooMultiExistsAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.MEXISTS", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.CuckooMultiExistsUnhookedAsync(s.key, s.items, s.cancellationToken))
            : CuckooMultiExistsUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<bool[]> CuckooMultiExistsUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(CuckooMultiExistsCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Counts copies of one item in a Cuckoo filter.</summary>
    public ValueTask<long> CuckooCountAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.COUNT", cancellationToken,
                (Owner: this, key, item, cancellationToken),
                static s => s.Owner.CuckooCountUnhookedAsync(s.key, s.item, s.cancellationToken))
            : CuckooCountUnhookedAsync(key, item, cancellationToken);

    private async ValueTask<long> CuckooCountUnhookedAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default)
    {
        ProbabilisticValueValidation.ThrowIfNull(item, nameof(item));
        using var result = await DispatchAsync(CuckooCountCommand, [key, item], cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Returns the raw CF.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> CuckooInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.CuckooInfoAsync(key, cancellationToken);

    /// <summary>Reads the next Cuckoo filter dump chunk.</summary>
    public ValueTask<RespireProbabilisticDumpChunk> CuckooScanDumpAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.SCANDUMP", cancellationToken,
                (Owner: this, key, iterator, cancellationToken),
                static s => s.Owner.CuckooScanDumpUnhookedAsync(s.key, s.iterator, s.cancellationToken))
            : CuckooScanDumpUnhookedAsync(key, iterator, cancellationToken);

    private async ValueTask<RespireProbabilisticDumpChunk> CuckooScanDumpUnhookedAsync(RespireKey key, long iterator = 0, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(CuckooScanDumpCommand, [key, iterator], cancellationToken).ConfigureAwait(false);
        return ParseDumpChunk(result);
    }

    /// <summary>Loads a Cuckoo filter dump chunk.</summary>
    public ValueTask CuckooLoadChunkAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CF.LOADCHUNK", cancellationToken,
                (Owner: this, key, chunk, cancellationToken),
                static s => s.Owner.CuckooLoadChunkUnhookedAsync(s.key, s.chunk, s.cancellationToken))
            : CuckooLoadChunkUnhookedAsync(key, chunk, cancellationToken);

    private async ValueTask CuckooLoadChunkUnhookedAsync(RespireKey key, RespireProbabilisticDumpChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(chunk.Data);
        using var result = await DispatchAsync(CuckooLoadChunkCommand, [key, chunk.Iterator, chunk.Data], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Initializes Count-Min Sketch with explicit dimensions.</summary>
    public ValueTask CountMinInitializeByDimensionsAsync(RespireKey key, long width, long depth, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CMS.INITBYDIM", cancellationToken,
                (Owner: this, key, width, depth, cancellationToken),
                static s => s.Owner.CountMinInitializeByDimensionsUnhookedAsync(s.key, s.width, s.depth, s.cancellationToken))
            : CountMinInitializeByDimensionsUnhookedAsync(key, width, depth, cancellationToken);

    private async ValueTask CountMinInitializeByDimensionsUnhookedAsync(RespireKey key, long width, long depth, CancellationToken cancellationToken = default)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        using var result = await DispatchAsync(CountMinInitializeByDimensionsCommand, [key, width, depth], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Initializes Count-Min Sketch for the selected error rate and probability.</summary>
    public ValueTask CountMinInitializeByProbabilityAsync(RespireKey key, double errorRate, double probability, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CMS.INITBYPROB", cancellationToken,
                (Owner: this, key, errorRate, probability, cancellationToken),
                static s => s.Owner.CountMinInitializeByProbabilityUnhookedAsync(s.key, s.errorRate, s.probability, s.cancellationToken))
            : CountMinInitializeByProbabilityUnhookedAsync(key, errorRate, probability, cancellationToken);

    private async ValueTask CountMinInitializeByProbabilityUnhookedAsync(RespireKey key, double errorRate, double probability, CancellationToken cancellationToken = default)
    {
        ValidateErrorRate(errorRate, nameof(errorRate));
        ValidateErrorRate(probability, nameof(probability));
        using var result = await DispatchAsync(CountMinInitializeByProbabilityCommand, [key, errorRate, probability], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Increments one or more Count-Min Sketch item estimates.</summary>
    public ValueTask<long[]> CountMinIncrementAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CMS.INCRBY", cancellationToken,
                (Owner: this, key, increments, cancellationToken),
                static s => s.Owner.CountMinIncrementUnhookedAsync(s.key, s.increments, s.cancellationToken))
            : CountMinIncrementUnhookedAsync(key, increments, cancellationToken);

    private async ValueTask<long[]> CountMinIncrementUnhookedAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
    {
        var args = BuildIncrementArguments(increments);
        using var result = await DispatchAsync(CountMinIncrementCommand, [key, .. args], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Queries estimated Count-Min Sketch frequencies.</summary>
    public ValueTask<long[]> CountMinQueryAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CMS.QUERY", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.CountMinQueryUnhookedAsync(s.key, s.items, s.cancellationToken))
            : CountMinQueryUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<long[]> CountMinQueryUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(CountMinQueryCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Returns the raw CMS.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> CountMinInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.CountMinInfoAsync(key, cancellationToken);

    /// <summary>Merges Count-Min Sketches into a destination sketch.</summary>
    public ValueTask CountMinMergeAsync(RespireKey destination, RespireKey source, RespireCountMinMergeOptions? options = null, CancellationToken cancellationToken = default)
        => CountMinMergeAsync(destination, [source], options, cancellationToken);

    /// <summary>Merges Count-Min sketches into the destination.</summary>
    public ValueTask CountMinMergeAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireCountMinMergeOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "CMS.MERGE", cancellationToken,
                (Owner: this, destination, sources, options, cancellationToken),
                static s => s.Owner.CountMinMergeUnhookedAsync(s.destination, s.sources, s.options, s.cancellationToken))
            : CountMinMergeUnhookedAsync(destination, sources, options, cancellationToken);

    private async ValueTask CountMinMergeUnhookedAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireCountMinMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKeys(sources);
        var optionArguments = (options ?? new()).ToArguments(sources.Count);
        var args = new RespireValue[checked(sources.Count + optionArguments.Length)];
        for (var index = 0; index < sources.Count; index++) args[index] = sources[index];
        optionArguments.CopyTo(args, sources.Count);
        using var result = await DispatchAsync(CountMinMergeCommand, [destination, sources.Count, .. args], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a Top-K sketch.</summary>
    public ValueTask TopKReserveAsync(RespireKey key, long count, RespireTopKReserveOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TOPK.RESERVE", cancellationToken,
                (Owner: this, key, count, options, cancellationToken),
                static s => s.Owner.TopKReserveUnhookedAsync(s.key, s.count, s.options, s.cancellationToken))
            : TopKReserveUnhookedAsync(key, count, options, cancellationToken);

    private async ValueTask TopKReserveUnhookedAsync(RespireKey key, long count, RespireTopKReserveOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        using var result = await DispatchAsync(TopKReserveCommand, [key, count, .. (options ?? new()).ToArguments()], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds items to a Top-K sketch and returns items that were evicted.</summary>
    public ValueTask<byte[]?[]> TopKAddAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TOPK.ADD", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.TopKAddUnhookedAsync(s.key, s.items, s.cancellationToken))
            : TopKAddUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<byte[]?[]> TopKAddUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(TopKAddCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadNullableBytes(result);
    }

    /// <summary>Increments item counts in a Top-K sketch.</summary>
    public ValueTask<byte[]?[]> TopKIncrementAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TOPK.INCRBY", cancellationToken,
                (Owner: this, key, increments, cancellationToken),
                static s => s.Owner.TopKIncrementUnhookedAsync(s.key, s.increments, s.cancellationToken))
            : TopKIncrementUnhookedAsync(key, increments, cancellationToken);

    private async ValueTask<byte[]?[]> TopKIncrementUnhookedAsync(RespireKey key, IReadOnlyDictionary<RespireValue, long> increments, CancellationToken cancellationToken = default)
    {
        var args = BuildIncrementArguments(increments);
        using var result = await DispatchAsync(TopKIncrementCommand, [key, .. args], cancellationToken).ConfigureAwait(false);
        return ReadNullableBytes(result);
    }

    /// <summary>Checks whether each item is currently in the Top-K list.</summary>
    public ValueTask<bool[]> TopKQueryAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TOPK.QUERY", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.TopKQueryUnhookedAsync(s.key, s.items, s.cancellationToken))
            : TopKQueryUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<bool[]> TopKQueryUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(TopKQueryCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadBooleans(result);
    }

    /// <summary>Returns estimated Top-K counts for each item.</summary>
    public ValueTask<long[]> TopKCountAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TOPK.COUNT", cancellationToken,
                (Owner: this, key, items, cancellationToken),
                static s => s.Owner.TopKCountUnhookedAsync(s.key, s.items, s.cancellationToken))
            : TopKCountUnhookedAsync(key, items, cancellationToken);

    private async ValueTask<long[]> TopKCountUnhookedAsync(RespireKey key, IReadOnlyList<RespireValue> items, CancellationToken cancellationToken = default)
    {
        ValidateItems(items);
        using var result = await DispatchAsync(TopKCountCommand, [key, .. items], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    /// <summary>Returns the raw TOPK.LIST response: items, or item/count pairs when <paramref name="withCount"/> is true. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TopKListAsync(RespireKey key, bool withCount = false, CancellationToken cancellationToken = default)
        => _commands.TopKListAsync(key, withCount ? ["WITHCOUNT"] : [], cancellationToken);

    /// <summary>Returns the raw TOPK.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TopKInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.TopKInfoAsync(key, cancellationToken);

    /// <summary>Creates a t-digest sketch.</summary>
    public ValueTask TDigestCreateAsync(RespireKey key, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.CREATE", cancellationToken,
                (Owner: this, key, options, cancellationToken),
                static s => s.Owner.TDigestCreateUnhookedAsync(s.key, s.options, s.cancellationToken))
            : TDigestCreateUnhookedAsync(key, options, cancellationToken);

    private async ValueTask TDigestCreateUnhookedAsync(RespireKey key, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(TDigestCreateCommand, [key, .. (options ?? new()).ToArguments(allowOverride: false)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets an existing t-digest sketch.</summary>
    public ValueTask TDigestResetAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.RESET", cancellationToken,
                (Owner: this, key, cancellationToken),
                static s => s.Owner.TDigestResetUnhookedAsync(s.key, s.cancellationToken))
            : TDigestResetUnhookedAsync(key, cancellationToken);

    private async ValueTask TDigestResetUnhookedAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(TDigestResetCommand, [key], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges t-digest sketches into a destination sketch.</summary>
    public ValueTask TDigestMergeAsync(RespireKey destination, RespireKey source, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
        => TDigestMergeAsync(destination, [source], options, cancellationToken);

    /// <summary>Merges t-digests into the destination.</summary>
    public ValueTask TDigestMergeAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.MERGE", cancellationToken,
                (Owner: this, destination, sources, options, cancellationToken),
                static s => s.Owner.TDigestMergeUnhookedAsync(s.destination, s.sources, s.options, s.cancellationToken))
            : TDigestMergeUnhookedAsync(destination, sources, options, cancellationToken);

    private async ValueTask TDigestMergeUnhookedAsync(RespireKey destination, IReadOnlyList<RespireKey> sources, RespireTDigestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKeys(sources);
        var args = new List<RespireValue>(checked(sources.Count + 4));
        foreach (var source in sources) args.Add(source);
        args.AddRange((options ?? new()).ToArguments(allowOverride: true));
        using var result = await DispatchAsync(TDigestMergeCommand, [destination, sources.Count, .. args], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds observations to an existing t-digest sketch.</summary>
    public ValueTask TDigestAddAsync(RespireKey key, IReadOnlyList<double> observations, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.ADD", cancellationToken,
                (Owner: this, key, observations, cancellationToken),
                static s => s.Owner.TDigestAddUnhookedAsync(s.key, s.observations, s.cancellationToken))
            : TDigestAddUnhookedAsync(key, observations, cancellationToken);

    private async ValueTask TDigestAddUnhookedAsync(RespireKey key, IReadOnlyList<double> observations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0) throw new ArgumentException("At least one observation is required.", nameof(observations));
        foreach (var observation in observations) if (!double.IsFinite(observation)) throw new ArgumentOutOfRangeException(nameof(observations));
        using var result = await DispatchAsync(TDigestAddCommand, [key, .. observations.Select(static value => (RespireValue)value)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the minimum observation or NaN for an empty sketch.</summary>
    public ValueTask<double> TDigestMinimumAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.MIN", cancellationToken,
                (Owner: this, key, cancellationToken),
                static s => s.Owner.TDigestMinimumUnhookedAsync(s.key, s.cancellationToken))
            : TDigestMinimumUnhookedAsync(key, cancellationToken);

    private async ValueTask<double> TDigestMinimumUnhookedAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(TDigestMinimumCommand, [key], cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Returns the maximum observation or NaN for an empty sketch.</summary>
    public ValueTask<double> TDigestMaximumAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.MAX", cancellationToken,
                (Owner: this, key, cancellationToken),
                static s => s.Owner.TDigestMaximumUnhookedAsync(s.key, s.cancellationToken))
            : TDigestMaximumUnhookedAsync(key, cancellationToken);

    private async ValueTask<double> TDigestMaximumUnhookedAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await DispatchAsync(TDigestMaximumCommand, [key], cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Estimates values at quantile fractions.</summary>
    public ValueTask<double[]> TDigestQuantileAsync(RespireKey key, IReadOnlyList<double> quantiles, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.QUANTILE", cancellationToken,
                (Owner: this, key, quantiles, cancellationToken),
                static s => s.Owner.TDigestQuantileUnhookedAsync(s.key, s.quantiles, s.cancellationToken))
            : TDigestQuantileUnhookedAsync(key, quantiles, cancellationToken);

    private ValueTask<double[]> TDigestQuantileUnhookedAsync(RespireKey key, IReadOnlyList<double> quantiles, CancellationToken cancellationToken = default)
        => TDigestValuesAsync(key, quantiles, ValidateQuantiles, TDigestQuantileCommand, cancellationToken);

    /// <summary>Estimates cumulative distribution fractions for values.</summary>
    public ValueTask<double[]> TDigestCdfAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.CDF", cancellationToken,
                (Owner: this, key, values, cancellationToken),
                static s => s.Owner.TDigestCdfUnhookedAsync(s.key, s.values, s.cancellationToken))
            : TDigestCdfUnhookedAsync(key, values, cancellationToken);

    private ValueTask<double[]> TDigestCdfUnhookedAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestValuesAsync(key, values, ValidateFiniteValues, TDigestCdfCommand, cancellationToken);

    /// <summary>Estimates ascending ranks for values.</summary>
    public ValueTask<long[]> TDigestRankAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.RANK", cancellationToken,
                (Owner: this, key, values, cancellationToken),
                static s => s.Owner.TDigestRankUnhookedAsync(s.key, s.values, s.cancellationToken))
            : TDigestRankUnhookedAsync(key, values, cancellationToken);

    private ValueTask<long[]> TDigestRankUnhookedAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestIntegerValuesAsync(key, values, ValidateFiniteValues, TDigestRankCommand, cancellationToken);

    /// <summary>Estimates descending ranks for values.</summary>
    public ValueTask<long[]> TDigestReverseRankAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.REVRANK", cancellationToken,
                (Owner: this, key, values, cancellationToken),
                static s => s.Owner.TDigestReverseRankUnhookedAsync(s.key, s.values, s.cancellationToken))
            : TDigestReverseRankUnhookedAsync(key, values, cancellationToken);

    private ValueTask<long[]> TDigestReverseRankUnhookedAsync(RespireKey key, IReadOnlyList<double> values, CancellationToken cancellationToken = default)
        => TDigestIntegerValuesAsync(key, values, ValidateFiniteValues, TDigestReverseRankCommand, cancellationToken);

    /// <summary>Estimates values at ascending ranks.</summary>
    public ValueTask<double[]> TDigestByRankAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.BYRANK", cancellationToken,
                (Owner: this, key, ranks, cancellationToken),
                static s => s.Owner.TDigestByRankUnhookedAsync(s.key, s.ranks, s.cancellationToken))
            : TDigestByRankUnhookedAsync(key, ranks, cancellationToken);

    private ValueTask<double[]> TDigestByRankUnhookedAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => TDigestRankValuesAsync(key, ranks, TDigestByRankCommand, cancellationToken);

    /// <summary>Estimates values at descending ranks.</summary>
    public ValueTask<double[]> TDigestByReverseRankAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.BYREVRANK", cancellationToken,
                (Owner: this, key, ranks, cancellationToken),
                static s => s.Owner.TDigestByReverseRankUnhookedAsync(s.key, s.ranks, s.cancellationToken))
            : TDigestByReverseRankUnhookedAsync(key, ranks, cancellationToken);

    private ValueTask<double[]> TDigestByReverseRankUnhookedAsync(RespireKey key, IReadOnlyList<long> ranks, CancellationToken cancellationToken = default)
        => TDigestRankValuesAsync(key, ranks, TDigestByReverseRankCommand, cancellationToken);

    /// <summary>Estimates the mean after trimming lower and upper fractions.</summary>
    public ValueTask<double> TDigestTrimmedMeanAsync(RespireKey key, double lowCut, double highCut, CancellationToken cancellationToken = default)
        => _client is RespireClient client && client.Core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "TDIGEST.TRIMMED_MEAN", cancellationToken,
                (Owner: this, key, lowCut, highCut, cancellationToken),
                static s => s.Owner.TDigestTrimmedMeanUnhookedAsync(s.key, s.lowCut, s.highCut, s.cancellationToken))
            : TDigestTrimmedMeanUnhookedAsync(key, lowCut, highCut, cancellationToken);

    private async ValueTask<double> TDigestTrimmedMeanUnhookedAsync(RespireKey key, double lowCut, double highCut, CancellationToken cancellationToken = default)
    {
        ValidateQuantile(lowCut, nameof(lowCut)); ValidateQuantile(highCut, nameof(highCut));
        if (lowCut >= highCut) throw new ArgumentOutOfRangeException(nameof(lowCut));
        using var result = await DispatchAsync(TDigestTrimmedMeanCommand, [key, lowCut, highCut], cancellationToken).ConfigureAwait(false);
        return ReadDouble(result);
    }

    /// <summary>Returns the raw TDIGEST.INFO response. Dispose the result when done.</summary>
    public ValueTask<RespireResult> TDigestInfoAsync(RespireKey key, CancellationToken cancellationToken = default)
        => _commands.TDigestInfoAsync(key, cancellationToken);

    private async ValueTask<double[]> TDigestValuesAsync(RespireKey key, IReadOnlyList<double> values, Action<IReadOnlyList<double>> validate, RespireCommand command, CancellationToken cancellationToken)
    {
        validate(values);
        using var result = await DispatchAsync(command, [key, .. values.Select(static value => (RespireValue)value)], cancellationToken).ConfigureAwait(false);
        var output = new double[result.Count];
        for (var index = 0; index < output.Length; index++) output[index] = ReadDouble(result[index]);
        return output;
    }

    private async ValueTask<long[]> TDigestIntegerValuesAsync(RespireKey key, IReadOnlyList<double> values, Action<IReadOnlyList<double>> validate, RespireCommand command, CancellationToken cancellationToken)
    {
        validate(values);
        using var result = await DispatchAsync(command, [key, .. values.Select(static value => (RespireValue)value)], cancellationToken).ConfigureAwait(false);
        return ReadIntegers(result);
    }

    private async ValueTask<double[]> TDigestRankValuesAsync(RespireKey key, IReadOnlyList<long> ranks, RespireCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        if (ranks.Count == 0 || ranks.Any(static rank => rank < 0)) throw new ArgumentOutOfRangeException(nameof(ranks));
        using var result = await DispatchAsync(command, [key, .. ranks.Select(static rank => (RespireValue)rank)], cancellationToken).ConfigureAwait(false);
        var output = new double[result.Count];
        for (var index = 0; index < output.Length; index++) output[index] = ReadDouble(result[index]);
        return output;
    }

    // Match the generated bindings' command names and mutation metadata. Raw Info and List
    // transfers continue to use _commands; only the typed owners borrow unhooked dispatch.
    private static readonly RespireCommand BloomReserveCommand = RespireCommand.Create("BF.RESERVE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand BloomAddCommand = RespireCommand.Create("BF.ADD", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand BloomExistsCommand = RespireCommand.Create("BF.EXISTS", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand BloomMultiAddCommand = RespireCommand.Create("BF.MADD", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand BloomMultiExistsCommand = RespireCommand.Create("BF.MEXISTS", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand BloomInsertCommand = RespireCommand.Create("BF.INSERT", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand BloomCardinalityCommand = RespireCommand.Create("BF.CARD", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand BloomScanDumpCommand = RespireCommand.Create("BF.SCANDUMP", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand BloomLoadChunkCommand = RespireCommand.Create("BF.LOADCHUNK", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooReserveCommand = RespireCommand.Create("CF.RESERVE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooAddCommand = RespireCommand.Create("CF.ADD", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooAddIfAbsentCommand = RespireCommand.Create("CF.ADDNX", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooInsertCommand = RespireCommand.Create("CF.INSERT", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooInsertIfAbsentCommand = RespireCommand.Create("CF.INSERTNX", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooDeleteCommand = RespireCommand.Create("CF.DEL", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CuckooExistsCommand = RespireCommand.Create("CF.EXISTS", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand CuckooMultiExistsCommand = RespireCommand.Create("CF.MEXISTS", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand CuckooCountCommand = RespireCommand.Create("CF.COUNT", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand CuckooScanDumpCommand = RespireCommand.Create("CF.SCANDUMP", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand CuckooLoadChunkCommand = RespireCommand.Create("CF.LOADCHUNK", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CountMinInitializeByDimensionsCommand = RespireCommand.Create("CMS.INITBYDIM", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CountMinInitializeByProbabilityCommand = RespireCommand.Create("CMS.INITBYPROB", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CountMinIncrementCommand = RespireCommand.Create("CMS.INCRBY", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand CountMinQueryCommand = RespireCommand.Create("CMS.QUERY", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand CountMinMergeCommand = RespireCommand.Create("CMS.MERGE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TopKReserveCommand = RespireCommand.Create("TOPK.RESERVE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TopKAddCommand = RespireCommand.Create("TOPK.ADD", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TopKIncrementCommand = RespireCommand.Create("TOPK.INCRBY", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TopKQueryCommand = RespireCommand.Create("TOPK.QUERY", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TopKCountCommand = RespireCommand.Create("TOPK.COUNT", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestCreateCommand = RespireCommand.Create("TDIGEST.CREATE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TDigestResetCommand = RespireCommand.Create("TDIGEST.RESET", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TDigestMergeCommand = RespireCommand.Create("TDIGEST.MERGE", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TDigestAddCommand = RespireCommand.Create("TDIGEST.ADD", RespireCacheMutation.SingleKey);
    private static readonly RespireCommand TDigestMinimumCommand = RespireCommand.Create("TDIGEST.MIN", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestMaximumCommand = RespireCommand.Create("TDIGEST.MAX", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestQuantileCommand = RespireCommand.Create("TDIGEST.QUANTILE", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestCdfCommand = RespireCommand.Create("TDIGEST.CDF", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestRankCommand = RespireCommand.Create("TDIGEST.RANK", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestReverseRankCommand = RespireCommand.Create("TDIGEST.REVRANK", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestByRankCommand = RespireCommand.Create("TDIGEST.BYRANK", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestByReverseRankCommand = RespireCommand.Create("TDIGEST.BYREVRANK", RespireCacheMutation.ReadOnly);
    private static readonly RespireCommand TDigestTrimmedMeanCommand = RespireCommand.Create("TDIGEST.TRIMMED_MEAN", RespireCacheMutation.ReadOnly);

    private ValueTask<RespireResult> DispatchAsync(RespireCommand command, RespireValue[] arguments, CancellationToken token)
    {
        if (token.IsCancellationRequested) return ValueTask.FromCanceled<RespireResult>(token);
        return _client is RespireClient client && client.Core.Options.CommandHook is not null
            ? client.ExecuteOwnedCommandUnhookedAsync(command, arguments, RespireCommandFlags.None, token)
            : _client.ExecuteAsync(command, arguments, cancellationToken: token);
    }

    private static RespireProbabilisticDumpChunk ParseDumpChunk(RespireResult result)
    {
        if (result.Type != RespireDataType.Array || result.Count != 2)
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
        if (result.Type != RespireDataType.Double)
        {
            var text = result.AsString();
            if (string.Equals(text, "inf", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "+inf", StringComparison.OrdinalIgnoreCase))
                return double.PositiveInfinity;
            if (string.Equals(text, "-inf", StringComparison.OrdinalIgnoreCase))
                return double.NegativeInfinity;
        }
        return result.AsDouble();
    }

    private static bool IsOk(RespireResult result) => result.Type == RespireDataType.SimpleString ? result.AsString() == "OK" : result.AsBoolean();
    private static bool ReadBoolean(RespireResult result)
        => result.Type == RespireDataType.Boolean ? result.AsBoolean() : result.AsInteger() == 1;
    private static bool[] ReadBooleans(RespireResult result) { var values = new bool[result.Count]; for (var i = 0; i < values.Length; i++) values[i] = ReadBoolean(result[i]); return values; }
    private static RespireCuckooInsertResult[] ReadCuckooInsertResults(RespireResult result)
    {
        var values = new RespireCuckooInsertResult[result.Count];
        for (var i = 0; i < values.Length; i++)
        {
            // RESP3 replies with booleans for success/presence and keeps -1 for a full filter.
            var item = result[i];
            values[i] = (item.Type == RespireDataType.Boolean ? (item.AsBoolean() ? 1 : 0) : item.AsInteger()) switch
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
