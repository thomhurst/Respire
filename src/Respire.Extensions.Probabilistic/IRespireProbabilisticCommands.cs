namespace Respire.Extensions.Probabilistic;

/// <summary>Generated command bindings for Redis probabilistic data structures.</summary>
[RespireCommands]
public interface IRespireProbabilisticCommands
{
    // Bloom filter commands.
    /// <summary>Executes BF.RESERVE with typed arguments.</summary>
    [RespireCommand("BF.RESERVE")] ValueTask<RespireResult> BloomReserveAsync(RespireKey key, double errorRate, long capacity, RespireValue[] options, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.ADD with typed arguments.</summary>
    [RespireCommand("BF.ADD")] ValueTask<RespireResult> BloomAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.EXISTS with typed arguments.</summary>
    [RespireCommand("BF.EXISTS")] ValueTask<RespireResult> BloomExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.MADD with typed arguments.</summary>
    [RespireCommand("BF.MADD")] ValueTask<RespireResult> BloomMultiAddAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.MEXISTS with typed arguments.</summary>
    [RespireCommand("BF.MEXISTS")] ValueTask<RespireResult> BloomMultiExistsAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.INSERT with typed arguments.</summary>
    [RespireCommand("BF.INSERT")] ValueTask<RespireResult> BloomInsertAsync(RespireKey key, RespireValue[] optionsAndItems, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.INFO with typed arguments.</summary>
    [RespireCommand("BF.INFO")] ValueTask<RespireResult> BloomInfoAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.CARD with typed arguments.</summary>
    [RespireCommand("BF.CARD")] ValueTask<RespireResult> BloomCardinalityAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.SCANDUMP with typed arguments.</summary>
    [RespireCommand("BF.SCANDUMP")] ValueTask<RespireResult> BloomScanDumpAsync(RespireKey key, long iterator, CancellationToken cancellationToken = default);
    /// <summary>Executes BF.LOADCHUNK with typed arguments.</summary>
    [RespireCommand("BF.LOADCHUNK")] ValueTask<RespireResult> BloomLoadChunkAsync(RespireKey key, long iterator, RespireValue data, CancellationToken cancellationToken = default);

    // Cuckoo filter commands.
    /// <summary>Executes CF.RESERVE with typed arguments.</summary>
    [RespireCommand("CF.RESERVE")] ValueTask<RespireResult> CuckooReserveAsync(RespireKey key, long capacity, RespireValue[] options, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.ADD with typed arguments.</summary>
    [RespireCommand("CF.ADD")] ValueTask<RespireResult> CuckooAddAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.ADDNX with typed arguments.</summary>
    [RespireCommand("CF.ADDNX")] ValueTask<RespireResult> CuckooAddIfAbsentAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.INSERT with typed arguments.</summary>
    [RespireCommand("CF.INSERT")] ValueTask<RespireResult> CuckooInsertAsync(RespireKey key, RespireValue[] optionsAndItems, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.INSERTNX with typed arguments.</summary>
    [RespireCommand("CF.INSERTNX")] ValueTask<RespireResult> CuckooInsertIfAbsentAsync(RespireKey key, RespireValue[] optionsAndItems, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.DEL with typed arguments.</summary>
    [RespireCommand("CF.DEL")] ValueTask<RespireResult> CuckooDeleteAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.EXISTS with typed arguments.</summary>
    [RespireCommand("CF.EXISTS")] ValueTask<RespireResult> CuckooExistsAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.MEXISTS with typed arguments.</summary>
    [RespireCommand("CF.MEXISTS")] ValueTask<RespireResult> CuckooMultiExistsAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.COUNT with typed arguments.</summary>
    [RespireCommand("CF.COUNT")] ValueTask<RespireResult> CuckooCountAsync(RespireKey key, RespireValue item, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.INFO with typed arguments.</summary>
    [RespireCommand("CF.INFO")] ValueTask<RespireResult> CuckooInfoAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.SCANDUMP with typed arguments.</summary>
    [RespireCommand("CF.SCANDUMP")] ValueTask<RespireResult> CuckooScanDumpAsync(RespireKey key, long iterator, CancellationToken cancellationToken = default);
    /// <summary>Executes CF.LOADCHUNK with typed arguments.</summary>
    [RespireCommand("CF.LOADCHUNK")] ValueTask<RespireResult> CuckooLoadChunkAsync(RespireKey key, long iterator, RespireValue data, CancellationToken cancellationToken = default);

    // Count-Min Sketch commands.
    /// <summary>Executes CMS.INITBYDIM with typed arguments.</summary>
    [RespireCommand("CMS.INITBYDIM")] ValueTask<RespireResult> CountMinInitializeByDimensionsAsync(RespireKey key, long width, long depth, CancellationToken cancellationToken = default);
    /// <summary>Executes CMS.INITBYPROB with typed arguments.</summary>
    [RespireCommand("CMS.INITBYPROB")] ValueTask<RespireResult> CountMinInitializeByProbabilityAsync(RespireKey key, double errorRate, double probability, CancellationToken cancellationToken = default);
    /// <summary>Executes CMS.INCRBY with typed arguments.</summary>
    [RespireCommand("CMS.INCRBY")] ValueTask<RespireResult> CountMinIncrementAsync(RespireKey key, RespireValue[] itemIncrementPairs, CancellationToken cancellationToken = default);
    /// <summary>Executes CMS.QUERY with typed arguments.</summary>
    [RespireCommand("CMS.QUERY")] ValueTask<RespireResult> CountMinQueryAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes CMS.INFO with typed arguments.</summary>
    [RespireCommand("CMS.INFO")] ValueTask<RespireResult> CountMinInfoAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes CMS.MERGE with typed arguments.</summary>
    [RespireCommand("CMS.MERGE")] ValueTask<RespireResult> CountMinMergeAsync(RespireKey destination, long sourceCount, RespireValue[] sourcesAndOptions, CancellationToken cancellationToken = default);

    // Top-K commands.
    /// <summary>Executes TOPK.RESERVE with typed arguments.</summary>
    [RespireCommand("TOPK.RESERVE")] ValueTask<RespireResult> TopKReserveAsync(RespireKey key, long count, RespireValue[] options, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.ADD with typed arguments.</summary>
    [RespireCommand("TOPK.ADD")] ValueTask<RespireResult> TopKAddAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.INCRBY with typed arguments.</summary>
    [RespireCommand("TOPK.INCRBY")] ValueTask<RespireResult> TopKIncrementAsync(RespireKey key, RespireValue[] itemIncrementPairs, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.QUERY with typed arguments.</summary>
    [RespireCommand("TOPK.QUERY")] ValueTask<RespireResult> TopKQueryAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.COUNT with typed arguments.</summary>
    [RespireCommand("TOPK.COUNT")] ValueTask<RespireResult> TopKCountAsync(RespireKey key, RespireValue[] items, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.LIST with typed arguments.</summary>
    [RespireCommand("TOPK.LIST")] ValueTask<RespireResult> TopKListAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);
    /// <summary>Executes TOPK.INFO with typed arguments.</summary>
    [RespireCommand("TOPK.INFO")] ValueTask<RespireResult> TopKInfoAsync(RespireKey key, CancellationToken cancellationToken = default);

    // t-digest commands.
    /// <summary>Executes TDIGEST.CREATE with typed arguments.</summary>
    [RespireCommand("TDIGEST.CREATE")] ValueTask<RespireResult> TDigestCreateAsync(RespireKey key, RespireValue[] options, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.RESET with typed arguments.</summary>
    [RespireCommand("TDIGEST.RESET")] ValueTask<RespireResult> TDigestResetAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.MERGE with typed arguments.</summary>
    [RespireCommand("TDIGEST.MERGE")] ValueTask<RespireResult> TDigestMergeAsync(RespireKey destination, long sourceCount, RespireValue[] sourcesAndOptions, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.ADD with typed arguments.</summary>
    [RespireCommand("TDIGEST.ADD")] ValueTask<RespireResult> TDigestAddAsync(RespireKey key, RespireValue[] observations, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.MIN with typed arguments.</summary>
    [RespireCommand("TDIGEST.MIN")] ValueTask<RespireResult> TDigestMinimumAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.MAX with typed arguments.</summary>
    [RespireCommand("TDIGEST.MAX")] ValueTask<RespireResult> TDigestMaximumAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.QUANTILE with typed arguments.</summary>
    [RespireCommand("TDIGEST.QUANTILE")] ValueTask<RespireResult> TDigestQuantileAsync(RespireKey key, RespireValue[] quantiles, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.CDF with typed arguments.</summary>
    [RespireCommand("TDIGEST.CDF")] ValueTask<RespireResult> TDigestCdfAsync(RespireKey key, RespireValue[] values, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.RANK with typed arguments.</summary>
    [RespireCommand("TDIGEST.RANK")] ValueTask<RespireResult> TDigestRankAsync(RespireKey key, RespireValue[] values, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.REVRANK with typed arguments.</summary>
    [RespireCommand("TDIGEST.REVRANK")] ValueTask<RespireResult> TDigestReverseRankAsync(RespireKey key, RespireValue[] values, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.BYRANK with typed arguments.</summary>
    [RespireCommand("TDIGEST.BYRANK")] ValueTask<RespireResult> TDigestByRankAsync(RespireKey key, RespireValue[] ranks, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.BYREVRANK with typed arguments.</summary>
    [RespireCommand("TDIGEST.BYREVRANK")] ValueTask<RespireResult> TDigestByReverseRankAsync(RespireKey key, RespireValue[] ranks, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.TRIMMED_MEAN with typed arguments.</summary>
    [RespireCommand("TDIGEST.TRIMMED_MEAN")] ValueTask<RespireResult> TDigestTrimmedMeanAsync(RespireKey key, double lowCut, double highCut, CancellationToken cancellationToken = default);
    /// <summary>Executes TDIGEST.INFO with typed arguments.</summary>
    [RespireCommand("TDIGEST.INFO")] ValueTask<RespireResult> TDigestInfoAsync(RespireKey key, CancellationToken cancellationToken = default);
}
