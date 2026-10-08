using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // A value holder keeps response ownership together without a per-read helper allocation.
    // ResolveAsync reads a copy; the caller records its returned winner before final cleanup.
    private struct HedgeRace
    {
        internal Task<RespValue>? Original;
        internal Task<RespValue>? Hedge;
        internal RespireConnection? Alternative;
        internal Task<RespValue>? Returned;
        internal RespireTelemetry.ErrorObservation OriginalObservation;
        internal RespireTelemetry.ErrorObservation HedgeObservation;

#if NET
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        internal readonly async ValueTask<(RespValue Response, Task<RespValue> Winner)> ResolveAsync(
            RespireTelemetry.ErrorObservation observation, int callerAttempts)
        {
            var original = Original!;
            var hedge = Hedge;
            var winner = hedge is null ? original : await Task.WhenAny(original, hedge).ConfigureAwait(false);
            try
            {
                RespValue response;
                try { response = await winner.ConfigureAwait(false); }
                catch (Exception error) when (hedge is not null && !IsFatalHedgeFailure(error))
                {
                    // A failed optional leg cannot replace a successful original. If both fail,
                    // preserve the original exception type and identity instead of aggregating it.
                    winner = ReferenceEquals(winner, original) ? hedge : original;
                    try { response = await winner.ConfigureAwait(false); }
                    catch (Exception otherError) when (!IsFatalHedgeFailure(otherError))
                    {
                        winner = original;
                        return (await original.ConfigureAwait(false), original);
                    }
                }
                if (ReferenceEquals(winner, hedge)) RespireTelemetry.RecordHedgeWon(Alternative!);
                return (response, winner);
            }
            finally
            {
                observation.SetAttempts(callerAttempts +
                    (ReferenceEquals(winner, original) ? OriginalObservation : HedgeObservation).Attempts);
            }
        }

        internal readonly void DisposeLosers()
        {
            // Accepted losers retain their FIFO response slots. Observe and dispose their
            // replies without canceling them or delaying the caller that receives the winner.
            // Resolution may already have awaited a failed leg. Report only here so each
            // discarded leg has one owner. A sole failed original belongs to the caller.
            if (Original is not null && !ReferenceEquals(Original, Returned))
                _ = DisposeReplyAsync(Original, OriginalObservation, Hedge is not null);
            else OriginalObservation.Dispose();
            if (Hedge is not null && !ReferenceEquals(Hedge, Returned))
                _ = DisposeReplyAsync(Hedge, HedgeObservation, reportError: true);
            else HedgeObservation.Dispose();
        }

        private static async Task DisposeReplyAsync(Task<RespValue> response,
            RespireTelemetry.ErrorObservation observation, bool reportError)
        {
            try { using var discarded = await response.ConfigureAwait(false); }
            catch (Exception error)
            {
                if (reportError) RespireTelemetry.RecordError(error, internallyHandled: true, observation.Attempts);
            }
            finally { observation.Dispose(); }
        }
    }
}
