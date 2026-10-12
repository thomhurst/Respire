using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Retries a logical operation whose dispatch owns its own routing: Cluster redirects, script
/// fallback, function library reloads or a failover group's active member.
/// </summary>
/// <remarks>
/// Such an owner does not expose immutable transport write evidence. Every failed attempt is
/// therefore treated as a possible execution, so only the category permission can admit another
/// attempt. One deadline and cancellation budget span every attempt, route selection and backoff.
/// Each attempt reads its route again; a retry never reuses a circuit-rejected endpoint, because
/// circuit rejection is never a selected failure. When the owner's dispatch proves that a circuit
/// rejection ends a resend before it executes anything, that resend is not reported as an attempted
/// retry. A caller-supplied operation can execute commands before a later one is rejected, so its
/// rejected resend still counts as attempted.
/// </remarks>
internal static class LogicalCommandRetry
{
    internal readonly struct Attempt(int number, CancellationToken token, CommandDeadline deadline)
    {
        /// <summary>One for the initial send.</summary>
        internal int Number { get; } = number;
        /// <summary>The logical cancellation and deadline budget shared by all attempts.</summary>
        internal CancellationToken Token { get; } = token;
        internal CommandDeadline Deadline { get; } = deadline;
    }

    internal static CommandDeadline CreateDeadline(TimeSpan? commandTimeout)
        => commandTimeout is { } timeout
            ? CommandDeadline.After(Math.Max(1L, (long)timeout.TotalMilliseconds))
            : CommandDeadline.None;

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal static async ValueTask<TResult> RunAsync<TState, TResult>(
        CommandRetryPolicy policy,
        string operation,
        RespireCommandRetryCategory category,
        TimeSpan? commandTimeout,
        TState state,
        Func<TState, Attempt, ValueTask<TResult>> send,
        RespireTelemetry.ErrorObservation observation,
        CancellationToken callerToken,
        bool rejectionProvesUnexecuted = true)
    {
        callerToken.ThrowIfCancellationRequested();
        var deadline = CreateDeadline(commandTimeout);
        using var timeout = deadline.IsSet
            ? CommandTimeoutCancellation.Create(callerToken, TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
            : null;
        var token = timeout?.Token ?? callerToken;
        var retries = 0;
        Exception? finalError = null;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                ThrowIfStopped(operation, commandTimeout, token, deadline);
                try
                {
                    var result = await send(state, new Attempt(attempt, token, deadline)).ConfigureAwait(false);
                    RecordAttempted(category, attempt, ref retries);
                    if (retries != 0) RespireTelemetry.RecordCommandRetry(category, "succeeded");
                    return result;
                }
                catch (Exception error)
                {
                    // Circuit rejection is never a selected failure, so it always ends the operation.
                    if (!rejectionProvesUnexecuted || error is not RespireCircuitOpenException)
                        RecordAttempted(category, attempt, ref retries);
                    var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
                    if (!policy.TryGetDelay(error, CommandWriteOutcome.Unknown, category, attempt, remaining, token, out var delay))
                        throw;
                    observation.Handled(error);
                    if (delay != TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                    ThrowIfStopped(operation, commandTimeout, token, deadline);
                }
            }
        }
        catch (OperationCanceledException error) when (callerToken.IsCancellationRequested
            && error.CancellationToken == token && token != callerToken)
        {
            finalError = new OperationCanceledException(error.Message, error, callerToken);
            throw finalError;
        }
        catch (OperationCanceledException error) when (timeout is not null && timeout.IsCancellationRequested
            && error.CancellationToken == token)
        {
            finalError = new RespireTimeoutException(operation, commandTimeout!.Value, error);
            throw finalError;
        }
        catch (Exception error) { finalError = error; throw; }
        finally
        {
            if (finalError is not null && retries != 0)
                RespireTelemetry.RecordCommandRetry(category, "exhausted");
        }
    }

    // Without transport evidence, a resend that the owner cannot prove unexecuted is the attempted
    // retry. It is recorded once the resend finishes, so a circuit rejection that ends a
    // single-command dispatch on the route the retry selected is not reported as another attempt.
    private static void RecordAttempted(RespireCommandRetryCategory category, int attempt, ref int retries)
    {
        if (attempt == 1) return;
        retries++;
        RespireTelemetry.RecordCommandRetry(category, "attempted");
    }

    private static void ThrowIfStopped(string operation, TimeSpan? commandTimeout, CancellationToken token, CommandDeadline deadline)
    {
        token.ThrowIfCancellationRequested();
        if (deadline.IsSet && deadline.RemainingMilliseconds == 0)
            throw new RespireTimeoutException(operation, commandTimeout!.Value);
    }
}
