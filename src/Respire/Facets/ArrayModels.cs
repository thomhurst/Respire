using System.Globalization;

namespace Respire;

/// <summary>An index and its input value for ARMSET.</summary>
public readonly record struct RespireArrayItem(ulong Index, RespireValue Value);

/// <summary>An inclusive array range. Either endpoint may be larger.</summary>
public readonly record struct RespireArrayRange(ulong Start, ulong End);

/// <summary>An existing array slot returned by ARSCAN or ARGREP WITHVALUES.</summary>
public readonly record struct RespireArrayEntry<T>(ulong Index, T? Value);

/// <summary>A numeric or logical ARGREP boundary.</summary>
public readonly record struct RespireArrayBound
{
    private readonly byte _kind;
    private readonly ulong _index;
    private RespireArrayBound(byte kind, ulong index = 0) { _kind = kind; _index = index; }
    /// <summary>The first logical position, written as '-'.</summary>
    public static RespireArrayBound First => new(1);
    /// <summary>The last logical position, written as '+'.</summary>
    public static RespireArrayBound Last => new(2);
    /// <summary>A numeric position; UInt64.MaxValue is reserved by Redis.</summary>
    public static implicit operator RespireArrayBound(ulong index) => new(0, index);
    internal RespireValue Argument
    {
        get
        {
            if (_kind == 1) return "-";
            if (_kind == 2) return "+";
            ArrayCommandArguments.ValidateIndex(_index);
            return _index;
        }
    }
    /// <inheritdoc/>
    public override string ToString() => _kind switch
    {
        1 => "-", 2 => "+", _ => _index.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>ARGREP predicate interpretation.</summary>
public enum RespireArrayPredicateKind
{
    /// <summary>Exact byte equality.</summary>
    Exact,
    /// <summary>A contained substring (Redis MATCH).</summary>
    Contains,
    /// <summary>A Redis glob pattern.</summary>
    Glob,
    /// <summary>A Redis POSIX extended regular expression.</summary>
    Regex,
}

/// <summary>A predicate applied to existing array values.</summary>
public readonly record struct RespireArrayPredicate(RespireArrayPredicateKind Kind, RespireValue Pattern);

/// <summary>Optional ARGREP predicate combination and reply limit.</summary>
public readonly record struct RespireArrayGrepOptions
{
    /// <summary>Require all predicates instead of the default OR combination.</summary>
    public bool MatchAll { get; init; }
    /// <summary>Use Redis NOCASE matching.</summary>
    public bool IgnoreCase { get; init; }
    /// <summary>A positive maximum number of matching slots; null has no limit.</summary>
    public long? Limit { get; init; }
}

/// <summary>An AROP operation over existing slots in an inclusive range.</summary>
public enum RespireArrayOperation
{
    /// <summary>Sum numeric values.</summary>
    Sum,
    /// <summary>Smallest numeric value.</summary>
    Min,
    /// <summary>Largest numeric value.</summary>
    Max,
    /// <summary>Bitwise AND of integer-convertible values.</summary>
    And,
    /// <summary>Bitwise OR of integer-convertible values.</summary>
    Or,
    /// <summary>Bitwise XOR of integer-convertible values.</summary>
    Xor,
    /// <summary>Count exact matches to the supplied value.</summary>
    Match,
    /// <summary>Count populated slots.</summary>
    Used,
}

/// <summary>
/// An AROP result. SUM/MIN/MAX preserve Redis's numeric text without rounding it to a
/// .NET double. Bitwise operations and MATCH/USED return Integer. Empty numeric or
/// bitwise input returns null; MATCH/USED return zero instead.
/// </summary>
public readonly record struct RespireArrayAggregate(string? NumericText, long? Integer)
{
    /// <summary>Whether Redis returned nil.</summary>
    public bool IsNull => NumericText is null && Integer is null;
}

/// <summary>ARINFO metadata. Encoding statistics are present only with FULL.</summary>
public sealed class RespireArrayInfo
{
    /// <summary>Number of populated slots.</summary>
    public ulong Count { get; internal set; }
    /// <summary>Highest populated index plus one.</summary>
    public ulong Length { get; internal set; }
    /// <summary>Reported insertion position. ARINFO reports zero for an exhausted cursor; use ARNEXT to distinguish exhaustion.</summary>
    public ulong NextInsertIndex { get; internal set; }
    /// <summary>Allocated slices.</summary>
    public ulong Slices { get; internal set; }
    /// <summary>Allocated directory capacity.</summary>
    public ulong DirectorySize { get; internal set; }
    /// <summary>Super-directory entries, or zero without a super-directory.</summary>
    public ulong SuperDirectoryEntries { get; internal set; }
    /// <summary>Positions per slice.</summary>
    public ulong SliceSize { get; internal set; }
    /// <summary>Dense slice count with FULL.</summary>
    public ulong? DenseSlices { get; internal set; }
    /// <summary>Sparse slice count with FULL.</summary>
    public ulong? SparseSlices { get; internal set; }
    /// <summary>Average dense window size with FULL.</summary>
    public double? AverageDenseSize { get; internal set; }
    /// <summary>Average dense fill ratio with FULL.</summary>
    public double? AverageDenseFill { get; internal set; }
    /// <summary>Average sparse capacity with FULL.</summary>
    public double? AverageSparseSize { get; internal set; }
}
