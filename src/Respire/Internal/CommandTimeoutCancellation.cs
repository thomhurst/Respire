namespace Respire.Internal;

/// <summary>Creates timeout cancellation while avoiding unnecessary source allocations.</summary>
internal static class CommandTimeoutCancellation
{
    private const int MaxPoolSize = 4096;
    private static readonly Reservoir.CancellationTokenSourcePool Pool = new(MaxPoolSize);

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
