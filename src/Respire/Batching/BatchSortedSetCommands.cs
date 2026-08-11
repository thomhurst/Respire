using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Sorted set (score-ordered members) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="ISortedSetCommands"/>; collection
/// cardinality uses <see cref="Count"/> and score-range cardinality uses
/// <see cref="CountByScore(RespireKey, double, double)"/>.
/// </summary>
public interface IBatchSortedSetCommands
{
    /// <summary>Adds or updates one member. True when the member was new. Redis: ZADD.</summary>
    RespirePending<bool> Add(RespireKey key, RespireValue member, double score);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    RespirePending<long> Add(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries);

    /// <summary>The member's score, or null when absent. Redis: ZSCORE.</summary>
    RespirePending<double?> Score(RespireKey key, RespireValue member);

    /// <summary>Scores for each member, preserving nulls for missing members. Redis: ZMSCORE.</summary>
    RespirePending<double?[]> Scores(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Atomically adds to a member's score and returns the new score. Redis: ZINCRBY.</summary>
    RespirePending<double> Increment(RespireKey key, RespireValue member, double by);

    /// <summary>Removes members; returns how many existed. Redis: ZREM.</summary>
    RespirePending<long> Remove(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Number of members. Redis: ZCARD.</summary>
    RespirePending<long> Count(RespireKey key);

    /// <summary>
    /// Removes and returns up to <paramref name="count"/> members, lowest-scored first unless
    /// <paramref name="descending"/> is true. Redis: ZPOPMIN / ZPOPMAX.
    /// </summary>
    RespirePending<SortedSetEntry[]> Pop(
        RespireKey key, long count, bool descending = false);

    /// <summary>Removes members and deserializes them with their scores. Redis: ZPOPMIN / ZPOPMAX.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<SortedSetEntry<T>[]> Pop<T>(RespireKey key, long count, bool descending = false);

    /// <summary>Removes members whose scores are within the inclusive range. Redis: ZREMRANGEBYSCORE.</summary>
    RespirePending<long> RemoveRangeByScore(RespireKey key, double min, double max);

    /// <summary>Removes members whose scores are within the range. Redis: ZREMRANGEBYSCORE.</summary>
    RespirePending<long> RemoveRangeByScore(RespireKey key, RespireScoreRange range)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>Removes members whose ranks are within the inclusive range. Redis: ZREMRANGEBYRANK.</summary>
    RespirePending<long> RemoveRangeByRank(RespireKey key, long start, long stop);

    /// <summary>Members with scores within the inclusive range. Redis: ZCOUNT.</summary>
    RespirePending<long> CountByScore(RespireKey key, double min, double max);

    /// <summary>Number of members whose scores are within the range. Redis: ZCOUNT.</summary>
    RespirePending<long> CountByScore(RespireKey key, RespireScoreRange range)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>The member's 0-based rank, or null when absent. Redis: ZRANK / ZREVRANK.</summary>
    RespirePending<long?> Rank(RespireKey key, RespireValue member, bool descending = false);

    /// <summary>Members by rank range (inclusive; negative counts from the end). Redis: ZRANGE.</summary>
    RespirePending<string[]> Range(RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members by rank deserialized as <typeparamref name="T"/>. Redis: ZRANGE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T[]> Range<T>(RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores by rank range. Redis: ZRANGE WITHSCORES.</summary>
    RespirePending<SortedSetEntry[]> RangeWithScores(
        RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members and scores by rank, with members deserialized. Redis: ZRANGE WITHSCORES.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<SortedSetEntry<T>[]> RangeWithScores<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores within the inclusive score range. Redis: ZRANGE BYSCORE.</summary>
    RespirePending<string[]> RangeByScore(RespireKey key, double min, double max, bool descending = false);

    /// <summary>Members within an inclusive score range, deserialized. Redis: ZRANGE BYSCORE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T[]> RangeByScore<T>(RespireKey key, double min, double max, bool descending = false);

    /// <summary>Members within a score range, optionally paged. Redis: ZRANGE BYSCORE.</summary>
    RespirePending<string[]> RangeByScore(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => throw new NotSupportedException("Typed sorted-set score ranges are not implemented.");

    /// <summary>Members within a score range, deserialized and optionally paged. Redis: ZRANGE BYSCORE.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T[]> RangeByScore<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false);

    /// <summary>Members and scores within a score range, optionally paged. Redis: ZRANGE BYSCORE WITHSCORES.</summary>
    RespirePending<SortedSetEntry[]> RangeByScoreWithScores(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => throw new NotSupportedException("Sorted-set score ranges with scores are not implemented.");

    /// <summary>Members and scores within a score range, with members deserialized.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<SortedSetEntry<T>[]> RangeByScoreWithScores<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false);

    /// <summary>The sorted-set intersection. Redis: ZINTER.</summary>
    RespirePending<string[]> Intersect(params ReadOnlySpan<RespireKey> keys);

    /// <summary>The sorted-set union. Redis: ZUNION.</summary>
    RespirePending<string[]> Union(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members in the first sorted set but not the rest. Redis: ZDIFF.</summary>
    RespirePending<string[]> Difference(params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the intersection and returns its size. Redis: ZINTERSTORE.</summary>
    RespirePending<long> IntersectStore(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the union and returns its size. Redis: ZUNIONSTORE.</summary>
    RespirePending<long> UnionStore(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Stores the difference and returns its size. Redis: ZDIFFSTORE.</summary>
    RespirePending<long> DifferenceStore(RespireKey destination, params ReadOnlySpan<RespireKey> keys);

    /// <summary>Members within a lexicographical range, optionally paged. Redis: ZRANGE BYLEX.</summary>
    RespirePending<string[]> RangeByLex(
        RespireKey key, RespireLexRange range, long offset = 0, long? count = null,
        bool descending = false)
        => throw new NotSupportedException("Sorted-set lexicographical ranges are not implemented.");

    /// <summary>Stores a rank range in another sorted set. Redis: ZRANGESTORE.</summary>
    RespirePending<long> StoreRange(
        RespireKey destination, RespireKey source, long start = 0, long stop = -1,
        bool descending = false)
        => throw new NotSupportedException("Sorted-set range storage is not implemented.");

    /// <summary>Stores a score range in another sorted set. Redis: ZRANGESTORE BYSCORE.</summary>
    RespirePending<long> StoreRangeByScore(
        RespireKey destination, RespireKey source, RespireScoreRange range,
        long offset = 0, long? count = null, bool descending = false)
        => throw new NotSupportedException("Sorted-set score-range storage is not implemented.");

    /// <summary>Stores a lexicographical range in another sorted set. Redis: ZRANGESTORE BYLEX.</summary>
    RespirePending<long> StoreRangeByLex(
        RespireKey destination, RespireKey source, RespireLexRange range,
        long offset = 0, long? count = null, bool descending = false)
        => throw new NotSupportedException("Sorted-set lexicographical range storage is not implemented.");
}

internal sealed class BatchSortedSetCommands(IPendingSink sink) : IBatchSortedSetCommands
{
    public RespirePending<bool> Add(RespireKey key, RespireValue member, double score)
        => sink.Add<Cmd3, bool>(
            "ZADD", new Cmd3(Verbs.ZAdd, sink.Client.Key(in key), score, member),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> Add(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries)
        => sink.Add<Cmd1N, long>(
            "ZADD",
            new Cmd1N(Verbs.ZAdd, sink.Client.Key(in key), SortedSetCommands.ScoreMemberPairs(entries)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double?> Score(RespireKey key, RespireValue member)
        => sink.Add<Cmd2, double?>(
            "ZSCORE", new Cmd2(Verbs.ZScore, sink.Client.Key(in key), member),
            static (c, v) => ResponseReader.DoubleOrNull(in v));

    public RespirePending<double?[]> Scores(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => sink.Add<Cmd1N, double?[]>(
            "ZMSCORE", new Cmd1N(Verbs.ZMScore, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.NullableDoubleArray(in v));

    public RespirePending<double> Increment(RespireKey key, RespireValue member, double by)
        => sink.Add<Cmd3, double>(
            "ZINCRBY", new Cmd3(Verbs.ZIncrBy, sink.Client.Key(in key), by, member),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<long> Remove(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => sink.Add<Cmd1N, long>(
            "ZREM", new Cmd1N(Verbs.ZRem, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> Count(RespireKey key)
        => sink.Add<Cmd1, long>(
            "ZCARD", new Cmd1(Verbs.ZCard, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<SortedSetEntry[]> Pop(
        RespireKey key, long count, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var command = descending ? RespireCommands.SortedSet.ZPOPMAX : RespireCommands.SortedSet.ZPOPMIN;
        return sink.Add<Cmd2, SortedSetEntry[]>(
            command.Name, new Cmd2(command.Verb, sink.Client.Key(in key), count),
            static (c, v) => SortedSetCommands.ParseEntries(in v));
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<SortedSetEntry<T>[]> Pop<T>(
        RespireKey key, long count, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var command = descending ? RespireCommands.SortedSet.ZPOPMAX : RespireCommands.SortedSet.ZPOPMIN;
        return sink.Add<Cmd2, SortedSetEntry<T>[]>(
            command.Name, new Cmd2(command.Verb, sink.Client.Key(in key), count),
            static (c, v) => SortedSetCommands.ParseEntries<T>(c, in v));
    }

    public RespirePending<long> RemoveRangeByScore(RespireKey key, double min, double max)
        => RemoveRangeByScore(key, new RespireScoreRange(min, max));

    public RespirePending<long> RemoveRangeByScore(RespireKey key, RespireScoreRange range)
        => sink.Add<Cmd3, long>(
            "ZREMRANGEBYSCORE",
            new Cmd3(
                RespireCommands.SortedSet.ZREMRANGEBYSCORE.Verb,
                sink.Client.Key(in key),
                range.Minimum.ToRespireValue(),
                range.Maximum.ToRespireValue()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> RemoveRangeByRank(RespireKey key, long start, long stop)
        => sink.Add<Cmd3, long>(
            "ZREMRANGEBYRANK",
            new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYRANK.Verb, sink.Client.Key(in key), start, stop),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> CountByScore(RespireKey key, double min, double max)
        => CountByScore(key, new RespireScoreRange(min, max));

    public RespirePending<long> CountByScore(RespireKey key, RespireScoreRange range)
        => sink.Add<Cmd3, long>(
            "ZCOUNT",
            new Cmd3(
                Verbs.ZCount,
                sink.Client.Key(in key),
                range.Minimum.ToRespireValue(),
                range.Maximum.ToRespireValue()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long?> Rank(RespireKey key, RespireValue member, bool descending = false)
        => sink.Add<Cmd2, long?>(
            descending ? "ZREVRANK" : "ZRANK",
            new Cmd2(descending ? Verbs.ZRevRank : Verbs.ZRank, sink.Client.Key(in key), member),
            static (c, v) => ResponseReader.IntegerOrNull(in v));

    public RespirePending<string[]> Range(
        RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => descending
            ? sink.Add<Cmd4, string[]>(
                "ZRANGE", new Cmd4(Verbs.ZRange, sink.Client.Key(in key), start, stop, "REV"),
                static (c, v) => ResponseReader.StringArray(in v))
            : sink.Add<Cmd3, string[]>(
                "ZRANGE", new Cmd3(Verbs.ZRange, sink.Client.Key(in key), start, stop),
                static (c, v) => ResponseReader.StringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T[]> Range<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => descending
            ? sink.Add<Cmd4, T[]>(
                "ZRANGE", new Cmd4(Verbs.ZRange, sink.Client.Key(in key), start, stop, "REV"),
                static (c, v) => c.DeserializeArray<T>(in v))
            : sink.Add<Cmd3, T[]>(
                "ZRANGE", new Cmd3(Verbs.ZRange, sink.Client.Key(in key), start, stop),
                static (c, v) => c.DeserializeArray<T>(in v));

    public RespirePending<SortedSetEntry[]> RangeWithScores(
        RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => descending
            ? sink.Add<Cmd5, SortedSetEntry[]>(
                "ZRANGE",
                new Cmd5(Verbs.ZRange, sink.Client.Key(in key), start, stop, "REV", "WITHSCORES"),
                static (c, v) => SortedSetCommands.ParseEntries(in v))
            : sink.Add<Cmd4, SortedSetEntry[]>(
                "ZRANGE",
                new Cmd4(Verbs.ZRange, sink.Client.Key(in key), start, stop, "WITHSCORES"),
                static (c, v) => SortedSetCommands.ParseEntries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<SortedSetEntry<T>[]> RangeWithScores<T>(
        RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => descending
            ? sink.Add<Cmd5, SortedSetEntry<T>[]>(
                "ZRANGE",
                new Cmd5(Verbs.ZRange, sink.Client.Key(in key), start, stop, "REV", "WITHSCORES"),
                static (c, v) => SortedSetCommands.ParseEntries<T>(c, in v))
            : sink.Add<Cmd4, SortedSetEntry<T>[]>(
                "ZRANGE",
                new Cmd4(Verbs.ZRange, sink.Client.Key(in key), start, stop, "WITHSCORES"),
                static (c, v) => SortedSetCommands.ParseEntries<T>(c, in v));

    public RespirePending<string[]> RangeByScore(
        RespireKey key, double min, double max, bool descending = false)
        => RangeByScore(key, new RespireScoreRange(min, max), descending: descending);

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T[]> RangeByScore<T>(
        RespireKey key, double min, double max, bool descending = false)
        => RangeByScore<T>(key, new RespireScoreRange(min, max), descending: descending);

    public RespirePending<string[]> RangeByScore(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => sink.Add<Cmd1N, string[]>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                sink.Client.Key(in key),
                SortedSetCommands.RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: false)),
            static (c, v) => ResponseReader.StringArray(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T[]> RangeByScore<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => sink.Add<Cmd1N, T[]>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                sink.Client.Key(in key),
                SortedSetCommands.RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: false)),
            static (c, v) => c.DeserializeArray<T>(in v));

    public RespirePending<SortedSetEntry[]> RangeByScoreWithScores(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => sink.Add<Cmd1N, SortedSetEntry[]>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                sink.Client.Key(in key),
                SortedSetCommands.RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: true)),
            static (c, v) => SortedSetCommands.ParseEntries(in v));

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<SortedSetEntry<T>[]> RangeByScoreWithScores<T>(
        RespireKey key, RespireScoreRange range, long offset = 0, long? count = null,
        bool descending = false)
        => sink.Add<Cmd1N, SortedSetEntry<T>[]>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                sink.Client.Key(in key),
                SortedSetCommands.RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYSCORE", offset, count, descending, withScores: true)),
            static (c, v) => SortedSetCommands.ParseEntries<T>(c, in v));

    public RespirePending<string[]> Intersect(params ReadOnlySpan<RespireKey> keys)
        => Algebra("ZINTER", Verbs.ZInter, keys);

    public RespirePending<string[]> Union(params ReadOnlySpan<RespireKey> keys)
        => Algebra("ZUNION", Verbs.ZUnion, keys);

    public RespirePending<string[]> Difference(params ReadOnlySpan<RespireKey> keys)
        => Algebra("ZDIFF", Verbs.ZDiff, keys);

    public RespirePending<long> IntersectStore(
        RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => AlgebraStore("ZINTERSTORE", Verbs.ZInterStore, destination, keys);

    public RespirePending<long> UnionStore(
        RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => AlgebraStore("ZUNIONSTORE", Verbs.ZUnionStore, destination, keys);

    public RespirePending<long> DifferenceStore(
        RespireKey destination, params ReadOnlySpan<RespireKey> keys)
        => AlgebraStore("ZDIFFSTORE", Verbs.ZDiffStore, destination, keys);

    public RespirePending<string[]> RangeByLex(
        RespireKey key, RespireLexRange range, long offset = 0, long? count = null,
        bool descending = false)
        => sink.Add<Cmd1N, string[]>(
            "ZRANGE",
            new Cmd1N(
                Verbs.ZRange,
                sink.Client.Key(in key),
                SortedSetCommands.RangeArguments(
                    range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
                    "BYLEX", offset, count, descending, withScores: false)),
            static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<long> StoreRange(
        RespireKey destination, RespireKey source, long start = 0, long stop = -1,
        bool descending = false)
        => sink.Add<Cmd2N, long>(
            "ZRANGESTORE",
            new Cmd2N(
                RespireCommands.SortedSet.ZRANGESTORE.Verb,
                sink.Client.Key(in destination),
                sink.Client.Key(in source),
                descending ? [start, stop, "REV"] : [start, stop]),
            destination, source,
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> StoreRangeByScore(
        RespireKey destination, RespireKey source, RespireScoreRange range,
        long offset = 0, long? count = null, bool descending = false)
        => StoreRangeCore(
            destination, source,
            range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
            "BYSCORE", offset, count, descending);

    public RespirePending<long> StoreRangeByLex(
        RespireKey destination, RespireKey source, RespireLexRange range,
        long offset = 0, long? count = null, bool descending = false)
        => StoreRangeCore(
            destination, source,
            range.Minimum.ToRespireValue(), range.Maximum.ToRespireValue(),
            "BYLEX", offset, count, descending);

    private RespirePending<long> StoreRangeCore(
        RespireKey destination, RespireKey source, RespireValue minimum, RespireValue maximum,
        string mode, long offset, long? count, bool descending)
        => sink.Add<Cmd2N, long>(
            "ZRANGESTORE",
            new Cmd2N(
                RespireCommands.SortedSet.ZRANGESTORE.Verb,
                sink.Client.Key(in destination),
                sink.Client.Key(in source),
                SortedSetCommands.RangeArguments(
                    minimum, maximum, mode, offset, count, descending, withScores: false)),
            destination, source,
            static (c, v) => ResponseReader.Integer(in v));

    private RespirePending<string[]> Algebra(
        string operation, Verb verb, ReadOnlySpan<RespireKey> keys)
        => sink.Add<CmdN, string[]>(
            operation,
            new CmdN(verb, SortedSetCommands.CountedKeys(sink.Client.MapKeys(keys))),
            keys,
            static (c, v) => ResponseReader.StringArray(in v));

    private RespirePending<long> AlgebraStore(
        string operation, Verb verb, RespireKey destination, ReadOnlySpan<RespireKey> keys)
        => sink.Add<Cmd1N, long>(
            operation,
            new Cmd1N(
                verb,
                sink.Client.Key(in destination),
                SortedSetCommands.CountedKeys(sink.Client.MapKeys(keys))),
            destination,
            keys,
            static (c, v) => ResponseReader.Integer(in v));
}
