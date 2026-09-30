using System.Diagnostics;

namespace Respire.Internal;

/// <summary>Reserves seed discovery time independently of the number of discovered primaries.</summary>
internal sealed class ClusterRecoveryBudget : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _duration;
    private readonly CancellationTokenSource _round;
    private readonly CancellationTokenSource _primaries;
    private CancellationTokenSource? _earlySeeds;

    internal ClusterRecoveryBudget(CancellationToken callerToken, TimeSpan duration)
    {
        _duration = duration;
        _round = CommandTimeoutCancellation.Create(callerToken, duration);
        _primaries = CommandTimeoutCancellation.Create(_round.Token,
            TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 2)));
    }

    internal CancellationToken Token => _round.Token;
    internal CancellationToken PrimaryToken => _primaries.Token;

    internal CancellationToken GetFallbackToken(bool last)
    {
        if (last)
        {
            return Token;
        }

        // All earlier seeds share one phase. Reserve half of the time left when seed
        // discovery begins for the final configured seed, regardless of list length.
        if (_earlySeeds is null)
        {
            var remaining = _duration - Stopwatch.GetElapsedTime(_started);
            _earlySeeds = CommandTimeoutCancellation.Create(Token,
                TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 2)));
        }
        return _earlySeeds.Token;
    }

    public void Dispose()
    {
        _earlySeeds?.Dispose();
        _primaries.Dispose();
        _round.Dispose();
    }
}
