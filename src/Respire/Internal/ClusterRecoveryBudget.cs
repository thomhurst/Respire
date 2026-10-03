namespace Respire.Internal;

/// <summary>Reserves seed discovery time independently of the number of discovered primaries.</summary>
internal sealed class ClusterRecoveryBudget : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly TimeSpan _duration;
    private readonly Deadline _round;
    private readonly Deadline _primaries;
    private Deadline? _earlySeeds;

    internal ClusterRecoveryBudget(CancellationToken callerToken, TimeSpan duration, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        _duration = duration;
        _round = new Deadline(callerToken, duration, _clock);
        _primaries = new Deadline(_round.Token,
            TimeSpan.FromTicks(Math.Max(1, duration.Ticks / 2)), _clock);
    }

    internal CancellationToken Token => _round.Token;
    internal CancellationToken PrimaryToken => _primaries.Token;

    internal bool IsCallerCancellation(OperationCanceledException error, CancellationToken callerToken)
        => CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, Token)
            || CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, PrimaryToken)
            || (_earlySeeds is not null
                && CommandTimeoutCancellation.IsFromLinkedToken(error, callerToken, _earlySeeds.Value.Token));

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
            _earlySeeds = new Deadline(Token,
                TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 2)), _clock);
        }
        return _earlySeeds.Value.Token;
    }

    public void Dispose()
    {
        _earlySeeds?.Dispose();
        _primaries.Dispose();
        _round.Dispose();
    }

    private readonly struct Deadline : IDisposable
    {
        private readonly CancellationTokenSource _linked;
        private readonly CancellationTokenSource? _timeout;

        internal Deadline(CancellationToken parent, TimeSpan duration, TimeProvider clock)
        {
            // Keep the existing pooled cancellation path for production's system clock.
            if (ReferenceEquals(clock, TimeProvider.System))
            {
                _timeout = null;
                _linked = CommandTimeoutCancellation.Create(parent, duration);
            }
            else
            {
                _timeout = new CancellationTokenSource(duration, clock);
                _linked = CancellationTokenSource.CreateLinkedTokenSource(parent, _timeout.Token);
            }
        }

        internal CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timeout?.Dispose();
        }
    }
}
