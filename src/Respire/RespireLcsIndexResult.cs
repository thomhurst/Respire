using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>A zero-based, inclusive range of byte offsets in a Redis string.</summary>
public readonly record struct RespireLcsRange(long Start, long End);

/// <summary>A contiguous match in the first and second strings.</summary>
/// <param name="FirstRange">Inclusive byte offsets in the first string.</param>
/// <param name="SecondRange">Inclusive byte offsets in the second string.</param>
/// <param name="Length">The server-reported match length, or null unless WITHMATCHLEN was requested.</param>
public readonly record struct RespireLcsMatch(RespireLcsRange FirstRange, RespireLcsRange SecondRange, long? Length);

/// <summary>Owned LCS IDX matches in server order (last match first), and the total subsequence length before filtering.</summary>
/// <remarks>Matches is an owned mutable array. Equality compares Matches by reference and Length by value.</remarks>
public readonly record struct RespireLcsIndexResult(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO owns its match array without copying on access.")]
    RespireLcsMatch[] Matches, long Length);

/// <summary>Options for LCS IDX (Redis 7.0+).</summary>
public sealed record RespireLcsOptions
{
    /// <summary>Only return contiguous matches at least this many bytes long. Must be nonnegative; null omits MINMATCHLEN.</summary>
    public long? MinimumMatchLength { get; init; }

    /// <summary>Include the server-reported length of each match using WITHMATCHLEN.</summary>
    public bool IncludeMatchLength { get; init; }
}
