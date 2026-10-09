namespace Respire.Internal;

// Created only in opt-in mode. Prefix and cache-bypass views share endpoint state.
internal sealed class StandaloneCircuitRegistry(RespireCircuitBreakerOptions options,
    Func<RespireEndpoint>? getCurrentEndpoint = null, Func<RespireEndpoint, bool>? isCurrentEndpoint = null)
{
    internal const int RetainedEndpointLimit = 16;
    private readonly Lock _gate = new();
    // Ordinary standalone clients reuse their configured host (not each resolved IP).
    // Retain current and in-flight endpoint state; trim only idle maintenance history.
    private readonly Dictionary<RespireEndpoint, Entry> _circuits = new(RespireEndpointComparer.Instance);
    private long _lastUse;

    internal sealed class Entry(EndpointCircuitBreaker circuit)
    {
        internal EndpointCircuitBreaker Circuit { get; } = circuit;
        internal int ActiveAdmissions;
        internal long LastUse;
    }

    internal CircuitAdmission Acquire(RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_circuits.TryGetValue(endpoint, out var entry))
                _circuits.Add(endpoint, entry = new(new(endpoint, options)));
            entry.LastUse = ++_lastUse;
            if (!entry.Circuit.TryAcquire(out var permit, out var retryAfter))
                throw new RespireCircuitOpenException(endpoint, retryAfter);
            entry.ActiveAdmissions++;
            TrimInactive();
            return new(permit, this, entry);
        }
    }

    internal void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.ActiveAdmissions--;
            TrimInactive();
        }
    }

    private void TrimInactive()
    {
        if (_circuits.Count <= RetainedEndpointLimit) return;
        var current = getCurrentEndpoint?.Invoke();
        while (_circuits.Count > RetainedEndpointLimit)
        {
            Entry? oldest = null;
            foreach (var entry in _circuits.Values)
            {
                if (entry.ActiveAdmissions != 0) continue;
                if (current is { } endpoint && RespireEndpointComparer.Instance.Equals(entry.Circuit.Endpoint, endpoint)
                    || isCurrentEndpoint?.Invoke(entry.Circuit.Endpoint) == true) continue;
                if (oldest is null || entry.LastUse < oldest.LastUse) oldest = entry;
            }
            // Active work owns its state until completion; it cannot be evicted to meet a cap.
            if (oldest is null) return;
            _circuits.Remove(oldest.Circuit.Endpoint);
        }
    }

    internal int CountForTests { get { lock (_gate) return _circuits.Count; } }

    internal EndpointCircuitBreaker GetForTests(RespireEndpoint endpoint)
    {
        lock (_gate) return _circuits[endpoint].Circuit;
    }
}

internal struct CircuitAdmission(CircuitPermit permit, StandaloneCircuitRegistry? registry = null,
    StandaloneCircuitRegistry.Entry? entry = null) : IDisposable
{
    // Move ownership once into CircuitStreamCompletion, or keep this mutable local.
    // Do not copy an admission: its outcome and one-shot permit must stay together.
    private CircuitPermit _permit = permit;
    private readonly StandaloneCircuitRegistry? _registry = registry;
    private readonly StandaloneCircuitRegistry.Entry? _entry = entry;
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

    public void Dispose()
    {
        if (_permit.Owner is not { } owner) return;
        owner.Complete(ref _permit, _outcome);
        // Complete clears this mutable permit. Repeated Dispose cannot release twice.
        _registry?.Release(_entry!);
    }
}
