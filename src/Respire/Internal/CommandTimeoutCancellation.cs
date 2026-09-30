namespace Respire.Internal;

/// <summary>Creates timeout cancellation while avoiding unnecessary source allocations.</summary>
internal static class CommandTimeoutCancellation
{
    private const int MaxPoolSize = 4096;
    private static readonly Reservoir.CancellationTokenSourcePool Pool = new(MaxPoolSize);

    /// <summary>Identifies cancellation from an owned link when its initiating token was cancelled.</summary>
    /// <remarks>Independent link deadlines and unrelated tokens must keep their original identity.</remarks>
    internal static bool IsFromLinkedToken(OperationCanceledException error,
        CancellationToken initiatingToken, CancellationToken linkedToken)
        => initiatingToken.IsCancellationRequested && error.CancellationToken == linkedToken;

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
