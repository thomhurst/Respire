namespace Respire.Internal;

/// <summary>Reserves seed discovery time independently of the number of discovered primaries.</summary>
internal sealed class ClusterRecoveryBudget : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly TimeSpan _duration;
    private readonly CancellationTokenSource _round;
    private readonly CancellationTokenSource _primaries;
    private CancellationTokenSource? _earlySeeds;
    private readonly CancellationTokenSource? _roundDeadline;
    private readonly CancellationTokenSource? _primaryDeadline;
    private CancellationTokenSource? _earlySeedDeadline;

    internal ClusterRecoveryBudget(CancellationToken callerToken, TimeSpan duration, TimeProvider clock)
    {
        _clock = clock;
        _started = clock.GetTimestamp();
        _duration = duration;
        _round = CreateDeadline(callerToken, duration, out _roundDeadline);
        _primaries = CreateDeadline(_round.Token,
            TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 2)), out _primaryDeadline);
    }

    internal CancellationToken Token => _round.Token;
    internal CancellationToken PrimaryToken => _primaries.Token;

    internal bool IsCallerCancellation(OperationCanceledException error, CancellationToken callerToken)
        => CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, Token)
            || CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, PrimaryToken)
            || (_earlySeeds is not null
                && CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, _earlySeeds.Token));

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
            var remaining = _duration - _clock.GetElapsedTime(_started);
            _earlySeeds = CreateDeadline(Token,
                TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 2)), out _earlySeedDeadline);
        }
        return _earlySeeds.Token;
    }

    public void Dispose()
    {
        _earlySeeds?.Dispose();
        _primaries.Dispose();
        _round.Dispose();
        _earlySeedDeadline?.Dispose();
        _primaryDeadline?.Dispose();
        _roundDeadline?.Dispose();
    }

    private CancellationTokenSource CreateDeadline(CancellationToken parent, TimeSpan duration,
        out CancellationTokenSource? deadline)
    {
        if (ReferenceEquals(_clock, TimeProvider.System))
        {
            deadline = null;
            return CommandTimeoutCancellation.Create(parent, duration);
        }

        deadline = new CancellationTokenSource(duration, _clock);
        return CancellationTokenSource.CreateLinkedTokenSource(parent, deadline.Token);
    }
}
