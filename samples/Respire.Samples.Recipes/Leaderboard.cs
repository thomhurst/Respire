using Respire;

namespace Respire.Samples.Recipes;

public sealed class Leaderboard(IRespireClient client)
{
    public const string ScoresKey = "{leaderboard:season-1}:scores";
    public const string NamesKey = "{leaderboard:season-1}:names";

    public async Task SaveAsync(string memberId, string displayName, double score,
        CancellationToken cancellationToken = default)
    {
        // Both keys share a Cluster slot. All writers must reserve these key types.
        await using var transaction = client.CreateTransaction();
        var name = transaction.Hashes.Set(NamesKey, (memberId, (RespireValue)displayName));
        var rank = transaction.SortedSets.Add(ScoresKey, memberId, score);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _ = name.Result;
        _ = rank.Result;
    }

    public async Task<Row[]> TopAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var entries = await client.SortedSets.RangeWithScoresAsync(
            ScoresKey, 0, count - 1, descending: true, cancellationToken).ConfigureAwait(false);
        if (entries.Length == 0) return [];

        var names = await client.Hashes.GetManyAsync(
            NamesKey, entries.Select(entry => entry.Member).ToArray(), cancellationToken).ConfigureAwait(false);
        return entries.Select((entry, index) =>
            new Row(entry.Member, names[index] ?? entry.Member, entry.Score)).ToArray();
    }

    public sealed record Row(string MemberId, string DisplayName, double Score);
}
