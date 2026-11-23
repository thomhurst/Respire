using System.Collections.ObjectModel;

namespace Respire.Probabilistic;

/// <summary>A Bloom insertion batch contains one or more item errors and may have applied other writes.</summary>
public sealed class RespireBloomBatchException : RespireException
{
    internal RespireBloomBatchException(string message, bool?[] itemResults, Dictionary<int, string> itemErrors, int skippedCount)
        : base(message)
    {
        ItemResults = Array.AsReadOnly(itemResults);
        ItemErrors = new ReadOnlyDictionary<int, string>(itemErrors);
        SkippedCount = skippedCount;
    }

    /// <summary>
    /// Server replies in input order. True means inserted, false means reported as already present,
    /// and null means rejected. Inputs skipped at the end of the batch have no entry.
    /// </summary>
    public IReadOnlyList<bool?> ItemResults
    {
        get;
    }

    /// <summary>The Redis error text for each rejected item, keyed by zero-based input index.</summary>
    public IReadOnlyDictionary<int, string> ItemErrors
    {
        get;
    }

    /// <summary>The number of inputs Redis skipped at the end of the batch.</summary>
    public int SkippedCount
    {
        get;
    }
}
