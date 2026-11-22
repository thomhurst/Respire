using Respire.Networking;

namespace Respire.Internal;

/// <summary>Creates timeout cancellation while avoiding unnecessary source allocations.</summary>
internal static class CommandTimeoutCancellation
{
    private const int MaxPoolSize = 4096;
    private const long MaximumTimerMilliseconds = uint.MaxValue - 1L;
    private static readonly Reservoir.CancellationTokenSourcePool Pool = new(MaxPoolSize);

    /// <summary>Identifies cancellation from an owned link when its initiating token was cancelled.</summary>
    /// <remarks>Independent link deadlines and unrelated tokens must keep their original identity.</remarks>
    internal static bool IsFromLinkedToken(OperationCanceledException error,
        CancellationToken initiatingToken, CancellationToken linkedToken)
        => initiatingToken.IsCancellationRequested && error.CancellationToken == linkedToken;

    /// <summary>Owns the timer for an absolute deadline and exposes its linked token.</summary>
    internal static IDisposable? CreateForDeadline(CancellationToken callerToken, CommandDeadline deadline,
        out CancellationToken token)
    {
        if (!deadline.IsSet)
        {
            token = callerToken;
            return null;
        }

        long remaining = deadline.RemainingMilliseconds;

        if (remaining <= MaximumTimerMilliseconds)
        {
            CancellationTokenSource source = Create(callerToken, TimeSpan.FromMilliseconds(Math.Max(1L, remaining)));
            token = source.Token;
            return source;
        }

        var cancellation = new DeadlineCancellation(callerToken);

        try
        {
            cancellation.Arm(deadline);
            token = cancellation.Token;
            return cancellation;
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    public static CancellationTokenSource Create(CancellationToken callerToken, TimeSpan timeout)
    {
        var source = Pool.RentLinked(callerToken);

        try
        {
            source.CancelAfter(timeout);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }
}
