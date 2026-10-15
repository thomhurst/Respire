using Respire.Commands;

namespace Respire;

public partial interface IRespireBatchSortedSetCommands
{
    /// <summary>Queues a member's rank and score, or null when missing. Redis: ZRANK/ZREVRANK WITHSCORE (7.2+).</summary>
    RespirePending<RespireSortedSetRankResult?> RankWithScore(RespireKey key, RespireValue member, bool descending = false);
}

internal sealed partial class BatchSortedSetCommands
{
    public RespirePending<RespireSortedSetRankResult?> RankWithScore(RespireKey key, RespireValue member, bool descending = false)
        => sink.Add<Cmd3, RespireSortedSetRankResult?>(descending ? "ZREVRANK" : "ZRANK",
            SortedSetCommands.RankWithScoreCommand(sink.Client, key, member, descending),
            static (c, v) => SortedSetCommands.ParseRankWithScore(in v));
}
