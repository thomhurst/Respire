using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Serialization;

namespace Respire;

public partial interface ISortedSetCommands
{
    /// <summary>Adds or conditionally updates one member. Returns true when new, or changed when CH is set. Redis: ZADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Adds or conditionally updates one serialized member; booleans retain Redis 1/0 encoding. Returns true when new, or changed with CH.</summary>
    /// <remarks>Specify the type argument explicitly to select serialization for types with an implicit RespireValue conversion.</remarks>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    ValueTask<bool> AddAsync<T>(RespireKey key, RespireSortedSetAddOptions options, T member, double score,
        CancellationToken cancellationToken = default);

    /// <summary>Adds or conditionally updates members. Returns the number added, or added/changed with CH. Requires at least one entry.</summary>
    ValueTask<long> AddAsync(RespireKey key, RespireSortedSetAddOptions options,
        params ReadOnlySpan<(RespireValue Member, double Score)> entries);

    /// <summary>Adds or conditionally updates members with cancellation. Returns the number added, or added/changed with CH.</summary>
    ValueTask<long> AddAsync(RespireKey key, RespireSortedSetAddOptions options,
        ReadOnlySpan<(RespireValue Member, double Score)> entries, CancellationToken cancellationToken);

    /// <summary>Conditionally increments one score. Returns null when the condition rejects it. GT/LT compare the resulting score. Redis: ZADD INCR.</summary>
    ValueTask<double?> IncrementAsync(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double by,
        CancellationToken cancellationToken = default);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double score,
        CancellationToken cancellationToken = default)
        => client.FlagAsync("ZADD", AddCommand(client, key, options, member, score), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [OverloadResolutionPriority(-1)]
    public ValueTask<bool> AddAsync<T>(RespireKey key, RespireSortedSetAddOptions options, T member, double score,
        CancellationToken cancellationToken = default)
    {
        // Reject invalid options before invoking user serialization. AddCommand also validates
        // because non-generic callers reach it directly.
        SortedSetAddCommand.Validate(options);
        return AddAsync(key, options, client.SerializeCollectionMember(member), score, cancellationToken);
    }

    public ValueTask<long> AddAsync(RespireKey key, RespireSortedSetAddOptions options,
        params ReadOnlySpan<(RespireValue Member, double Score)> entries)
        => AddAsync(key, options, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(RespireKey key, RespireSortedSetAddOptions options,
        ReadOnlySpan<(RespireValue Member, double Score)> entries, CancellationToken cancellationToken)
        => client.IntegerAsync("ZADD", AddCommand(client, key, options, entries), cancellationToken);

    public ValueTask<double?> IncrementAsync(RespireKey key, RespireSortedSetAddOptions options, RespireValue member, double by,
        CancellationToken cancellationToken = default)
        => client.DoubleOrNullAsync("ZADD", AddCommand(client, key, options, member, by, increment: true), cancellationToken);

    internal static SortedSetAddCommand AddCommand(RespireClient client, RespireKey key,
        RespireSortedSetAddOptions options, RespireValue member, double score, bool increment = false)
    {
        SortedSetAddCommand.Validate(options);
        return new(client.Key(in key), options, member, score, increment);
    }

    internal static SortedSetAddCommand AddCommand(RespireClient client, RespireKey key,
        RespireSortedSetAddOptions options, ReadOnlySpan<(RespireValue Member, double Score)> entries)
    {
        SortedSetAddCommand.Validate(options);
        if (entries.IsEmpty) throw new ArgumentException("At least one member is required.", nameof(entries));
        return new(client.Key(in key), options, member: default, score: default, pairs: ScoreMemberPairs(entries));
    }
}
