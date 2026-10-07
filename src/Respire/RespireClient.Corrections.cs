namespace Respire;

public sealed partial class RespireClient
{
    internal interface ITrackedCorrectionExecution<TResult>
    {
        ValueTask<TResult> Response { get; }
        TrackedConnectionIdentity ConnectionIdentity { get; }
        bool CommandMayBeOutstanding { get; }
    }

    internal enum CorrectionOrdering
    {
        FenceFirst,
        // The supplied correction owns FIFO ordering, route following, and any required fence.
        OrderedCorrection,
        // Compatible managed release must preserve its original error if fencing fails.
        BestEffortLockFence,
        // A managed release without CLIENT permissions still reports ownership loss.
        NotifyOnly,
    }

    /// <summary>
    /// Awaits a tracked operation and dispatches cleanup only for an ambiguous outcome.
    /// Setup remains outside this boundary; the identity is read after routing has settled.
    /// </summary>
    internal ValueTask<TResult> ExecuteWithCorrectionAsync<TResult>(
        ITrackedCorrectionExecution<TResult> execution,
        CorrectionOrdering ordering,
        Action? onOutcomeUncertain = null)
        => ExecuteWithCorrectionAsync(execution, ordering, false, correct: null, onOutcomeUncertain);

    internal async ValueTask<TResult> ExecuteWithCorrectionAsync<TResult, TState>(
        ITrackedCorrectionExecution<TResult> execution,
        CorrectionOrdering ordering,
        TState state,
        Func<TState, TrackedConnectionIdentity, ValueTask>? correct,
        Action? onOutcomeUncertain = null)
    {
        if (correct is not null && ordering is CorrectionOrdering.BestEffortLockFence or CorrectionOrdering.NotifyOnly)
            throw new ArgumentException("A dependent correction requires explicit ordering.", nameof(ordering));
        try
        {
            return await execution.Response.ConfigureAwait(false);
        }
        catch (Exception error) when (execution.CommandMayBeOutstanding && IsUncertainCorrectionOutcome(error))
        {
            // Managed ownership must be lost before a potentially blocked fence begins.
            onOutcomeUncertain?.Invoke();
            var identity = execution.ConnectionIdentity;
            if (ordering == CorrectionOrdering.FenceFirst && correct is not null && identity.ServerClientId <= 0)
                throw new InvalidOperationException("A fenced correction requires a tracked Redis client identity.", error);
            if (identity.ServerClientId > 0)
            {
                if (ordering == CorrectionOrdering.FenceFirst)
                    await FenceCorrectionConnectionAsync(identity).ConfigureAwait(false);
                else if (ordering == CorrectionOrdering.BestEffortLockFence
                    && error is OperationCanceledException or RespireTimeoutException or RespireConnectionException)
                    await TryFenceLockConnectionAsync(identity, "lock release").ConfigureAwait(false);
            }

            if (correct is not null)
                await correct(state, identity).ConfigureAwait(false);
            throw;
        }
    }

    internal static bool IsUncertainCorrectionOutcome(Exception error)
        => RespireException.GetDefinitiveServerError(error) is null && !IsCorrectionNotSubmitted(error);

    internal static bool IsCorrectionNotSubmitted(Exception error)
        => error is RespireException { IsCommandNotSubmitted: true }
            or RespireCommandNotSubmittedException
            or Networking.RespireConnectionRetiredException
            or Networking.RespireConnectionClosedBeforeSendException
            or RespireTimeoutException { Diagnostics.Stage: RespireCommandStage.WaitingForCapacity };
}
