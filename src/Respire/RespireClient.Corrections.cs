using System.Runtime.CompilerServices;
using Respire.Internal;

namespace Respire;

public sealed partial class RespireClient
{
    internal interface ITrackedCorrectionExecution<TResult>
    {
        ValueTask<TResult> Response { get; }
        TrackedConnectionIdentity ConnectionIdentity { get; }
        bool CommandMayBeOutstanding { get; }
        RespireTelemetry.ErrorObservation ErrorObservation => default;
        ValueTask<TResult> CompleteResponseAsync(ValueTask<TResult> response) => response;
    }

    internal enum CorrectionOrdering
    {
        // Waits for the captured connection's fence before the dependent correction; a fence
        // failure propagates and prevents the correction.
        FenceFirst,
        // The supplied correction owns FIFO ordering, route following, and any required fence
        // (cache TTL convergence, hash-field lease cleanup, queued semaphore fence-then-release).
        // No fence acknowledgement is fabricated.
        OrderedCorrection,
        // Compatible managed release must preserve its original error if fencing fails; fence
        // failures are only logged. Cannot run a dependent correction.
        BestEffortLockFence,
        // A managed release without CLIENT permissions still reports ownership loss. Cannot run a
        // dependent correction.
        NotifyOnly,
    }

    /// <summary>
    /// Awaits a tracked operation and dispatches cleanup only for an ambiguous outcome.
    /// Setup remains outside this boundary; the identity is read after routing has settled.
    /// </summary>
    /// <remarks>Submission state and the final connection identity are read only after the response
    /// settles, so redirects cannot leave cleanup targeting a pre-redirect identity. Callers pass value
    /// state and static callbacks; success allocates no identity accessor or cleanup closure. Definitive
    /// Redis errors and transport proof of non-submission skip cleanup. An uncertain managed lock
    /// operation notifies ownership loss before waiting for a fence. Cleanup is never cancelled with
    /// the abandoned command's token; <see cref="Internal.CorrectionCoordinator"/> owns its policy.</remarks>
    internal ValueTask<TResult> ExecuteWithCorrectionAsync<TResult>(
        ITrackedCorrectionExecution<TResult> execution,
        CorrectionOrdering ordering,
        Action? onOutcomeUncertain = null)
        => ExecuteWithCorrectionAsync(execution, ordering, false, correct: null, onOutcomeUncertain);

    internal ValueTask<TResult> ExecuteWithCorrectionAsync<TResult, TState>(
        ITrackedCorrectionExecution<TResult> execution,
        CorrectionOrdering ordering,
        TState state,
        Func<TState, TrackedConnectionIdentity, ValueTask>? correct,
        Action? onOutcomeUncertain = null)
    {
        if (correct is not null && ordering is CorrectionOrdering.BestEffortLockFence or CorrectionOrdering.NotifyOnly)
            return ValueTask.FromException<TResult>(
                new ArgumentException("A dependent correction requires explicit ordering.", nameof(ordering)));
        // The tracked response lends its lease. Only this observer returns it, after the
        // response's mutation fence and any dependent correction have completed.
        var observation = execution.ErrorObservation;
        if (execution is TrackedLockExecution)
            return execution.CompleteResponseAsync(
                ExecuteWithCorrectionCoreAsync(execution, ordering, state, correct, onOutcomeUncertain, observation));
        if (observation.IsEmpty) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        return RespireTelemetry.ObserveFinalError(
            ExecuteWithCorrectionCoreAsync(execution, ordering, state, correct, onOutcomeUncertain, observation), observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TResult> ExecuteWithCorrectionCoreAsync<TResult, TState>(
        ITrackedCorrectionExecution<TResult> execution,
        CorrectionOrdering ordering,
        TState state,
        Func<TState, TrackedConnectionIdentity, ValueTask>? correct,
        Action? onOutcomeUncertain,
        RespireTelemetry.ErrorObservation observation)
    {
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
                    await TryFenceLockConnectionAsync(identity, "lock release", observation).ConfigureAwait(false);
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
