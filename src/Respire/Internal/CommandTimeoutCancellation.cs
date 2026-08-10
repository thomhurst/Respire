namespace Respire.Internal;

/// <summary>Creates timeout cancellation without linking an inert caller token.</summary>
internal static class CommandTimeoutCancellation
{
    public static CancellationTokenSource Create(CancellationToken callerToken, TimeSpan timeout)
    {
        if (!callerToken.CanBeCanceled)
        {
            return new CancellationTokenSource(timeout);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
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
