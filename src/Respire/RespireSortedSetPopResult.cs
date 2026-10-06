using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>A selected sorted-set key and its popped member/score. Returned storage is owned; Key excludes the view prefix.</summary>
public readonly record struct RespireSortedSetPopResult(RespireKey Key, SortedSetEntry Entry);

/// <summary>A selected sorted-set key and its popped entries in score order. Returned storage is owned; Key excludes the view prefix.</summary>
public readonly record struct RespireSortedSetPopManyResult(RespireKey Key,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    SortedSetEntry[] Entries);

/// <summary>A selected sorted-set key and its popped member/score. Returned storage is owned; Key excludes the view prefix.</summary>
public readonly record struct RespireSortedSetPopResult<T>(RespireKey Key, SortedSetEntry<T> Entry);

/// <summary>A selected sorted-set key and its popped entries in score order. Returned storage is owned; Key excludes the view prefix.</summary>
public readonly record struct RespireSortedSetPopManyResult<T>(RespireKey Key,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    SortedSetEntry<T>[] Entries);
