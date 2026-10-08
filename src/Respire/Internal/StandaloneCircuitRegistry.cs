namespace Respire.Internal;

// Created only in opt-in mode. Prefix and cache-bypass views share endpoint state.
internal sealed class StandaloneCircuitRegistry(RespireCircuitBreakerOptions options)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<RespireEndpoint, EndpointCircuitBreaker> _circuits = [];

    internal CircuitAdmission Acquire(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EndpointCircuitBreaker circuit;
        lock (_gate)
        {
            if (!_circuits.TryGetValue(endpoint, out circuit!))
                _circuits.Add(endpoint, circuit = new(endpoint, options));
        }
        if (!circuit.TryAcquire(out var permit, out var retryAfter))
            throw new RespireCircuitOpenException(endpoint, retryAfter);
        return new(permit);
    }

    internal EndpointCircuitBreaker GetForTests(RespireEndpoint endpoint)
    {
        lock (_gate) return _circuits[endpoint];
    }
}

internal struct CircuitAdmission(CircuitPermit permit) : IDisposable
{
    private CircuitPermit _permit = permit;
    private CircuitOutcome _outcome = CircuitOutcome.Ignored;

    internal void Success() => _outcome = CircuitOutcome.Success;

    internal void Failed(Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || error is OperationCanceledException
            || error is RespireException { IsCommandNotSubmitted: true }) return;
        _outcome = error switch
        {
            RespireAuthenticationException => CircuitOutcome.Ignored,
            RespireConnectionException or RespireProtocolException or RespireTimeoutException => CircuitOutcome.Failure,
            RespireServerException or RespireScriptingEngineUnavailableException => CircuitOutcome.Success,
            _ => CircuitOutcome.Ignored,
        };
    }

    public void Dispose() => _permit.Owner?.Complete(ref _permit, _outcome);
}
