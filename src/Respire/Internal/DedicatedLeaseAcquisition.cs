using Respire.Networking;

namespace Respire.Internal;

/// <summary>Route-specific state carried by value through a dedicated rental.</summary>
internal interface IDedicatedLeaseRoute : IDisposable
{
    void ThrowIfDisposed();
    bool CanRetry(int attempt, CancellationToken cancellationToken);
    void RecordRetirement(Exception error, int attempt);
    bool TryExcludeFailedCandidate(DedicatedConnectionPool pool, Exception error, CancellationToken cancellationToken) => false;
    ValueTask<DedicatedConnectionPool> SelectReplacementAsync(CancellationToken cancellationToken);
    void SetTerminalError(Exception error);
}

/// <summary>Owns the dedicated rental loop for standalone, Sentinel, and Cluster callers.</summary>
/// <remarks>
/// <para>Blocking commands, WATCH creation, durability batches, and streamed uploads reselect a pool
/// retired between selection and rent, including retirement cancellation during the handshake
/// (<see cref="RespireConnectionRetiredException"/>). The retry ends at successful rent and never
/// replays application commands or WATCH state; durability keeps its fresh-connection requirement.
/// The returned pool stays with its lease through return or disposal. Returning the same stopped pool
/// ends with the original failure. Caller cancellation and client disposal stop retries. Corrective
/// fences that pin a captured server do not follow topology replacements.</para>
/// <para>Standalone/Sentinel routes select through <see cref="ClientCore"/>; Cluster keeps its
/// slot/read/ASK route and lazily creates one discovery scope after the first retirement. Discovery
/// accounting does not reset at each pool change. The original token, including an upload's
/// acquisition deadline, passes unchanged through every rental and reselection. Pool connection
/// recovery owns transient retries within a rental; pool lifetime cancellation ends that recovery
/// before this loop selects a replacement.</para>
/// <para>Wire coverage (standalone-or-Sentinel / Cluster): rental after publication
/// (MovingReplacesUploadPoolAndDrainsAcceptedUpload / RetiredPoolSelectionRetriesBeforeRentAndKeepsItsOwner);
/// handshake retirement (MovingRetriesDedicatedHandshakeRetiredBeforeDispatch / DedicatedHandshakeRetriesOnlyRetirement);
/// publication before old-pool retirement (SameEndpointPublicationRevalidatesUploadBeforePoolRetirement /
/// AskUploadRevalidatesMovingPoolBeforeOldPoolStartsStopping); disposal during drain
/// (DisposeAbortsUploadWhileMovedPoolIsRetiring, both); WATCH and durability
/// (PromotionDoesNotMoveAnExistingWatchedTransaction, NewBatchUsesThePromotedGenerationAndDurabilityKeepsOneSocket /
/// Cluster WATCH and batch durability suites). DedicatedLeaseAcquisitionTests covers same-pool rejection,
/// cancellation during selection and before idle rental, disposed owners, and allocation-free warmed
/// rentals for both route-state implementations with positive controls.</para>
/// </remarks>
internal static class DedicatedLeaseAcquisition
{
    // One caller token covers every rental and replacement selection. The pool's connection
    // recovery owns transient connect retries; routes can retry topology retirement or an
    // eligible read candidate failure before any application command is accepted.
    // Struct routes avoid strategy allocations.
    internal static async ValueTask<(DedicatedConnectionPool Pool, RespireConnection Connection)> RentAsync<TRoute>(
        DedicatedConnectionPool pool, TRoute route, CancellationToken cancellationToken,
        bool reuseIdle, DedicatedLeaseKind kind, string? preferredZone = null,
        StandaloneCircuitRegistry? circuits = null)
        where TRoute : struct, IDedicatedLeaseRoute
    {
        try
        {
            var retirements = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                route.ThrowIfDisposed();
                try
                {
                    var admission = circuits?.Acquire(pool.Endpoint, cancellationToken) ?? default;
                    try
                    {
                        var connection = await pool.RentAsync(cancellationToken, reuseIdle: reuseIdle, kind: kind,
                            preferredZone: preferredZone).ConfigureAwait(false);
                        // Connecting is not a successful application probe. Dispatch owns its own permit.
                        return (pool, connection);
                    }
                    catch (Exception error) { admission.ConnectionFailed(error, cancellationToken); throw; }
                    finally { admission.Dispose(); }
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && pool.IsStopping
                    && DedicatedConnectionPool.IsRetirementFailure(error)
                    && route.CanRetry(retirements, cancellationToken))
                {
                    route.RecordRetirement(error, retirements++);
                    var replacement = await route.SelectReplacementAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReferenceEquals(replacement, pool)) throw;
                    pool = replacement;
                }
                catch (Exception error) when (route.TryExcludeFailedCandidate(pool, error, cancellationToken))
                {
                    pool = await route.SelectReplacementAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { route.SetTerminalError(error); throw; }
        finally { route.Dispose(); }
    }
}
