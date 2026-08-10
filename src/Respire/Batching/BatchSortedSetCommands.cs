using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Sorted set (score-ordered members) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="ISortedSetCommands"/>; collection
/// cardinality uses <see cref="Count"/> and score-range cardinality uses
/// <see cref="CountByScore"/>.
/// </summary>
public interface IBatchSortedSetCommands
{
    /// <summary>Adds or updates one member. True when the member was new. Redis: ZADD.</summary>
    RespirePending<bool> Add(RespireKey key, RespireValue member, double score);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    RespirePending<long> Add(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries);

    /// <summary>The member's score, or null when absent. Redis: ZSCORE.</summary>
    RespirePending<double?> Score(RespireKey key, RespireValue member);

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

    /// <summary>Removes members whose scores are within the inclusive range. Redis: ZREMRANGEBYSCORE.</summary>
    RespirePending<long> RemoveRangeByScore(RespireKey key, double min, double max);

    /// <summary>Removes members whose ranks are within the inclusive range. Redis: ZREMRANGEBYRANK.</summary>
    RespirePending<long> RemoveRangeByRank(RespireKey key, long start, long stop);

    /// <summary>Members with scores within the inclusive range. Redis: ZCOUNT.</summary>
    RespirePending<long> CountByScore(RespireKey key, double min, double max);

    /// <summary>The member's 0-based rank, or null when absent. Redis: ZRANK / ZREVRANK.</summary>
    RespirePending<long?> Rank(RespireKey key, RespireValue member, bool descending = false);

    /// <summary>Members by rank range (inclusive; negative counts from the end). Redis: ZRANGE.</summary>
    RespirePending<string[]> Range(RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores by rank range. Redis: ZRANGE WITHSCORES.</summary>
    RespirePending<SortedSetEntry[]> RangeWithScores(
        RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores within the inclusive score range. Redis: ZRANGE BYSCORE.</summary>
    RespirePending<string[]> RangeByScore(RespireKey key, double min, double max, bool descending = false);
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

    public RespirePending<long> RemoveRangeByScore(RespireKey key, double min, double max)
        => sink.Add<Cmd3, long>(
            "ZREMRANGEBYSCORE",
            new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYSCORE.Verb, sink.Client.Key(in key), min, max),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> RemoveRangeByRank(RespireKey key, long start, long stop)
        => sink.Add<Cmd3, long>(
            "ZREMRANGEBYRANK",
            new Cmd3(RespireCommands.SortedSet.ZREMRANGEBYRANK.Verb, sink.Client.Key(in key), start, stop),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> CountByScore(RespireKey key, double min, double max)
        => sink.Add<Cmd3, long>(
            "ZCOUNT", new Cmd3(Verbs.ZCount, sink.Client.Key(in key), min, max),
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

    public RespirePending<string[]> RangeByScore(
        RespireKey key, double min, double max, bool descending = false)
        => descending
            // With REV the bounds swap: ZRANGE key max min BYSCORE REV.
            ? sink.Add<Cmd5, string[]>(
                "ZRANGE", new Cmd5(Verbs.ZRange, sink.Client.Key(in key), max, min, "BYSCORE", "REV"),
                static (c, v) => ResponseReader.StringArray(in v))
            : sink.Add<Cmd4, string[]>(
                "ZRANGE", new Cmd4(Verbs.ZRange, sink.Client.Key(in key), min, max, "BYSCORE"),
                static (c, v) => ResponseReader.StringArray(in v));
}
