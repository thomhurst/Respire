namespace Respire.Internal;

/// <summary>Creates timeout cancellation while avoiding unnecessary source allocations.</summary>
internal static class CommandTimeoutCancellation
{
    public static CancellationTokenSource Create(CancellationToken callerToken, TimeSpan timeout)
    {
        CancellationTokenSource source;
        if (callerToken.CanBeCanceled)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        }
        else
        {
            source = Reservoir.CancellationTokenSourcePool.Shared.Rent();
        }

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
