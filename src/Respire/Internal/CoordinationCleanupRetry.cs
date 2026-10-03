using System.Diagnostics;

namespace Respire.Internal;

internal static class CoordinationCleanupRetry
{
    // Spreads retries from clients that lost the same connection at the same moment.
    internal static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    internal static TimeSpan NextDelay(TimeSpan delay, TimeSpan maximumDelay)
        => TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maximumDelay.Ticks));

    internal static async Task<bool> RunAsync(long started, Func<ValueTask<CleanupAttemptResult>> attempt,
        CleanupRetryPolicy policy, Func<bool>? shouldContinue = null, Action<string>? onAbandoned = null)
    {
        var delay = policy.InitialDelay;
        while (shouldContinue is null || shouldContinue())
        {
            var outcome = await attempt().ConfigureAwait(false);
            if (outcome == CleanupAttemptResult.Succeeded) return true;
            if (outcome == CleanupAttemptResult.Abandoned)
            {
                onAbandoned?.Invoke("client_disposed");
                return false;
            }
            if (Stopwatch.GetElapsedTime(started) >= policy.Limit)
            {
                onAbandoned?.Invoke("exhausted");
                return false;
            }
            await Task.Delay(WithJitter(delay)).ConfigureAwait(false);
            delay = NextDelay(delay, policy.MaximumDelay);
        }
        return false;
    }
}
