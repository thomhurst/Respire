namespace Respire;

/// <summary>A SORT result slice. Offset and count must be nonnegative; omit the limit to return all entries.</summary>
public readonly record struct RespireSortLimit(long Offset, long Count);

/// <summary>Options for SORT/SORT_RO. External BY/GET patterns are relative to the client key prefix.</summary>
public sealed record RespireSortOptions
{
    /// <summary>Sort descending instead of ascending.</summary>
    public bool Descending { get; init; }
    /// <summary>Compare members or weights lexicographically rather than numerically.</summary>
    public bool Alpha { get; init; }
    /// <summary>Return a slice of the sorted entries.</summary>
    public RespireSortLimit? Limit { get; init; }
    /// <summary>External weight pattern, optionally using ->field. A pattern without * disables sorting.</summary>
    public RespireKey? By { get; init; }
    /// <summary>Ordered GET patterns. # returns the original member; missing external values become null.</summary>
    /// <remarks>Options equality compares the memory backing store and slice, not pattern contents. A with copy shares this memory.</remarks>
    public ReadOnlyMemory<RespireKey> Get { get; init; }
    /// <summary>Use SORT_RO (Redis 7+). Cannot be combined with SortStore.</summary>
    public bool ReadOnly { get; init; }
}
