using Respire;

namespace Respire.Samples.Recipes;

public sealed class SessionStore(IRespireClient client)
{
    // One hash per user; every opaque session ID has an independent sliding lifetime.
    public ValueTask<bool> SaveAsync(string userId, string sessionId, string payload,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default)
        => client.Hashes.SetExpireAsync(Key(userId), idleTimeout,
            (sessionId, (RespireValue)payload), cancellationToken); // HSETEX

    public async Task<string?> ReadAndRefreshAsync(string userId, string sessionId,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        var values = await client.Hashes.GetAndExpireAsync(
            Key(userId), idleTimeout, sessionId, cancellationToken).ConfigureAwait(false); // HGETEX
        return values[0]; // Missing or expired sessions stay missing.
    }

    public ValueTask<long> RevokeAsync(string userId, string sessionId,
        CancellationToken cancellationToken = default)
        => client.Hashes.RemoveAsync(Key(userId), sessionId, cancellationToken);

    public static string Key(string userId) => $"sessions:{userId}";
}
