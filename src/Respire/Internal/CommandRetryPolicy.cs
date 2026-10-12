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
        delay = default;
        return CanRetry(failure, outcome, category, completedAttempts, remaining, cancellationToken)
            && TryGetDelay(completedAttempts, remaining, out delay);
    }

    // A complete unit (MULTI/EXEC) is resent only when every member category is individually
    // eligible for the unit's single write outcome. One unaudited member blocks the whole unit.
    internal bool TryGetUnitDelay(Exception failure, CommandWriteOutcome outcome, RetryCategorySet categories,
        int completedAttempts, TimeSpan remaining, CancellationToken cancellationToken, out TimeSpan delay)
    {
        delay = default;
        if (categories.IsEmpty || categories.Contains(RespireCommandRetryCategory.Never)) return false;
        for (var category = RespireCommandRetryCategory.Always; category <= RespireCommandRetryCategory.ServerAdmin; category++)
        {
            if (categories.Contains(category)
                && !CanRetry(failure, outcome, category, completedAttempts, remaining, cancellationToken)) return false;
        }
        return TryGetDelay(completedAttempts, remaining, out delay);
    }

    internal bool CanRetry(Exception failure, CommandWriteOutcome outcome, RespireCommandRetryCategory category,
        int completedAttempts, TimeSpan remaining, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(completedAttempts);
        if (cancellationToken.IsCancellationRequested || remaining <= TimeSpan.Zero
            || completedAttempts >= _settings.MaxAttempts
            || _settings.MaxCommandRetryCategory == RespireCommandRetryCategory.Never
            || category == RespireCommandRetryCategory.Never || !Enum.IsDefined(category)
            || !IsSelectedFailure(failure)) return false;

        // Unknown evidence has the same conservative risk as a submitted frame. Exception flags
        // and nested causes cannot upgrade either outcome to definitely unwritten.
        if (outcome is not (CommandWriteOutcome.Unknown or CommandWriteOutcome.DefinitelyUnwritten
            or CommandWriteOutcome.EffectMayHaveReachedServer)) return false;
        return outcome == CommandWriteOutcome.DefinitelyUnwritten || PermitsPossibleExecution(category);
    }

    private bool TryGetDelay(int completedAttempts, TimeSpan remaining, out TimeSpan delay)
    {
        var candidate = GetDelay(completedAttempts, Random.Shared.NextDouble());
        delay = candidate < remaining ? candidate : default;
        return candidate < remaining;
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
