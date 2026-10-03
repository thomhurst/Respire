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

#if NET
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        internal readonly async ValueTask<(RespValue Response, Task<RespValue> Winner)> ResolveAsync()
        {
            var original = Original!;
            var hedge = Hedge;
            var winner = hedge is null ? original : await Task.WhenAny(original, hedge).ConfigureAwait(false);
            RespValue response;
            try { response = await winner.ConfigureAwait(false); }
            catch (Exception error) when (hedge is not null && !IsFatalHedgeFailure(error))
            {
                // A failed optional leg cannot replace a successful original. If both fail,
                // preserve the original exception type and identity instead of aggregating it.
                winner = ReferenceEquals(winner, original) ? hedge : original;
                try { response = await winner.ConfigureAwait(false); }
                catch (Exception otherError) when (!IsFatalHedgeFailure(otherError))
                { return (await original.ConfigureAwait(false), original); }
            }
            if (ReferenceEquals(winner, hedge)) RespireTelemetry.RecordHedgeWon(Alternative!);
            return (response, winner);
        }

        internal readonly void DisposeLosers()
        {
            // Accepted losers retain their FIFO response slots. Observe and dispose their
            // replies without canceling them or delaying the caller that receives the winner.
            if (Original is not null && !ReferenceEquals(Original, Returned)) _ = DisposeReplyAsync(Original);
            if (Hedge is not null && !ReferenceEquals(Hedge, Returned)) _ = DisposeReplyAsync(Hedge);
        }

        private static async Task DisposeReplyAsync(Task<RespValue> response)
        {
            try { using var discarded = await response.ConfigureAwait(false); }
            catch (Exception) { /* A losing failure has no caller to receive it. */ }
        }
    }
}
