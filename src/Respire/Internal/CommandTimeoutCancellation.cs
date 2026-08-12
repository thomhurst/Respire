namespace Respire.Internal;

/// <summary>Creates timeout cancellation while avoiding unnecessary source allocations.</summary>
internal static class CommandTimeoutCancellation
{
    public static Lease Create(CancellationToken callerToken, TimeSpan timeout)
    {
#if NET10_0_OR_GREATER
        var source = Reservoir.CancellationTokenSourcePool.Shared.Rent();
        CancellationTokenRegistration callerRegistration = default;
        try
        {
            if (callerToken.CanBeCanceled)
            {
                callerRegistration = callerToken.UnsafeRegister(
                    static state => ((CancellationTokenSource)state!).Cancel(),
                    source);
            }

            source.CancelAfter(timeout);
            return new Lease(source, callerRegistration);
        }
        catch
        {
            callerRegistration.Dispose();
            source.Dispose();
            throw;
        }
#else
        if (!callerToken.CanBeCanceled)
        {
            return new Lease(new CancellationTokenSource(timeout));
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        try
        {
            source.CancelAfter(timeout);
            return new Lease(source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
#endif
    }

    internal struct Lease : IDisposable
    {
        private CancellationTokenSource? _source;
#if NET10_0_OR_GREATER
        private CancellationTokenRegistration _callerRegistration;

        public Lease(
            CancellationTokenSource source,
            CancellationTokenRegistration callerRegistration)
        {
            _source = source;
            _callerRegistration = callerRegistration;
        }
#else
        public Lease(CancellationTokenSource source)
        {
            _source = source;
        }
#endif

        public readonly CancellationToken Token
            => _source?.Token ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
#if NET10_0_OR_GREATER
            _callerRegistration.Dispose();
            _callerRegistration = default;
#endif
            var source = _source;
            _source = null;
            source?.Dispose();
        }
    }
}
