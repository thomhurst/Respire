using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>Wire representation of a vector supplied to VADD or VSIM.</summary>
public enum RespireVectorEncoding
{
    /// <summary>One little-endian FP32 bulk string, without an intermediate vector byte array.</summary>
    Fp32,
    /// <summary>VALUES followed by the dimension and invariant floating-point arguments.</summary>
    Values,
}

/// <summary>Quantization requested when a vector set is created.</summary>
public enum RespireVectorQuantization
{
    /// <summary>Use the server default.</summary>
    Default,
    /// <summary>NOQUANT: retain floating-point components.</summary>
    None,
    /// <summary>Q8: eight-bit quantization.</summary>
    Q8,
    /// <summary>BIN: binary quantization.</summary>
    Binary,
}

/// <summary>VADD controls. Existing sets retain their creation-time configuration.</summary>
/// <remarks>Repeat compatible quantization, M, and REDUCE settings on subsequent additions.
/// Omitted settings use server defaults, which can conflict with an existing set.</remarks>
public readonly record struct RespireVectorAddOptions
{
    /// <summary>REDUCE target dimension. The server validates compatibility with the set.</summary>
    public int? ReduceDimensions { get; init; }
    /// <summary>CAS: allow background graph construction where the server supports it.</summary>
    public bool CheckAndSet { get; init; }
    /// <summary>Quantization for a newly created set.</summary>
    public RespireVectorQuantization Quantization { get; init; }
    /// <summary>EF construction effort, 1 through 1,000,000.</summary>
    public int? ExplorationFactor { get; init; }
    /// <summary>M graph links, 4 through 4096.</summary>
    public int? Links { get; init; }
    /// <summary>SETATTR raw JSON. Null omits the option. Borrowed until the operation completes.</summary>
    public RespireValue AttributesJson { get; init; }
}

/// <summary>VSIM search controls. Omitted values preserve server defaults.</summary>
public readonly record struct RespireVectorSearchOptions
{
    /// <summary>Include similarity scores.</summary>
    public bool IncludeScores { get; init; }
    /// <summary>Include raw JSON attributes, or null for members without attributes.</summary>
    public bool IncludeAttributes { get; init; }
    /// <summary>Maximum results; must be positive.</summary>
    public long? Count { get; init; }
    /// <summary>EF search effort, 1 through 1,000,000.</summary>
    public int? ExplorationFactor { get; init; }
    /// <summary>FILTER expression evaluated by Redis against attributes.</summary>
    public string? Filter { get; init; }
    /// <summary>FILTER-EF maximum filtering effort; must be positive.</summary>
    public long? FilterExplorationFactor { get; init; }
    /// <summary>EPSILON maximum distance, from zero through one.</summary>
    public double? Epsilon { get; init; }
    /// <summary>TRUTH: use exact linear search instead of approximate graph search.</summary>
    public bool Exact { get; init; }
    /// <summary>NOTHREAD: execute on the server's main thread.</summary>
    public bool NoThread { get; init; }
}

/// <summary>An owned binary member with optional similarity score and JSON attributes.</summary>
/// <remarks>Arrays are caller-owned and mutable; record equality compares array references.</remarks>
public sealed record RespireVectorMatch(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] Member, double? Score,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[]? AttributesJson);

/// <summary>An owned VINFO snapshot. Unknown fields preserve future server additions.</summary>
/// <remarks>AdditionalFields have GC-owned storage and need no disposal.</remarks>
public sealed record RespireVectorSetInfo(string Quantization, long Dimensions, long Size, long Links,
    long MaximumLevel, long AttributesCount, long? ProjectionInputDimensions,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);
