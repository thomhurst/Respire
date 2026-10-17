namespace Respire;

/// <summary>Opt-in, Redis-backed cache-aside stampede protection.</summary>
/// <remarks>All callers of a key must agree on its type, expiry and options.</remarks>
public sealed record RespireCacheAsideOptions
{
    /// <summary>Coordinate factories across clients using a token-owned SET NX PX lease. Defaults to true.</summary>
    public bool DistributedSingleFlight { get; init; } = true;

    /// <summary>XFetch early-refresh factor. Zero disables early refresh; one is a typical starting point.</summary>
    public double EarlyRefreshBeta { get; init; }

    /// <summary>Maximum lease lifetime. Factories must finish within this duration. Defaults to 30 seconds.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum contention wait before TimeoutException. Does not bound factory work. Defaults to 5 seconds.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay between contention attempts. Defaults to 25 milliseconds.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>Retain expired values for this extra duration. Contending readers may return them while another caller refreshes.</summary>
    /// <remarks>The elected caller awaits its factory; no background work is started. Defaults to zero.</remarks>
    public TimeSpan StaleWhileRevalidate { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(EarlyRefreshBeta) || EarlyRefreshBeta < 0)
            throw new ArgumentOutOfRangeException(nameof(EarlyRefreshBeta));
        ValidateDuration(LockTimeout, nameof(LockTimeout), allowZero: false);
        ValidateDuration(WaitTimeout, nameof(WaitTimeout), allowZero: true);
        ValidateDuration(RetryDelay, nameof(RetryDelay), allowZero: false);
        ValidateDuration(StaleWhileRevalidate, nameof(StaleWhileRevalidate), allowZero: true);
        if (StaleWhileRevalidate > TimeSpan.Zero && !DistributedSingleFlight)
            throw new ArgumentException("StaleWhileRevalidate requires DistributedSingleFlight.");
    }

    private static void ValidateDuration(TimeSpan value, string name, bool allowZero)
    {
        // CancellationTokenSource and Task.Delay share this maximum timer duration.
        if (value.TotalMilliseconds < (allowZero ? 0 : 1) || value.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(name);
    }
}
