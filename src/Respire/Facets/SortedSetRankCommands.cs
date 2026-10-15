using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>A member's zero-based position and score in a sorted set.</summary>
/// <param name="Rank">The zero-based position in the requested ascending or descending order.</param>
/// <param name="Score">The member's score.</param>
public readonly record struct RespireSortedSetRankResult(long Rank, double Score);

public partial interface IRespireSortedSetCommands
{
    /// <summary>Returns a member's rank and score, or null when the key or member is missing. Redis: ZRANK/ZREVRANK WITHSCORE (7.2+).</summary>
    ValueTask<RespireSortedSetRankResult?> RankWithScoreAsync(RespireKey key, RespireValue member,
        bool descending = false, CancellationToken cancellationToken = default);
}

internal sealed partial class SortedSetCommands
{
    public ValueTask<RespireSortedSetRankResult?> RankWithScoreAsync(RespireKey key, RespireValue member,
        bool descending = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync(descending ? "ZREVRANK" : "ZRANK",
            RankWithScoreCommand(client, key, member, descending), cancellationToken, this,
            static (SortedSetCommands _, in RespValue value) => ParseRankWithScore(in value));

    internal static Cmd3 RankWithScoreCommand(RespireClient client, RespireKey key, RespireValue member, bool descending)
        => new(descending ? Verbs.ZRevRank : Verbs.ZRank, client.Key(in key), member, "WITHSCORE");

    internal static RespireSortedSetRankResult? ParseRankWithScore(in RespValue value)
    {
        if (value.IsNull) return null;
        if (value.Type != RespireDataType.Array || value.AsArray().Length != 2)
            throw new RespireProtocolException("A sorted-set rank with score must contain exactly two elements.");
        var pair = value.AsArray();
        if (pair[0].Type != RespireDataType.Integer || pair[0].AsInteger() < 0)
            throw new RespireProtocolException("A sorted-set rank must be a nonnegative integer.");
        return new(pair[0].AsInteger(), ResponseReader.Double(in pair[1]));
    }
}
