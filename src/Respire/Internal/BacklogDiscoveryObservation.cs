namespace Respire.Internal;

// Cancellation releases a queued caller before discovery has necessarily unwound. Detach
// before returning its pooled observation, so late selector cleanup cannot touch another
// operation that subsequently rents the same storage. Only queued discovery allocates this.
internal sealed class BacklogDiscoveryObservation(RespireTelemetry.ErrorObservation caller) : IDispatchObservation
{
    private readonly Lock _gate = new();
    private RespireTelemetry.ErrorObservation _caller = caller;
    private bool _attached = true;

    internal RespireTelemetry.ErrorObservation Observation => new(this, 0);

    internal void Detach()
    {
        lock (_gate)
        {
            _attached = false;
            _caller = default;
        }
    }

    public bool IsOpen(long generation)
    {
        lock (_gate) return _attached && _caller.IsOpen;
    }

    public int Attempts(long generation)
    {
        lock (_gate) return _attached ? _caller.Attempts : 0;
    }

    public RespireEndpoint? Endpoint(long generation)
    {
        lock (_gate) return _attached ? _caller.Endpoint : null;
    }

    public void UseEndpoint(long generation, RespireEndpoint? endpoint)
    {
        lock (_gate)
            if (_attached) _caller.UseEndpoint(endpoint);
    }

    public void SetAttempts(long generation, int attempts)
    {
        lock (_gate)
            if (_attached) _caller.SetAttempts(attempts);
    }

    public bool Handled(long generation, Exception error, RespireEndpoint? endpoint, bool overrideEndpoint)
    {
        lock (_gate) return _attached && _caller.TryHandled(error, endpoint, overrideEndpoint);
    }

    public void Retry(long generation)
    {
        lock (_gate)
            if (_attached) _caller.Retry();
    }
}
