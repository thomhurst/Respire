using Respire;
using Respire.Coordination;

namespace Respire.Samples.Recipes;

public static class LeaderElection
{
    public static async Task<bool> TryRunAsync(IRespireClient client,
        Func<long, CancellationToken, Task> leaderWork, CancellationToken cancellationToken = default)
    {
        var coordination = new RespireCoordination(client);
        await using var attempt = await coordination.TryAcquireFencedLockAsync(
            "{scheduler}:leader", "{scheduler}:fence", TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (!attempt.Acquired) return false;

        await using var renewal = await attempt.Lock.KeepAliveAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, renewal.CancellationToken);
        // The protected resource must reject writes with an older fencing token.
        await leaderWork(attempt.Lock.FencingToken, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        return true;
    }
}
