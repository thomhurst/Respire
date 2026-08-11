using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;
using Respire.Protocol;

namespace Respire;

/// <summary>A sorted-set member with its score.</summary>
public readonly record struct SortedSetEntry(string Member, double Score);

/// <summary>A deserialized sorted-set member with its score.</summary>
public readonly record struct SortedSetEntry<T>(T Member, double Score);

/// <summary>An inclusive or exclusive score boundary used by sorted-set range commands.</summary>
public readonly record struct RespireScoreBound
{
    private RespireScoreBound(double value, bool exclusive)
    {
        if (double.IsNaN(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A score boundary cannot be NaN.");
        }
        if (exclusive && double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An infinite score boundary cannot be exclusive.");
        }

        Value = value;
        IsExclusive = exclusive;
    }

    /// <summary>The numeric boundary value.</summary>
    public double Value { get; }

    /// <summary>Whether the boundary excludes <see cref="Value"/>.</summary>
    public bool IsExclusive { get; }

    /// <summary>Negative infinity.</summary>
    public static RespireScoreBound Min => new(double.NegativeInfinity, exclusive: false);

    /// <summary>Positive infinity.</summary>
    public static RespireScoreBound Max => new(double.PositiveInfinity, exclusive: false);

    /// <summary>Creates an inclusive score boundary.</summary>
    public static RespireScoreBound Inclusive(double value) => new(value, exclusive: false);

    /// <summary>Creates an exclusive, finite score boundary.</summary>
    public static RespireScoreBound Exclusive(double value) => new(value, exclusive: true);

    /// <summary>Creates an inclusive score boundary.</summary>
    public static implicit operator RespireScoreBound(double value) => Inclusive(value);

    internal RespireValue ToRespireValue()
    {
        if (double.IsNegativeInfinity(Value))
        {
            return "-inf";
        }

        if (double.IsPositiveInfinity(Value))
        {
            return "+inf";
        }

        return string.Concat(
            IsExclusive ? "(" : null,
            Value.ToString("R", CultureInfo.InvariantCulture));
    }
}

/// <summary>A minimum and maximum score boundary.</summary>
public readonly record struct RespireScoreRange(RespireScoreBound Minimum, RespireScoreBound Maximum)
{
    /// <summary>Creates an inclusive score range.</summary>
    public RespireScoreRange(double minimum, double maximum)
        : this((RespireScoreBound)minimum, (RespireScoreBound)maximum)
    {
    }

    /// <summary>All possible scores.</summary>
    public static RespireScoreRange All => new(RespireScoreBound.Min, RespireScoreBound.Max);
}

/// <summary>An inclusive or exclusive member boundary used by lexicographical sorted-set ranges.</summary>
public readonly record struct RespireLexBound
{
    private const byte InclusiveKind = 0;
    private const byte ExclusiveKind = 1;
    private const byte MinimumKind = 2;
    private const byte MaximumKind = 3;

    private readonly byte _kind;

    private RespireLexBound(string? value, byte kind)
    {
        if (kind <= ExclusiveKind)
        {
            ArgumentNullException.ThrowIfNull(value);
        }

        Value = value;
        _kind = kind;
    }

    /// <summary>The member value, or null for an infinite boundary.</summary>
    public string? Value { get; }

    /// <summary>Whether this finite boundary excludes <see cref="Value"/>.</summary>
    public bool IsExclusive => _kind == ExclusiveKind;

    /// <summary>Negative infinity.</summary>
    public static RespireLexBound Min => new(null, MinimumKind);

    /// <summary>Positive infinity.</summary>
    public static RespireLexBound Max => new(null, MaximumKind);

    /// <summary>Creates an inclusive member boundary.</summary>
    public static RespireLexBound Inclusive(string value) => new(value, InclusiveKind);

    /// <summary>Creates an exclusive member boundary.</summary>
    public static RespireLexBound Exclusive(string value) => new(value, ExclusiveKind);

    /// <summary>Creates an inclusive member boundary.</summary>
    public static implicit operator RespireLexBound(string value) => Inclusive(value);

    internal RespireValue ToRespireValue()
        => _kind switch
        {
            MinimumKind => "-",
            MaximumKind => "+",
            ExclusiveKind => string.Concat("(", Value),
            _ when Value is not null => string.Concat("[", Value),
            _ => throw new InvalidOperationException("A default RespireLexBound is not valid."),
        };
}

/// <summary>A minimum and maximum lexicographical member boundary.</summary>
public readonly record struct RespireLexRange(RespireLexBound Minimum, RespireLexBound Maximum)
{
    /// <summary>Creates an inclusive lexicographical range.</summary>
    public RespireLexRange(string minimum, string maximum)
        : this((RespireLexBound)minimum, (RespireLexBound)maximum)
    {
    }

    /// <summary>All possible members.</summary>
    public static RespireLexRange All => new(RespireLexBound.Min, RespireLexBound.Max);
}

/// <summary>
/// Sorted set (score-ordered members) commands. Collection cardinality uses
/// <see cref="CountAsync"/>; score-range cardinality uses
/// <see cref="CountByScoreAsync(RespireKey, double, double, CancellationToken)"/>.
/// </summary>
public interface ISortedSetCommands
{
    /// <summary>Adds or updates one member. Returns true when the member was new. Redis: ZADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, RespireValue member, double score, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates one serialized <typeparamref name="T"/> member. Returns true when the
    /// member was new. Redis: ZADD.
    /// <para>
    /// An argument already typed as <see cref="RespireValue"/> picks the non-generic overload;
    /// any other type picks this one. Boolean members retain the Redis-native <c>1</c>/<c>0</c>
    /// encoding used by the other member APIs; other types use normal typed serialization.
    /// <paramref name="score"/> has no default, so a lone <see cref="SortedSetEntry"/> still binds
    /// to the <c>params</c> overload.
    /// </para>
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<bool> AddAsync<T>(RespireKey key, T member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, ReadOnlySpan<SortedSetEntry> entries, CancellationToken cancellationToken);

    /// <summary>The member's score, or null when absent. Redis: ZSCORE.</summary>
    ValueTask<double?> ScoreAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);

    /// <summary>The scores for each member, preserving nulls for absent members. Redis: ZMSCORE.</summary>
    ValueTask<double?[]> ScoresAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>The scores for each member, preserving nulls for absent members. Redis: ZMSCORE.</summary>
    ValueTask<double?[]> ScoresAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Atomically adds to a member's score and returns the new score. Redis: ZINCRBY.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, RespireValue member, double by, CancellationToken cancellationToken = default);

    /// <summary>Removes members; returns how many existed. Redis: ZREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Removes members; returns how many existed. Redis: ZREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Number of members. Redis: ZCARD.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Iterates members and scores incrementally. Redis: ZSCAN.</summary>
    IAsyncEnumerable<SortedSetEntry> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes and returns up to <paramref name="count"/> members, lowest-scored first unless
    /// <paramref name="descending"/> is true. Redis: ZPOPMIN / ZPOPMAX.
    /// </summary>
    ValueTask<SortedSetEntry[]> PopAsync(
        RespireKey key, long count, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Removes members and deserializes them with their scores. Redis: ZPOPMIN / ZPOPMAX.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<SortedSetEntry<T>[]> PopAsync<T>(
        RespireKey key, long count, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Removes members whose scores are within the inclusive range. Redis: ZREMRANGEBYSCORE.</summary>
    ValueTask<long> RemoveRangeByScoreAsync(
        RespireKey key, double min, double max, CancellationToken cancellationToken = default);

    /// <summary>Removes members whose scores are within the range. Redis: ZREMRANGEBYSCORE.</summary>
    ValueTask<long> RemoveRangeByScoreAsync(
        RespireKey key, RespireScoreRange range, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>Removes members whose ranks are within the inclusive range. Redis: ZREMRANGEBYRANK.</summary>
    ValueTask<long> RemoveRangeByRankAsync(
        RespireKey key, long start, long stop, CancellationToken cancellationToken = default);

    /// <summary>Members with scores within the inclusive range. Redis: ZCOUNT.</summary>
    ValueTask<long> CountByScoreAsync(RespireKey key, double min, double max, CancellationToken cancellationToken = default);

    /// <summary>Number of members whose scores are within the range. Redis: ZCOUNT.</summary>
    ValueTask<long> CountByScoreAsync(
        RespireKey key, RespireScoreRange range, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>The member's 0-based rank, or null when absent. Redis: ZRANK / ZREVRANK.</summary>
    ValueTask<long?> RankAsync(RespireKey key, RespireValue member, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members by rank range (inclusive; negative counts from the end). Redis: ZRANGE.</summary>
    ValueTask<string[]> RangeAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members by rank range deserialized as <typeparamref name="T"/>. Redis: ZRANGE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RangeAsync<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Members with scores by rank range. Redis: ZRANGE WITHSCORES.</summary>
    ValueTask<SortedSetEntry[]> RangeWithScoresAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members and scores by rank range, with members deserialized. Redis: ZRANGE WITHSCORES.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<SortedSetEntry<T>[]> RangeWithScoresAsync<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Members with scores within the inclusive score range. Redis: ZRANGE BYSCORE.</summary>
    ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, double min, double max, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members within an inclusive score range, deserialized as <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RangeByScoreAsync<T>(
        RespireKey key, double min, double max, bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>Members within a score range, optionally paged. Redis: ZRANGE BYSCORE.</summary>
    ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>Members within a score range, deserialized and optionally paged. Redis: ZRANGE BYSCORE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<T[]> RangeByScoreAsync<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members and scores within a score range, optionally paged. Redis: ZRANGE BYSCORE WITHSCORES.</summary>
    ValueTask<SortedSetEntry[]> RangeByScoreWithScoresAsync(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sorted-set score ranges with scores are not implemented.");

    /// <summary>Members and scores within a score range, with members deserialized. Redis: ZRANGE BYSCORE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    ValueTask<SortedSetEntry<T>[]> RangeByScoreWithScoresAsync<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>The intersection of the sorted sets. Redis: ZINTER.</summary>
    ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The intersection of the sorted sets. Redis: ZINTER.</summary>
    ValueTask<string[]> IntersectAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>The union of the sorted sets. Redis: ZUNION.</summary>
    ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The union of the sorted sets. Redis: ZUNION.</summary>
    ValueTask<string[]> UnionAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Members in the first sorted set but not the rest. Redis: ZDIFF.</summary>
    ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members in the first sorted set but not the rest. Redis: ZDIFF.</summary>
    ValueTask<string[]> DifferenceAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the intersection and returns its size. Redis: ZINTERSTORE.</summary>
    ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the intersection and returns its size. Redis: ZINTERSTORE.</summary>
    ValueTask<long> IntersectStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the union and returns its size. Redis: ZUNIONSTORE.</summary>
    ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the union and returns its size. Redis: ZUNIONSTORE.</summary>
    ValueTask<long> UnionStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Stores the difference and returns its size. Redis: ZDIFFSTORE.</summary>
    ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the difference and returns its size. Redis: ZDIFFSTORE.</summary>
    ValueTask<long> DifferenceStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);

    /// <summary>Members within a lexicographical range, optionally paged. Redis: ZRANGE BYLEX.</summary>
    ValueTask<string[]> RangeByLexAsync(
        RespireKey key, RespireLexRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sorted-set lexicographical ranges are not implemented.");

    /// <summary>Stores a rank range in another sorted set. Redis: ZRANGESTORE.</summary>
    ValueTask<long> StoreRangeAsync(
        RespireKey destination, RespireKey source, long start = 0, long stop = -1,
        bool descending = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sorted-set range storage is not implemented.");

    /// <summary>Stores a score range in another sorted set. Redis: ZRANGESTORE BYSCORE.</summary>
    ValueTask<long> StoreRangeByScoreAsync(
        RespireKey destination, RespireKey source, RespireScoreRange range,
        long offset = 0, long? count = null, bool descending = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sorted-set score-range storage is not implemented.");

    /// <summary>Stores a lexicographical range in another sorted set. Redis: ZRANGESTORE BYLEX.</summary>
    ValueTask<long> StoreRangeByLexAsync(
        RespireKey destination, RespireKey source, RespireLexRange range,
        long offset = 0, long? count = null, bool descending = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Sorted-set lexicographical range storage is not implemented.");
}

internal sealed class SortedSetCommands(RespireClient client) : ISortedSetCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, RespireValue member, double score, CancellationToken cancellationToken = default)
        => client.FlagAsync("ZADD", new Cmd3(Verbs.ZAdd, client.Key(in key), score, member), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<bool> AddAsync<T>(RespireKey key, T member, double score, CancellationToken cancellationToken = default)
        => client.FlagAsync(
            "ZADD",
            new Cmd3(Verbs.ZAdd, client.Key(in key), score, client.SerializeCollectionMember(member)),
            cancellationToken);

    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries)
        => AddAsync(key, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(
        RespireKey key, ReadOnlySpan<SortedSetEntry> entries, CancellationToken cancellationToken)
        => client.IntegerAsync(
            "ZADD", new Cmd1N(Verbs.ZAdd, client.Key(in key), ScoreMemberPairs(entries)), cancellationToken);

    /// <summary>score member… — shared with the deferred (batch/transaction) facet.</summary>
    internal static RespireValue[] ScoreMemberPairs(ReadOnlySpan<SortedSetEntry> entries)
    {
        var args = new RespireValue[entries.Length * 2];
        for (var i = 0; i < entries.Length; i++)
        {
            args[i * 2] = entries[i].Score;
            args[i * 2 + 1] = entries[i].Member;
        }

        return args;
    }

    public ValueTask<double?> ScoreAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => client.DoubleOrNullAsync("ZSCORE", new Cmd2(Verbs.ZScore, client.Key(in key), member), cancellationToken);

    public ValueTask<double?[]> ScoresAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => ScoresAsync(key, members, CancellationToken.None);

    public ValueTask<double?[]> ScoresAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
        => client.NullableDoubleArrayAsync(
            "ZMSCORE", new Cmd1N(Verbs.ZMScore, client.Key(in key), members.ToArray()), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, RespireValue member, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("ZINCRBY", new Cmd3(Verbs.ZIncrBy, client.Key(in key), by, member), cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => RemoveAsync(key, members, CancellationToken.None);

    public ValueTask<long> RemoveAsync(RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
        => client.IntegerValuesAsync("ZREM", Verbs.ZRem, client.Key(in key), members, cancellationToken);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("ZCARD", new Cmd1(Verbs.ZCard, client.Key(in key)), cancellationToken);

    public IAsyncEnumerable<SortedSetEntry> ScanAsync(
        RespireKey key, string? match = null, int countHint = 250,
        CancellationToken cancellationToken = default)
        => CollectionScan.EnumerateAsync(
            client, "ZSCAN", RespireCommands.SortedSet.ZSCAN.Verb, key, match, countHint,
            ParseEntries, cancellationToken);

    public ValueTask<SortedSetEntry[]> PopAsync(
        RespireKey key, long count, bool descending = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var command = descending ? RespireCommands.SortedSet.ZPOPMAX : RespireCommands.SortedSet.ZPOPMIN;
        return client.ConvertResponseAsync(
            command.Name, new Cmd2(command.Verb, client.Key(in key), count), cancellationToken, this,
            static (SortedSetCommands _, in RespValue value) => ParseEntries(in value));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<SortedSetEntry<T>[]> PopAsync<T>(
        RespireKey key, long count, bool descending = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var command = descending ? RespireCommands.SortedSet.ZPOPMAX : RespireCommands.SortedSet.ZPOPMIN;
        return client.ConvertResponseAsync(
            command.Name, new Cmd2(command.Verb, client.Key(in key), count), cancellationToken, client,
            static (RespireClient state, in RespValue value) => ParseEntries<T>(state, in value));
    }

    public ValueTask<long> RemoveRangeByScoreAsync(
        RespireKey key, double min, double max, CancellationToken cancellationToken = default)
        => RemoveRangeByScoreAsync(key, new RespireScoreRange(min, max), cancellationToken);

    public ValueTask<long> RemoveRangeByScoreAsync(
        RespireKey key, RespireScoreRange range, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "ZREMRANGEBYSCORE",
            new Cmd3(
                RespireCommands.SortedSet.ZREMRANGEBYSCORE.Verb,
                client.Key(in key),
                range.Minimum.ToRespireValue(),
                range.Maximum.ToRespireValue()),
            cancellationToken);

    public ValueTask<long> RemoveRangeByRankAsync(
        RespireKey key, long start, long stop, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "ZREMRANGEBYRANK",
            new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYRANK.Verb, client.Key(in key), start, stop),
            cancellationToken);

    public ValueTask<long> CountByScoreAsync(RespireKey key, double min, double max, CancellationToken cancellationToken = default)
        => CountByScoreAsync(key, new RespireScoreRange(min, max), cancellationToken);

    public ValueTask<long> CountByScoreAsync(
        RespireKey key, RespireScoreRange range, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "ZCOUNT",
            new Cmd3(
                Verbs.ZCount,
                client.Key(in key),
                range.Minimum.ToRespireValue(),
                range.Maximum.ToRespireValue()),
            cancellationToken);

    public ValueTask<long?> RankAsync(
        RespireKey key, RespireValue member, bool descending = false, CancellationToken cancellationToken = default)
        => client.IntegerOrNullAsync(
            descending ? "ZREVRANK" : "ZRANK",
            new Cmd2(descending ? Verbs.ZRevRank : Verbs.ZRank, client.Key(in key), member),
            cancellationToken);

    public ValueTask<string[]> RangeAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => descending
            ? client.StringArrayAsync(
                "ZRANGE", new Cmd4(Verbs.ZRange, client.Key(in key), start, stop, "REV"), cancellationToken)
            : client.StringArrayAsync(
                "ZRANGE", new Cmd3(Verbs.ZRange, client.Key(in key), start, stop), cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RangeAsync<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false,
        CancellationToken cancellationToken = default)
        => descending
            ? client.DeserializeArrayAsync<T, Cmd4>(
                "ZRANGE", new Cmd4(Verbs.ZRange, client.Key(in key), start, stop, "REV"), cancellationToken)
            : client.DeserializeArrayAsync<T, Cmd3>(
                "ZRANGE", new Cmd3(Verbs.ZRange, client.Key(in key), start, stop), cancellationToken);

    public ValueTask<SortedSetEntry[]> RangeWithScoresAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
        => descending
            ? client.ConvertResponseAsync(
                "ZRANGE", new Cmd5(Verbs.ZRange, client.Key(in key), start, stop, "REV", "WITHSCORES"),
                cancellationToken, this,
                static (SortedSetCommands _, in RespValue value) => ParseEntries(in value))
            : client.ConvertResponseAsync(
                "ZRANGE", new Cmd4(Verbs.ZRange, client.Key(in key), start, stop, "WITHSCORES"),
                cancellationToken, this,
                static (SortedSetCommands _, in RespValue value) => ParseEntries(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<SortedSetEntry<T>[]> RangeWithScoresAsync<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false,
        CancellationToken cancellationToken = default)
        => RangeWithScoresCoreAsync<T>(
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                descending ? [start, stop, "REV", "WITHSCORES"] : [start, stop, "WITHSCORES"]),
            cancellationToken);

    public ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, double min, double max, bool descending = false, CancellationToken cancellationToken = default)
        => RangeByScoreAsync(
            key, new RespireScoreRange(min, max), descending: descending, cancellationToken: cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RangeByScoreAsync<T>(
        RespireKey key, double min, double max, bool descending = false,
        CancellationToken cancellationToken = default)
        => RangeByScoreAsync<T>(
            key, new RespireScoreRange(min, max), descending: descending, cancellationToken: cancellationToken);

    public ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.StringArrayAsync(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: false)),
            cancellationToken);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T[]> RangeByScoreAsync<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.DeserializeArrayAsync<T, Cmd1N>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: false)),
            cancellationToken);

    public ValueTask<SortedSetEntry[]> RangeByScoreWithScoresAsync(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: true)),
            cancellationToken,
            this,
            static (SortedSetCommands _, in RespValue value) => ParseEntries(in value));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<SortedSetEntry<T>[]> RangeByScoreWithScoresAsync<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => RangeWithScoresCoreAsync<T>(
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: true)),
            cancellationToken);

    public ValueTask<string[]> IntersectAsync(params ReadOnlySpan<RespireKey> keys)
        => IntersectAsync(keys, CancellationToken.None);

    public ValueTask<string[]> IntersectAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraAsync("ZINTER", Verbs.ZInter, keys, cancellationToken);

    public ValueTask<string[]> UnionAsync(params ReadOnlySpan<RespireKey> keys)
        => UnionAsync(keys, CancellationToken.None);

    public ValueTask<string[]> UnionAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraAsync("ZUNION", Verbs.ZUnion, keys, cancellationToken);

    public ValueTask<string[]> DifferenceAsync(params ReadOnlySpan<RespireKey> keys)
        => DifferenceAsync(keys, CancellationToken.None);

    public ValueTask<string[]> DifferenceAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraAsync("ZDIFF", Verbs.ZDiff, keys, cancellationToken);

    public ValueTask<long> IntersectStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => IntersectStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> IntersectStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraStoreAsync("ZINTERSTORE", Verbs.ZInterStore, destination, keys, cancellationToken);

    public ValueTask<long> UnionStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => UnionStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> UnionStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraStoreAsync("ZUNIONSTORE", Verbs.ZUnionStore, destination, keys, cancellationToken);

    public ValueTask<long> DifferenceStoreAsync(RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => DifferenceStoreAsync(destination, keys, CancellationToken.None);

    public ValueTask<long> DifferenceStoreAsync(
        RespireKey destination, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => AlgebraStoreAsync("ZDIFFSTORE", Verbs.ZDiffStore, destination, keys, cancellationToken);

    public ValueTask<string[]> RangeByLexAsync(
        RespireKey key, RespireLexRange range, long offset = 0, long? count = null,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.StringArrayAsync(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                client.Key(in key),
                RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYLEX", offset, count, descending, withScores: false)),
            cancellationToken);

    public ValueTask<long> StoreRangeAsync(
        RespireKey destination, RespireKey source, long start = 0, long stop = -1,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.IntegerAsync(
            "ZRANGESTORE",
            new Cmd2N(
                RespireCommands.SortedSet.ZRANGESTORE.Verb,
                client.Key(in destination),
                client.Key(in source),
                descending ? [start, stop, "REV"] : [start, stop]),
            cancellationToken);

    public ValueTask<long> StoreRangeByScoreAsync(
        RespireKey destination, RespireKey source, RespireScoreRange range,
        long offset = 0, long? count = null, bool descending = false,
        CancellationToken cancellationToken = default)
        => StoreRangeCoreAsync(
            destination, source,
            range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
            "BYSCORE", offset, count, descending, cancellationToken);

    public ValueTask<long> StoreRangeByLexAsync(
        RespireKey destination, RespireKey source, RespireLexRange range,
        long offset = 0, long? count = null, bool descending = false,
        CancellationToken cancellationToken = default)
        => StoreRangeCoreAsync(
            destination, source,
            range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
            "BYLEX", offset, count, descending, cancellationToken);

    private ValueTask<long> StoreRangeCoreAsync(
        RespireKey destination, RespireKey source, RespireValue minimum, RespireValue maximum,
        string mode, long offset, long? count, bool descending, CancellationToken cancellationToken)
        => client.IntegerAsync(
            "ZRANGESTORE",
            new Cmd2N(
                RespireCommands.SortedSet.ZRANGESTORE.Verb,
                client.Key(in destination),
                client.Key(in source),
                RangeArguments(minimum, maximum, mode, offset, count, descending, withScores: false)),
            cancellationToken);

    internal static RespireValue[] RangeArguments(
        RespireValue minimum, RespireValue maximum, string mode,
        long offset, long? count, bool descending, bool withScores)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (count is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
        }
        else if (offset != 0)
        {
            throw new ArgumentException("A count is required when offset is non-zero.", nameof(count));
        }

        var argumentCount = 3 + (descending ? 1 : 0) + (count.HasValue ? 3 : 0) + (withScores ? 1 : 0);
        var arguments = new RespireValue[argumentCount];
        var index = 0;
        arguments[index++] = descending ? maximum : minimum;
        arguments[index++] = descending ? minimum : maximum;
        arguments[index++] = mode;
        if (descending)
        {
            arguments[index++] = "REV";
        }

        if (count is { } pageSize)
        {
            arguments[index++] = "LIMIT";
            arguments[index++] = offset;
            arguments[index++] = pageSize;
        }

        if (withScores)
        {
            arguments[index] = "WITHSCORES";
        }

        return arguments;
    }

    /// <summary>WITHSCORES replies alternate member,score (RESP2 flat array; RESP3 pairs are flattened too).</summary>
    internal static SortedSetEntry[] ParseEntries(in RespValue reply)
    {
        var elements = reply.AsArray();

        // RESP3 returns an array of [member, score] pairs.
        if (elements.Length > 0 && elements[0].Type == RespDataType.Array)
        {
            var pairEntries = new SortedSetEntry[elements.Length];
            for (var i = 0; i < elements.Length; i++)
            {
                var pair = elements[i].AsArray();
                pairEntries[i] = new SortedSetEntry(pair[0].AsString(), ResponseReader.Double(in pair[1]));
            }

            return pairEntries;
        }

        var entries = new SortedSetEntry[elements.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new SortedSetEntry(elements[i * 2].AsString(), ResponseReader.Double(in elements[i * 2 + 1]));
        }

        return entries;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    private ValueTask<SortedSetEntry<T>[]> RangeWithScoresCoreAsync<T>(
        Cmd1N command, CancellationToken cancellationToken)
        => client.ConvertResponseAsync(
            "ZRANGE", command, cancellationToken, client,
            static (RespireClient state, in RespValue value) => ParseEntries<T>(state, in value));

    private ValueTask<string[]> AlgebraAsync(
        string operation, Verb verb, ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
        => client.StringArrayAsync(
            operation, new CmdN(verb, CountedKeys(client.MapKeys(keys))), cancellationToken);

    private ValueTask<long> AlgebraStoreAsync(
        string operation, Verb verb, RespireKey destination, ReadOnlySpan<RespireKey> keys,
        CancellationToken cancellationToken)
        => client.IntegerAsync(
            operation,
            new Cmd1N(verb, client.Key(in destination), CountedKeys(client.MapKeys(keys))),
            cancellationToken);

    internal static RespireValue[] CountedKeys(RespireValue[] keys)
    {
        var arguments = new RespireValue[keys.Length + 1];
        arguments[0] = keys.Length;
        keys.CopyTo(arguments, 1);
        return arguments;
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    internal static SortedSetEntry<T>[] ParseEntries<T>(RespireClient client, in RespValue reply)
    {
        var elements = reply.AsArray();
        if (elements.Length > 0 && elements[0].Type == RespDataType.Array)
        {
            var pairEntries = new SortedSetEntry<T>[elements.Length];
            for (var i = 0; i < elements.Length; i++)
            {
                var pair = elements[i].AsArray();
                pairEntries[i] = new SortedSetEntry<T>(
                    client.DeserializeBorrowed<T>(in pair[0])!, ResponseReader.Double(in pair[1]));
            }

            return pairEntries;
        }

        var entries = new SortedSetEntry<T>[elements.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new SortedSetEntry<T>(
                client.DeserializeBorrowed<T>(in elements[i * 2])!,
                ResponseReader.Double(in elements[i * 2 + 1]));
        }

        return entries;
    }
}
