using System.Diagnostics;

namespace Respire.Internal;

/// <summary>Reserves seed discovery time independently of the number of discovered primaries.</summary>
internal sealed class ClusterRecoveryBudget : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _duration;
    private readonly CancellationTokenSource _round;
    private readonly CancellationTokenSource _primaries;

    internal ClusterRecoveryBudget(CancellationToken callerToken, TimeSpan duration)
    {
        _duration = duration;
        _round = CommandTimeoutCancellation.Create(callerToken, duration);
        _primaries = CommandTimeoutCancellation.Create(_round.Token,
            TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 2)));
    }

    internal CancellationToken Token => _round.Token;
    internal CancellationToken PrimaryToken => _primaries.Token;

    internal CancellationTokenSource CreateFallbackAttempt(bool last)
    {
        var remaining = _duration - Stopwatch.GetElapsedTime(_started);
        // Reclaim time from quick failures. The last fallback uses the entire remainder.
        var ticks = last ? remaining.Ticks : remaining.Ticks / 2;
        return CommandTimeoutCancellation.Create(Token, TimeSpan.FromTicks(Math.Max(1, ticks)));
    }

    public void Dispose()
    {
        _primaries.Dispose();
        _round.Dispose();
    }
}
