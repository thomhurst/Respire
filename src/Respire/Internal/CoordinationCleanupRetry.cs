namespace Respire.Internal;

internal static class CoordinationCleanupRetry
{
    // Spreads retries from clients that lost the same connection at the same moment.
    internal static TimeSpan WithJitter(TimeSpan delay)
        => TimeSpan.FromTicks((long)(delay.Ticks * (0.75 + Random.Shared.NextDouble() * 0.5)));

    internal static TimeSpan NextDelay(TimeSpan delay, TimeSpan maximumDelay)
        => TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maximumDelay.Ticks));
}
