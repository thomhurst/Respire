using Respire.Networking;

namespace Respire.Internal;

/// <summary>
/// Reuses the cancellation source and timer that enforce a command timeout. Successful commands
/// reset and pool the source; genuinely cancelled sources cannot be reset and are discarded.
/// </summary>
internal sealed class PooledCommandCancellation : IDisposable
{
    private const int MaxPoolSize = 4096;
    private static readonly LockFreeStack<PooledCommandCancellation> Pool = new(MaxPoolSize);

    private readonly CancellationTokenSource _source = new();
    private CancellationTokenRegistration _callerRegistration;

    public CancellationToken Token => _source.Token;

    public static PooledCommandCancellation Rent(CancellationToken callerToken, TimeSpan timeout)
    {
        if (!Pool.TryPop(out var cancellation))
        {
            cancellation = new PooledCommandCancellation();
        }

        try
        {
            if (callerToken.CanBeCanceled)
            {
                cancellation._callerRegistration = callerToken.UnsafeRegister(
                    static state => ((PooledCommandCancellation)state!)._source.Cancel(),
                    cancellation);
            }

            cancellation._source.CancelAfter(timeout);
            return cancellation;
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _callerRegistration.Dispose();
        _callerRegistration = default;
        _source.CancelAfter(Timeout.InfiniteTimeSpan);

        if (!_source.TryReset() || !Pool.TryPush(this))
        {
            _source.Dispose();
        }
    }
}
