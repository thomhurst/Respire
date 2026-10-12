using Respire.Networking;

namespace Respire.Internal;

// Decides eligibility only. The dispatch owner must finish the attempt, retain arguments and
// barriers, refresh its route, and check deadline/cancellation again before admitting another send.
internal sealed class CommandRetryPolicy
{
    private readonly RespireRetryPolicy _settings;

    internal CommandRetryPolicy(RespireRetryPolicy settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;
    }

    internal bool IsEnabled => _settings.MaxAttempts > 1
        && _settings.MaxCommandRetryCategory != RespireCommandRetryCategory.Never
        && _settings.RetryableErrors != RespireRetryableErrors.None;

    internal bool TryGetDelay(Exception failure, CommandWriteOutcome outcome, RespireCommandRetryCategory category,
        int completedAttempts, TimeSpan remaining, CancellationToken cancellationToken, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(completedAttempts);
        delay = default;
        if (!CanRetry(failure, outcome, category, completedAttempts, remaining, cancellationToken)) return false;

        var candidate = GetDelay(completedAttempts, Random.Shared.NextDouble());
        if (candidate >= remaining) return false;
        delay = candidate;
        return true;
    }

    internal bool CanRetry(Exception failure, CommandWriteOutcome outcome, RespireCommandRetryCategory category,
        int completedAttempts, TimeSpan remaining, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || remaining <= TimeSpan.Zero
            || completedAttempts >= _settings.MaxAttempts
            || _settings.MaxCommandRetryCategory == RespireCommandRetryCategory.Never
            || category == RespireCommandRetryCategory.Never || !Enum.IsDefined(category)
            || !IsSelectedFailure(failure)) return false;

        // Unknown evidence has the same conservative risk as a submitted frame. Exception flags
        // and nested causes cannot upgrade either outcome to definitely unwritten.
        if (outcome is not (CommandWriteOutcome.Unknown or CommandWriteOutcome.DefinitelyUnwritten
            or CommandWriteOutcome.EffectMayHaveReachedServer)) return false;
        if (outcome != CommandWriteOutcome.DefinitelyUnwritten && !PermitsPossibleExecution(category)) return false;

        return true;
    }

    private bool IsSelectedFailure(Exception failure)
    {
        var selection = failure switch
        {
            RespireAuthenticationException or RespireReconnectLimitException => RespireRetryableErrors.None,
            RespireConnectionException => RespireRetryableErrors.ConnectionLoss,
            RespireServerException server => server.Code switch
            {
                RespireErrorCodes.Loading => RespireRetryableErrors.Loading,
                RespireErrorCodes.TryAgain => RespireRetryableErrors.TryAgain,
                RespireErrorCodes.MasterDown => RespireRetryableErrors.MasterDown,
                RespireErrorCodes.ReadOnly => RespireRetryableErrors.ReadOnly,
                _ => RespireRetryableErrors.None,
            },
            // Circuit rejection is not a transport failure, even when IsCommandNotSubmitted is true.
            _ => RespireRetryableErrors.None,
        };
        return (selection & _settings.RetryableErrors) != 0;
    }

    private bool PermitsPossibleExecution(RespireCommandRetryCategory category)
        => category switch
        {
            RespireCommandRetryCategory.Always => true,
            RespireCommandRetryCategory.Connection => _settings.MaxCommandRetryCategory is
                RespireCommandRetryCategory.Connection or RespireCommandRetryCategory.ReadOnly
                or RespireCommandRetryCategory.WriteChecked or RespireCommandRetryCategory.WriteLastWins
                or RespireCommandRetryCategory.WriteAccumulating or RespireCommandRetryCategory.ServerAdmin,
            RespireCommandRetryCategory.ReadOnly => _settings.MaxCommandRetryCategory is
                RespireCommandRetryCategory.ReadOnly or RespireCommandRetryCategory.WriteChecked
                or RespireCommandRetryCategory.WriteLastWins or RespireCommandRetryCategory.WriteAccumulating
                or RespireCommandRetryCategory.ServerAdmin,
            RespireCommandRetryCategory.WriteChecked => _settings.MaxCommandRetryCategory is
                RespireCommandRetryCategory.WriteChecked or RespireCommandRetryCategory.WriteLastWins
                or RespireCommandRetryCategory.WriteAccumulating or RespireCommandRetryCategory.ServerAdmin,
            RespireCommandRetryCategory.WriteLastWins => _settings.MaxCommandRetryCategory is
                RespireCommandRetryCategory.WriteLastWins or RespireCommandRetryCategory.WriteAccumulating
                or RespireCommandRetryCategory.ServerAdmin,
            RespireCommandRetryCategory.WriteAccumulating => _settings.MaxCommandRetryCategory is
                RespireCommandRetryCategory.WriteAccumulating or RespireCommandRetryCategory.ServerAdmin,
            RespireCommandRetryCategory.ServerAdmin => _settings.MaxCommandRetryCategory == RespireCommandRetryCategory.ServerAdmin,
            _ => false,
        };

    // The deterministic seam verifies jitter endpoints without timing or statistical assertions.
    internal TimeSpan GetDelay(int completedAttempts, double sample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(completedAttempts);
        if (!double.IsFinite(sample) || sample is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(sample));
        var ceiling = Math.Min(_settings.MaxBackoff.Ticks,
            _settings.InitialBackoff.Ticks * Math.Pow(2, Math.Min(completedAttempts - 1, 63)));
        return TimeSpan.FromTicks((long)(ceiling * (1 - _settings.Jitter + _settings.Jitter * sample)));
    }
}
