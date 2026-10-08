namespace Respire.Internal;

internal enum EndpointCircuitState { Closed, Open, HalfOpen }
internal enum CircuitOutcome { Success, Failure, Ignored }
internal readonly record struct CircuitPermit(EndpointCircuitBreaker? Owner, long Generation, int ProbeSlot, long ProbeId);
internal readonly record struct CircuitSnapshot(EndpointCircuitState State, int SampleCount, int FailureCount, int ActiveProbes, int SuccessfulProbes);

// Admissions have one completion owner. Passing the permit by ref consumes that owner's ticket.
internal sealed class EndpointCircuitBreaker
{
    private readonly Lock _gate = new();
    private readonly RespireCircuitBreakerOptions _options;
    private readonly TimeProvider _clock;
    private readonly Sample[] _samples;
    private readonly long[] _probes;
    private EndpointCircuitState _state;
    private int _head, _sampleCount, _failureCount, _activeProbes, _successfulProbes;
    private long _generation, _openedAt, _nextProbeId;

    public EndpointCircuitBreaker(RespireEndpoint endpoint, RespireCircuitBreakerOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Endpoint = endpoint;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _samples = new Sample[options.MaximumSampleCount];
        _probes = new long[options.HalfOpenProbeCount];
    }

    public RespireEndpoint Endpoint { get; }

    public CircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            TrimHistory(_clock.GetTimestamp());
            return new(_state, _sampleCount, _failureCount, _activeProbes, _successfulProbes);
        }
    }

    public bool TryAcquire(out CircuitPermit permit, out TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            permit = default;
            retryAfter = null;
            if (_state == EndpointCircuitState.Open)
            {
                var elapsed = _clock.GetElapsedTime(_openedAt, _clock.GetTimestamp());
                if (elapsed < _options.OpenDuration)
                {
                    retryAfter = _options.OpenDuration - elapsed;
                    return false;
                }
                _state = EndpointCircuitState.HalfOpen;
                _generation++;
                ClearHistory();
            }
            if (_state == EndpointCircuitState.Closed)
            {
                permit = new(this, _generation, -1, 0);
                return true;
            }
            if (_successfulProbes + _activeProbes == _probes.Length) return false;
            var slot = Array.IndexOf(_probes, 0L);
            // Zero denotes a free slot, including if the identity counter eventually wraps.
            if (++_nextProbeId == 0) ++_nextProbeId;
            _probes[slot] = _nextProbeId;
            _activeProbes++;
            permit = new(this, _generation, slot, _nextProbeId);
            return true;
        }
    }

    public void Complete(ref CircuitPermit permit, CircuitOutcome outcome)
    {
        if (outcome is not (CircuitOutcome.Success or CircuitOutcome.Failure or CircuitOutcome.Ignored))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (permit.Owner is null) return;
        if (!ReferenceEquals(permit.Owner, this)) throw new ArgumentException("The permit belongs to another circuit.", nameof(permit));
        var completed = permit;
        permit = default;
        lock (_gate)
        {
            if (completed.Generation != _generation) return;
            if (_state == EndpointCircuitState.HalfOpen)
            {
                if (completed.ProbeSlot < 0 || _probes[completed.ProbeSlot] != completed.ProbeId) return;
                _probes[completed.ProbeSlot] = 0;
                _activeProbes--;
                if (outcome == CircuitOutcome.Failure)
                {
                    Open(_clock.GetTimestamp());
                }
                else if (outcome == CircuitOutcome.Success && ++_successfulProbes == _probes.Length)
                {
                    _state = EndpointCircuitState.Closed;
                    _generation++;
                    _successfulProbes = 0;
                }
                return;
            }
            if (_state != EndpointCircuitState.Closed || outcome == CircuitOutcome.Ignored) return;
            var now = _clock.GetTimestamp();
            TrimHistory(now);
            if (_sampleCount == _samples.Length) RemoveOldest();
            var failure = outcome == CircuitOutcome.Failure;
            _samples[(_head + _sampleCount) % _samples.Length] = new(now, failure);
            _sampleCount++;
            if (failure) _failureCount++;
            if (_failureCount >= _options.MinimumFailureCount
                && (double)_failureCount / _sampleCount >= _options.FailureRateThreshold)
                Open(now);
        }
    }

    private void Open(long now)
    {
        _state = EndpointCircuitState.Open;
        _openedAt = now;
        _generation++;
        _activeProbes = _successfulProbes = 0;
        Array.Clear(_probes);
    }

    private void TrimHistory(long now)
    {
        while (_sampleCount != 0 && _clock.GetElapsedTime(_samples[_head].Timestamp, now) >= _options.SamplingWindow)
            RemoveOldest();
    }

    private void RemoveOldest()
    {
        if (_samples[_head].Failure) _failureCount--;
        _head = (_head + 1) % _samples.Length;
        _sampleCount--;
    }

    private void ClearHistory() => _head = _sampleCount = _failureCount = 0;
    private readonly record struct Sample(long Timestamp, bool Failure);
}
