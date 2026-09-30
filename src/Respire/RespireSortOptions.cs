namespace Respire;

/// <summary>A SORT result slice. Offset must be nonnegative; a negative count returns all entries from that offset.</summary>
public readonly record struct RespireSortLimit(long Offset, long Count);

/// <summary>Options for SORT/SORT_RO. External BY/GET patterns are relative to the client key prefix.</summary>
/// <remarks>Patterns are copied at initialization. Equality compares their ordered byte contents.</remarks>
public sealed record RespireSortOptions
{
    private readonly RespireKey? _by;
    private readonly ReadOnlyMemory<RespireKey> _get;
    /// <summary>Sort descending instead of ascending.</summary>
    public bool Descending { get; init; }
    /// <summary>Compare members or weights lexicographically rather than numerically.</summary>
    public bool Alpha { get; init; }
    /// <summary>Return a slice of the sorted entries.</summary>
    public RespireSortLimit? Limit { get; init; }
    /// <summary>External weight pattern, optionally using ->field. A pattern without * disables sorting.</summary>
    public RespireKey? By
    {
        get => _by;
        init => _by = value?.Snapshot();
    }
    /// <summary>Ordered GET patterns. # returns the original member; missing external values become null.</summary>
    /// <remarks>The collection and each binary pattern are copied. A with copy shares the owned snapshots.</remarks>
    public ReadOnlyMemory<RespireKey> Get
    {
        get => _get;
        init
        {
            if (value.IsEmpty)
            {
                _get = default;
                return;
            }
            var patterns = new RespireKey[value.Length];
            for (var i = 0; i < patterns.Length; i++) patterns[i] = value.Span[i].Snapshot();
            _get = patterns;
        }
    }
    /// <summary>Use SORT_RO (Redis 7+). Cannot be combined with SortStore.</summary>
    public bool ReadOnly { get; init; }

    /// <inheritdoc/>
    public bool Equals(RespireSortOptions? other)
        => ReferenceEquals(this, other) || other is not null
            && Descending == other.Descending && Alpha == other.Alpha && Limit == other.Limit
            && By == other.By && ReadOnly == other.ReadOnly && Get.Span.SequenceEqual(other.Get.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Descending);
        hash.Add(Alpha);
        hash.Add(Limit);
        hash.Add(By);
        hash.Add(ReadOnly);
        foreach (var pattern in Get.Span) hash.Add(pattern);
        return hash.ToHashCode();
    }
}
