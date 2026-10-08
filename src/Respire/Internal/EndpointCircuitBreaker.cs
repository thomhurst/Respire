namespace Respire.Internal;

internal enum EndpointCircuitState { Closed, Open, HalfOpen }
internal enum CircuitOutcome { Success, Failure, Ignored }
internal readonly record struct CircuitPermit(EndpointCircuitBreaker? Owner, long Generation, int ProbeSlot, long PermitId,
    EndpointCircuitBreaker.CompletionTicket? Ticket = null);
internal readonly record struct CircuitSnapshot(EndpointCircuitState State, int SampleCount, int FailureCount, int ActiveProbes, int SuccessfulProbes);

// Ticket identities make completion idempotent even when a caller copies a permit.
internal sealed class EndpointCircuitBreaker
{
    private readonly Lock _gate = new();
    private readonly RespireCircuitBreakerOptions _options;
    private readonly TimeProvider _clock;
    private readonly Sample[] _samples;
    private readonly long[] _probes;
    private readonly int[] _freeProbeSlots;
    private int _freeProbeCount;
    private CompletionTicket? _freeTickets = new();
    private int _freeTicketCount = 1;
    private EndpointCircuitState _state;
    private int _head, _sampleCount, _failureCount, _activeProbes, _successfulProbes;
    private long _generation, _openedAt, _nextPermitId;

    public EndpointCircuitBreaker(RespireEndpoint endpoint, RespireCircuitBreakerOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Endpoint = endpoint;
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _samples = new Sample[options.MaximumSampleCount];
        _probes = new long[options.HalfOpenProbeCount];
        _freeProbeSlots = new int[options.HalfOpenProbeCount];
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
            if (_state == EndpointCircuitState.Closed)
            {
                var now = _clock.GetTimestamp();
                TrimHistory(now);
                OpenIfThresholdReached(now);
            }
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
                var ticket = _freeTickets;
                if (ticket is null) ticket = new CompletionTicket();
                else
                {
                    _freeTickets = ticket.Next;
                    _freeTicketCount--;
                    ticket.Next = null;
                }
                ticket.Id = NextPermitId();
                permit = new(this, _generation, -1, ticket.Id, ticket);
                return true;
            }
            if (_successfulProbes + _activeProbes == _probes.Length) return false;
            var slot = _freeProbeSlots[--_freeProbeCount];
            _probes[slot] = NextPermitId();
            _activeProbes++;
            permit = new(this, _generation, slot, _nextPermitId);
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
            if (completed.Ticket is { } ticket)
            {
                if (ticket.Id != completed.PermitId) return;
                ticket.Id = 0;
                // Outstanding permits own overflow tickets; the endpoint retains only a bounded idle pool.
                if (_freeTicketCount < _samples.Length)
                {
                    ticket.Next = _freeTickets;
                    _freeTickets = ticket;
                    _freeTicketCount++;
                }
            }
            if (completed.Generation != _generation) return;
            if (_state == EndpointCircuitState.HalfOpen)
            {
                if (completed.ProbeSlot < 0 || _probes[completed.ProbeSlot] != completed.PermitId) return;
                _probes[completed.ProbeSlot] = 0;
                _freeProbeSlots[_freeProbeCount++] = completed.ProbeSlot;
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
            OpenIfThresholdReached(now);
        }
    }

    private void OpenIfThresholdReached(long now)
    {
        if (_failureCount >= _options.MinimumFailureCount
            && (double)_failureCount / _sampleCount >= _options.FailureRateThreshold)
            Open(now);
    }

    private long NextPermitId()
    {
        // Zero denotes a consumed ticket or free probe slot, including when the counter wraps.
        if (++_nextPermitId == 0) ++_nextPermitId;
        return _nextPermitId;
    }

    private void Open(long now)
    {
        _state = EndpointCircuitState.Open;
        _openedAt = now;
        _generation++;
        _activeProbes = _successfulProbes = 0;
        Array.Clear(_probes);
        for (var i = 0; i < _freeProbeSlots.Length; i++) _freeProbeSlots[i] = i;
        _freeProbeCount = _freeProbeSlots.Length;
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

    internal sealed class CompletionTicket
    {
        public long Id;
        public CompletionTicket? Next;
    }
}
