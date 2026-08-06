using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>A sorted-set member with its score.</summary>
public readonly record struct SortedSetEntry(string Member, double Score);

/// <summary>Sorted set (score-ordered members) commands.</summary>
public interface ISortedSetCommands
{
    /// <summary>Adds or updates one member. Returns true when the member was new. Redis: ZADD.</summary>
    ValueTask<bool> AddAsync(RespireKey key, RespireValue member, double score, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates many members; returns how many were new. Redis: ZADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries);

    /// <summary>The member's score, or null when absent. Redis: ZSCORE.</summary>
    ValueTask<double?> ScoreAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default);

    /// <summary>Atomically adds to a member's score and returns the new score. Redis: ZINCRBY.</summary>
    ValueTask<double> IncrementAsync(RespireKey key, RespireValue member, double by, CancellationToken cancellationToken = default);

    /// <summary>Removes members; returns how many existed. Redis: ZREM.</summary>
    ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Number of members. Redis: ZCARD.</summary>
    ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Members with scores within the inclusive range. Redis: ZCOUNT.</summary>
    ValueTask<long> CountByScoreAsync(RespireKey key, double min, double max, CancellationToken cancellationToken = default);

    /// <summary>The member's 0-based rank, or null when absent. Redis: ZRANK / ZREVRANK.</summary>
    ValueTask<long?> RankAsync(RespireKey key, RespireValue member, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members by rank range (inclusive; negative counts from the end). Redis: ZRANGE.</summary>
    ValueTask<string[]> RangeAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members with scores by rank range. Redis: ZRANGE WITHSCORES.</summary>
    ValueTask<SortedSetEntry[]> RangeWithScoresAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default);

    /// <summary>Members with scores within the inclusive score range. Redis: ZRANGE BYSCORE.</summary>
    ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, double min, double max, bool descending = false, CancellationToken cancellationToken = default);
}

internal sealed class SortedSetCommands(RespireClient client) : ISortedSetCommands
{
    public ValueTask<bool> AddAsync(RespireKey key, RespireValue member, double score, CancellationToken cancellationToken = default)
        => client.FlagAsync("ZADD", new Cmd3(Verbs.ZAdd, client.Key(in key), score, member), cancellationToken);

    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<SortedSetEntry> entries)
    {
        var args = new RespireValue[entries.Length * 2];
        for (var i = 0; i < entries.Length; i++)
        {
            args[i * 2] = entries[i].Score;
            args[i * 2 + 1] = entries[i].Member;
        }

        return client.IntegerAsync("ZADD", new Cmd1N(Verbs.ZAdd, client.Key(in key), args), CancellationToken.None);
    }

    public ValueTask<double?> ScoreAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken = default)
        => client.DoubleOrNullAsync("ZSCORE", new Cmd2(Verbs.ZScore, client.Key(in key), member), cancellationToken);

    public ValueTask<double> IncrementAsync(RespireKey key, RespireValue member, double by, CancellationToken cancellationToken = default)
        => client.DoubleAsync("ZINCRBY", new Cmd3(Verbs.ZIncrBy, client.Key(in key), by, member), cancellationToken);

    public ValueTask<long> RemoveAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
        => client.IntegerAsync(
            "ZREM", new Cmd1N(Verbs.ZRem, client.Key(in key), RespireClient.MapValues(members)), CancellationToken.None);

    public ValueTask<long> CountAsync(RespireKey key, CancellationToken cancellationToken = default)
        => client.IntegerAsync("ZCARD", new Cmd1(Verbs.ZCard, client.Key(in key)), cancellationToken);

    public ValueTask<long> CountByScoreAsync(RespireKey key, double min, double max, CancellationToken cancellationToken = default)
        => client.IntegerAsync("ZCOUNT", new Cmd3(Verbs.ZCount, client.Key(in key), min, max), cancellationToken);

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

    public async ValueTask<SortedSetEntry[]> RangeWithScoresAsync(
        RespireKey key, long start = 0, long stop = -1, bool descending = false, CancellationToken cancellationToken = default)
    {
        var reply = descending
            ? await client.SendAsync(
                "ZRANGE", new Cmd5(Verbs.ZRange, client.Key(in key), start, stop, "REV", "WITHSCORES"), cancellationToken)
                .ConfigureAwait(false)
            : await client.SendAsync(
                "ZRANGE", new Cmd4(Verbs.ZRange, client.Key(in key), start, stop, "WITHSCORES"), cancellationToken)
                .ConfigureAwait(false);

        var entries = ParseEntries(in reply);
        reply.Dispose();
        return entries;
    }

    public ValueTask<string[]> RangeByScoreAsync(
        RespireKey key, double min, double max, bool descending = false, CancellationToken cancellationToken = default)
        => descending
            // With REV the bounds swap: ZRANGE key max min BYSCORE REV.
            ? client.StringArrayAsync(
                "ZRANGE", new Cmd5(Verbs.ZRange, client.Key(in key), max, min, "BYSCORE", "REV"), cancellationToken)
            : client.StringArrayAsync(
                "ZRANGE", new Cmd4(Verbs.ZRange, client.Key(in key), min, max, "BYSCORE"), cancellationToken);

    /// <summary>WITHSCORES replies alternate member,score (RESP2 flat array; RESP3 pairs are flattened too).</summary>
    private static SortedSetEntry[] ParseEntries(in RespValue reply)
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
}
