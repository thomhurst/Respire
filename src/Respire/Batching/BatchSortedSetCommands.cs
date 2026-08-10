using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Sorted set (score-ordered members) commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="ISortedSetCommands"/>; collection
/// cardinality uses <see cref="CountAsync"/> and score-range cardinality uses
/// <see cref="CountByScoreAsync"/>.
/// </summary>
public interface IBatchSortedSetCommands
{
    /// <summary>Adds or updates one member. True when the member was new. Redis: ZADD.</summary>
    RespirePending<bool> AddAsync(RespireKey key, RespireValue member, double score);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    RespirePending<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries);

    /// <summary>The member's score, or null when absent. Redis: ZSCORE.</summary>
    RespirePending<double?> ScoreAsync(RespireKey key, RespireValue member);

    /// <summary>Atomically adds to a member's score and returns the new score. Redis: ZINCRBY.</summary>
    RespirePending<double> IncrementAsync(RespireKey key, RespireValue member, double by);

    /// <summary>Removes members; returns how many existed. Redis: ZREM.</summary>
    RespirePending<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Number of members. Redis: ZCARD.</summary>
    RespirePending<long> CountAsync(RespireKey key);

    /// <summary>Members with scores within the inclusive range. Redis: ZCOUNT.</summary>
    RespirePending<long> CountByScoreAsync(RespireKey key, double min, double max);

    /// <summary>The member's 0-based rank, or null when absent. Redis: ZRANK / ZREVRANK.</summary>
    RespirePending<long?> RankAsync(RespireKey key, RespireValue member, bool descending = false);

    /// <summary>Members by rank range (inclusive; negative counts from the end). Redis: ZRANGE.</summary>
    RespirePending<string[]> RangeAsync(RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores by rank range. Redis: ZRANGE WITHSCORES.</summary>
    RespirePending<SortedSetEntry[]> RangeWithScoresAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false);

    /// <summary>Members with scores within the inclusive score range. Redis: ZRANGE BYSCORE.</summary>
    RespirePending<string[]> RangeByScoreAsync(RespireKey key, double min, double max, bool descending = false);
}

internal sealed class BatchSortedSetCommands(IPendingSink sink) : IBatchSortedSetCommands
{
    public RespirePending<bool> AddAsync(RespireKey key, RespireValue member, double score)
        => sink.Add<Cmd3, bool>(
            "ZADD", new Cmd3(Verbs.ZAdd, sink.Client.Key(in key), score, member),
            static (c, v) => ResponseReader.Flag(in v));

    public RespirePending<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries)
        => sink.Add<Cmd1N, long>(
            "ZADD",
            new Cmd1N(Verbs.ZAdd, sink.Client.Key(in key), SortedSetCommands.ScoreMemberPairs(entries)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<double?> ScoreAsync(RespireKey key, RespireValue member)
        => sink.Add<Cmd2, double?>(
            "ZSCORE", new Cmd2(Verbs.ZScore, sink.Client.Key(in key), member),
            static (c, v) => ResponseReader.DoubleOrNull(in v));

    public RespirePending<double> IncrementAsync(RespireKey key, RespireValue member, double by)
        => sink.Add<Cmd3, double>(
            "ZINCRBY", new Cmd3(Verbs.ZIncrBy, sink.Client.Key(in key), by, member),
            static (c, v) => ResponseReader.Double(in v));

    public RespirePending<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => sink.Add<Cmd1N, long>(
            "ZREM", new Cmd1N(Verbs.ZRem, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> CountAsync(RespireKey key)
        => sink.Add<Cmd1, long>(
            "ZCARD", new Cmd1(Verbs.ZCard, sink.Client.Key(in key)),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long> CountByScoreAsync(RespireKey key, double min, double max)
        => sink.Add<Cmd3, long>(
            "ZCOUNT", new Cmd3(Verbs.ZCount, sink.Client.Key(in key), min, max),
            static (c, v) => ResponseReader.Integer(in v));

    public RespirePending<long?> RankAsync(RespireKey key, RespireValue member, bool descending = false)
        => sink.Add<Cmd2, long?>(
            descending ? "ZREVRANK" : "ZRANK",
            new Cmd2(descending ? Verbs.ZRevRank : Verbs.ZRank, sink.Client.Key(in key), member),
            static (c, v) => ResponseReader.IntegerOrNull(in v));

    public RespirePending<string[]> RangeAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false)
        => descending
            ? sink.Add<Cmd4, string[]>(
                "ZRANGE", new Cmd4(Verbs.ZRange, sink.Client.Key(in key), start, stop, "REV"),
                static (c, v) => ResponseReader.StringArray(in v))
            : sink.Add<Cmd3, string[]>(
                "ZRANGE", new Cmd3(Verbs.ZRange, sink.Client.Key(in key), start, stop),
                static (c, v) => ResponseReader.StringArray(in v));

    public RespirePending<SortedSetEntry[]> RangeWithScoresAsync(
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

    public RespirePending<string[]> RangeByScoreAsync(
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
