namespace Respire.Internal;

/// <summary>Owns the lifecycle and coalescing rules for cluster discovery refresh work.</summary>
internal sealed class SharedRefreshCoordinator(TimeProvider clock, TimeSpan coalescingWindow)
{
    private readonly Lock _gate = new();
    private RefreshFlight? _current;
    private long _lastTopologyRefreshTimestamp;
    private bool _hasTopologyRefreshTimestamp;

    internal enum RefreshFlightKind
    {
        Topology,
        ReadOnly,
    }

    /// <summary>One physical refresh shared by every caller that joins it.</summary>
    internal sealed class RefreshFlight
    {
        private RefreshFlight(RefreshFlightKind kind, int slot, RespireEndpoint source,
            CancellationTokenSource? cancellation, IDisposable? discoveryLease)
        {
            Kind = kind;
            Slot = slot;
            Source = source;
            Cancellation = cancellation;
            DiscoveryLease = discoveryLease;
        }

        internal static RefreshFlight ForTopology() => new(RefreshFlightKind.Topology, -1, default, null, null);

        internal static RefreshFlight ForReadOnly(int slot, RespireEndpoint source,
            CancellationTokenSource cancellation, IDisposable? discoveryLease)
            => new(RefreshFlightKind.ReadOnly, slot, source, cancellation, discoveryLease);

        internal RefreshFlightKind Kind { get; }
        internal int Slot { get; }
        internal RespireEndpoint Source { get; }
        internal CancellationTokenSource? Cancellation { get; }
        internal IDisposable? DiscoveryLease { get; }
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> Task => Completion.Task;
        internal int Waiters;
        internal bool Completed;
        internal bool Abandoned;

        internal bool Repairs(int slot, RespireEndpoint source)
            => Kind == RefreshFlightKind.ReadOnly && Slot == slot && Source.Port == source.Port
                && string.Equals(Source.Host, source.Host, StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct Join(RefreshFlight Flight, bool Started, bool NeedsOwnSlotRecovery);

    internal Join JoinReadOnly(int slot, RespireEndpoint source,
        Func<(CancellationTokenSource Cancellation, IDisposable? DiscoveryLease)> createResources)
    {
        ArgumentNullException.ThrowIfNull(createResources);
        lock (_gate)
        {
            var started = _current is null;
            if (started)
            {
                var (cancellation, discoveryLease) = createResources();
                _current = RefreshFlight.ForReadOnly(slot, source, cancellation, discoveryLease);
            }
            var flight = _current!;
            flight.Waiters++;
            return new Join(flight, started, !started && !flight.Repairs(slot, source));
        }
    }

    /// <summary>Returns false when a recent successful full refresh answers this request.</summary>
    internal bool JoinTopology(bool allowRecentSuccessfulResult, out RefreshFlight flight, out bool started)
    {
        lock (_gate)
        {
            if (allowRecentSuccessfulResult && _current is null && _hasTopologyRefreshTimestamp
                && clock.GetElapsedTime(_lastTopologyRefreshTimestamp) < coalescingWindow)
            {
                flight = null!;
                started = false;
                return false;
            }

            started = _current is null;
            if (started) _current = RefreshFlight.ForTopology();
            flight = _current!;
            flight.Waiters++;
            return true;
        }
    }

    /// <summary>Unpublishes completed work before making its result visible to waiters.</summary>
    internal void Complete(RefreshFlight flight, bool result, Exception? failure)
    {
        lock (_gate)
        {
            flight.Completed = true;
            if (ReferenceEquals(_current, flight)) _current = null;
            if (failure is null && result && flight.Kind == RefreshFlightKind.Topology)
            {
                _lastTopologyRefreshTimestamp = clock.GetTimestamp();
                _hasTopologyRefreshTimestamp = true;
            }
        }

        if (failure is null) flight.Completion.TrySetResult(result);
        else flight.Completion.TrySetException(failure);
        flight.Cancellation?.Dispose();
        flight.DiscoveryLease?.Dispose();
    }

    /// <summary>Release a waiter and return cancellation to signal outside the state lock.</summary>
    internal CancellationTokenSource? ReleaseWaiter(RefreshFlight flight)
    {
        lock (_gate)
        {
            if (flight.Waiters > 0) flight.Waiters--;
            if (flight.Kind != RefreshFlightKind.ReadOnly || flight.Completed || flight.Waiters != 0) return null;
            flight.Abandoned = true;
            if (ReferenceEquals(_current, flight)) _current = null;
            return flight.Cancellation;
        }
    }

    internal bool IsPublished(RefreshFlight flight)
    {
        lock (_gate) return ReferenceEquals(_current, flight);
    }
}
