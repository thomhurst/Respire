using Respire.Networking;

namespace Respire.Internal;

/// <summary>Route-specific state carried by value through a dedicated rental.</summary>
internal interface IDedicatedLeaseRoute : IDisposable
{
    void ThrowIfDisposed();
    bool CanRetry(int attempt, CancellationToken cancellationToken);
    void RecordRetirement(Exception error, int attempt);
    ValueTask<DedicatedConnectionPool> SelectReplacementAsync(CancellationToken cancellationToken);
    void SetTerminalError(DedicatedConnectionPool pool, Exception error);
}

internal static class DedicatedLeaseAcquisition
{
    // One caller token covers every rental and replacement selection. The pool's connection
    // recovery owns transient connect retries; this loop retries only topology retirement,
    // before any application command is accepted. Struct routes avoid strategy allocations.
    internal static async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentAsync<TRoute>(
        DedicatedConnectionPool pool, TRoute route, CancellationToken cancellationToken,
        bool reuseIdle, DedicatedLeaseKind kind)
        where TRoute : struct, IDedicatedLeaseRoute
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                route.ThrowIfDisposed();
                try
                {
                    var connection = await pool.RentAsync(cancellationToken, reuseIdle: reuseIdle, kind: kind).ConfigureAwait(false);
                    return (pool, connection);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && pool.IsStopping
                    && error is ObjectDisposedException or OperationCanceledException
                    && route.CanRetry(attempt, cancellationToken))
                {
                    route.RecordRetirement(error, attempt);
                    var replacement = await route.SelectReplacementAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReferenceEquals(replacement, pool)) throw;
                    pool = replacement;
                }
            }
        }
        catch (Exception error) { route.SetTerminalError(pool, error); throw; }
        finally { route.Dispose(); }
    }
}
