namespace Respire.Infrastructure;

/// <summary>State for the latest-wins MOVING handoff worker.</summary>
/// <remarks>Call state methods only while holding <see cref="Gate"/>.</remarks>
internal sealed class MovingHandoffCoordinator
{
    internal readonly record struct QueueResult(bool MarkConnectionSequence, bool StartWorker,
        bool HasSupersededEndpoint, RespireEndpoint SupersededEndpoint);

    internal sealed record Request(RespireEndpoint Endpoint, long Deadline, CancellationTokenSource Cancellation);

    private readonly Dictionary<(string Host, int Port), (long Sequence, long ExpiresAt)> _sequences = new();
    private Request? _pending;
    private Request? _active;
    private bool _worker;
    private TaskCompletionSource? _workerCompletion;
    private int _activeDrains;
    private TaskCompletionSource? _drainsIdle;
    private long _handoffEpoch;

    internal Lock Gate { get; } = new();

    internal bool HasSequenceFences
    {
        get
        {
            AssertGateHeld();
            return _sequences.Count != 0;
        }
    }

    internal long HandoffEpoch => Volatile.Read(ref _handoffEpoch);

    internal bool IsCurrent(long publicationGeneration, long connectionGeneration, long announcementEpoch)
        => publicationGeneration >= 0 && publicationGeneration == connectionGeneration
            && HandoffEpoch - announcementEpoch <= 1;

    internal QueueResult Queue(bool operational, bool current, long lastConnectionSequence,
        (string Host, int Port) peer, long sequence, RespireEndpoint endpoint, long deadline,
        long now, CancellationToken stopConnecting)
    {
        AssertGateHeld();
        if (!operational || !current || sequence <= lastConnectionSequence)
            return default;

        if (_sequences.TryGetValue(peer, out var seen) && sequence <= seen.Sequence
            && now < seen.ExpiresAt)
            return new(MarkConnectionSequence: true, StartWorker: false, HasSupersededEndpoint: false, default);

        _sequences[peer] = (sequence, deadline);
        var hasSupersededEndpoint = _active is { Cancellation.IsCancellationRequested: false };
        var supersededEndpoint = hasSupersededEndpoint ? _active!.Endpoint : default;
        _active?.Cancellation.Cancel();
        if (_pending is { } pending)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }
        _pending = new Request(endpoint, deadline, CancellationTokenSource.CreateLinkedTokenSource(stopConnecting));
        if (_worker)
            return new(MarkConnectionSequence: true, StartWorker: false, hasSupersededEndpoint, supersededEndpoint);

        _worker = true;
        _workerCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new(MarkConnectionSequence: true, StartWorker: true, hasSupersededEndpoint, supersededEndpoint);
    }

    /// <summary>Moves newest pending request to active, or ends worker when queue is empty/retired.</summary>
    internal Request? TakeNext(bool operational)
    {
        AssertGateHeld();
        if (!operational || _pending is null)
        {
            if (_pending is { } pending)
            {
                pending.Cancellation.Cancel();
                pending.Cancellation.Dispose();
            }
            _pending = null;
            _active = null;
            _worker = false;
            _workerCompletion?.TrySetResult();
            _workerCompletion = null;
            return null;
        }

        _active = _pending;
        _pending = null;
        return _active;
    }

    internal void Complete(Request request)
    {
        AssertGateHeld();
        if (ReferenceEquals(_active, request)) _active = null;
        request.Cancellation.Dispose();
    }

    internal bool HasPending
    {
        get
        {
            AssertGateHeld();
            return _pending is not null;
        }
    }

    internal Task? WorkerCompletion
    {
        get
        {
            AssertGateHeld();
            return _workerCompletion?.Task;
        }
    }

    internal long PublishHandoffEpoch() => Interlocked.Increment(ref _handoffEpoch);

    internal void BeginDrain()
    {
        AssertGateHeld();
        if (_activeDrains++ == 0 && _drainsIdle?.Task.IsCompleted == true)
            _drainsIdle = null;
    }

    internal void EndDrain()
    {
        AssertGateHeld();
        if (--_activeDrains == 0) _drainsIdle?.TrySetResult();
    }

    internal Task WaitForDrains()
    {
        AssertGateHeld();
        return _activeDrains == 0 ? Task.CompletedTask
            : (_drainsIdle ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    internal void ForgetSequences(IReadOnlySet<(string Host, int Port)> livePeers)
    {
        AssertGateHeld();
        if (_sequences.Count == 0) return;
        foreach (var peer in _sequences.Keys.ToArray())
        {
            if (!livePeers.Contains(peer)) _sequences.Remove(peer);
        }
    }

    internal bool HasSequenceFence((string Host, int Port) peer)
    {
        AssertGateHeld();
        return _sequences.ContainsKey(peer);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertGateHeld()
        => System.Diagnostics.Debug.Assert(Gate.IsHeldByCurrentThread,
            "MovingHandoffCoordinator state must be accessed while holding Gate.");
}
