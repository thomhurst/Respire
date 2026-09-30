using System.Collections.ObjectModel;
using System.Diagnostics;
using Respire.Internal;

namespace Respire;

/// <summary>An independently operated standalone Redis deployment in a failover group.</summary>
/// <param name="Options">Connection settings for one standalone Redis endpoint.</param>
/// <param name="Priority">Lower values have higher priority. Equal priorities keep input order.</param>
public sealed record RespireFailoverCandidate(RespireOptions Options, int Priority = 0);

/// <summary>Health and failback settings for a standalone failover group.</summary>
public sealed record RespireFailoverGroupOptions
{
    /// <summary>Delay between health probe rounds. Defaults to one second.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum duration of one health probe. Defaults to two seconds.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive failed probes required to open an endpoint circuit. Defaults to two.</summary>
    public int FailureThreshold { get; init; } = 2;

    /// <summary>Time an open circuit waits before one recovery probe. Defaults to five seconds.</summary>
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time a recovered higher-priority endpoint must stay healthy before failback. Defaults to ten seconds.</summary>
    public TimeSpan FailbackGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (ProbeInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
        if (ProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        if (FailureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(FailureThreshold));
        if (CircuitOpenDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CircuitOpenDuration));
        if (FailbackGracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailbackGracePeriod));
    }
}

/// <summary>Current endpoint health and circuit state.</summary>
public sealed record RespireFailoverEndpointStatus(
    RespireEndpoint Endpoint,
    int Priority,
    bool IsHealthy,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil,
    string? LastErrorType);

/// <summary>Describes an active endpoint change.</summary>
public sealed record RespireFailoverSwitch(
    RespireEndpoint? PreviousEndpoint,
    RespireEndpoint? CurrentEndpoint,
    string Reason,
    DateTimeOffset ChangedAt);

/// <summary>
/// Maintains standalone Redis clients and selects a healthy endpoint for new work.
/// Read <see cref="ActiveClient"/> for each new operation so callers observe endpoint changes.
/// </summary>
/// <remarks>
/// The group owns every candidate client until disposal. A caller that already obtained an
/// <see cref="ActiveClient"/> keeps using that deployment; the group never replays that call.
/// Subscriptions remain attached to their original client and must be recreated after a switch.
/// Independent Redis deployments do not share state or replication guarantees. Writes can reach
/// both deployments during detection and failback windows; applications must account for this.
/// Client-side caching is not supported because independent deployment caches can become stale.
/// </remarks>
public sealed class RespireFailoverGroup : IAsyncDisposable
{
    private readonly CandidateState[] _candidates;
    private readonly RespireFailoverGroupOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _monitor;
    private CandidateState? _active;
    private volatile bool _disposed;

    private RespireFailoverGroup(CandidateState[] candidates, RespireFailoverGroupOptions options)
    {
        _candidates = candidates;
        _options = options;
    }

    /// <summary>Raised when health policy changes the selected endpoint.</summary>
    public event Action<RespireFailoverSwitch>? EndpointSwitched;

    /// <summary>Whether the group currently has a healthy endpoint.</summary>
    public bool IsConnected => Volatile.Read(ref _active) is not null && !_disposed;

    /// <summary>The client selected for new operations. Read this property again after a switch.</summary>
    /// <exception cref="RespireConnectionException">No healthy endpoint is currently available.</exception>
    public IRespireClient ActiveClient
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Volatile.Read(ref _active)?.Client
                ?? throw new RespireConnectionException("No healthy Redis endpoint is available in the failover group.");
        }
    }

    /// <summary>Returns a snapshot of endpoint health and circuit state.</summary>
    public IReadOnlyList<RespireFailoverEndpointStatus> GetEndpointStatuses()
        => new ReadOnlyCollection<RespireFailoverEndpointStatus>(
            _candidates.Select(static candidate => candidate.Snapshot()).ToArray());

    /// <summary>Connects candidates and starts background health probes.</summary>
    public static async ValueTask<RespireFailoverGroup> ConnectAsync(
        IEnumerable<RespireFailoverCandidate> candidates,
        RespireFailoverGroupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var settings = options ?? new RespireFailoverGroupOptions();
        settings.Validate();

        var states = new List<CandidateState>();
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate is null) throw new ArgumentException("Failover candidates cannot contain null entries.", nameof(candidates));
                ArgumentNullException.ThrowIfNull(candidate.Options);
                var snapshot = candidate.Options.ValidateAndSnapshot();
                if (snapshot.Endpoints.Count != 1 || snapshot.UseCluster || !string.IsNullOrWhiteSpace(snapshot.SentinelPrimaryName))
                {
                    throw new RespireConfigurationException(
                        "Standalone failover candidates require exactly one endpoint and cannot enable Cluster or Sentinel mode.");
                }
                if (snapshot.ClientSideCache is not null)
                {
                    throw new RespireConfigurationException(
                        "Client-side caching is not supported across independent failover deployments.");
                }

                var client = RespireClient.Create(snapshot);
                states.Add(new CandidateState(client, candidate.Priority, states.Count));
            }

            if (states.Count == 0) throw new ArgumentException("At least one failover candidate is required.", nameof(candidates));

            var group = new RespireFailoverGroup(states.ToArray(), settings);
            await Task.WhenAll(states.Select(state => group.ProbeAsync(state, cancellationToken))).ConfigureAwait(false);
            await group.SelectActiveAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            if (Volatile.Read(ref group._active) is null)
            {
                throw new RespireConnectionException("Unable to connect to any standalone failover candidate.");
            }

            group._monitor = group.MonitorAsync();
            return group;
        }
        catch
        {
            foreach (var state in states) await state.Client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(_options.ProbeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    await Task.WhenAll(_candidates
                        .Where(candidate => candidate.CircuitOpenUntil is null || candidate.CircuitOpenUntil <= now)
                        .Select(candidate => ProbeAsync(candidate, _stop.Token))).ConfigureAwait(false);
                    await SelectActiveAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    RespireTelemetry.RecordFailoverMonitorError();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(CandidateState candidate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeTimeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await candidate.Client.PingAsync(timeout.Token).ConfigureAwait(false);
            candidate.MarkHealthy(DateTimeOffset.UtcNow);
            RespireTelemetry.RecordFailoverProbe(
                candidate.Endpoint,
                succeeded: true,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            candidate.MarkFailed(error, DateTimeOffset.UtcNow, _options);
            RespireTelemetry.RecordFailoverProbe(
                candidate.Endpoint,
                succeeded: false,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async ValueTask SelectActiveAsync(DateTimeOffset now)
    {
        RespireFailoverSwitch? change = null;
        await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var active = _active;
            var best = _candidates
                .Where(static candidate => candidate.IsHealthy)
                .OrderBy(static candidate => candidate.Priority)
                .ThenBy(static candidate => candidate.Order)
                .FirstOrDefault();

            CandidateState? selected = active;
            string? reason = null;
            if (active is null)
            {
                selected = best;
                reason = best is null ? null : "first-healthy";
            }
            else if (!active.IsHealthy)
            {
                selected = best;
                reason = best is null ? "no-healthy-endpoint" : "active-endpoint-unhealthy";
            }
            else if (best is not null && best.Order != active.Order && best.Priority < active.Priority
                && best.HealthySince is { } healthySince
                && now - healthySince >= _options.FailbackGracePeriod)
            {
                selected = best;
                reason = "higher-priority-endpoint-recovered";
            }

            if (!ReferenceEquals(selected, active))
            {
                Volatile.Write(ref _active, selected);
                change = new RespireFailoverSwitch(
                    active?.Endpoint,
                    selected?.Endpoint,
                    reason ?? "no-healthy-endpoint",
                    now);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (change is { } switched)
        {
            RespireTelemetry.RecordFailoverSwitch(switched.PreviousEndpoint, switched.CurrentEndpoint, switched.Reason);
            var handlers = EndpointSwitched;
            if (handlers is not null)
            {
                foreach (Action<RespireFailoverSwitch> handler in handlers.GetInvocationList())
                {
                    try { handler(switched); }
                    catch { RespireTelemetry.RecordFailoverMonitorError(); }
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _active, null);
            _stop.Cancel();
        }
        finally
        {
            _gate.Release();
        }

        if (_monitor is { } monitor)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        foreach (var candidate in _candidates) await candidate.Client.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class CandidateState(RespireClient client, int priority, int order)
    {
        private readonly object _gate = new();
        private bool _isHealthy;
        private int _consecutiveFailures;
        private DateTimeOffset? _circuitOpenUntil;
        private DateTimeOffset? _healthySince;
        private string? _lastErrorType;

        public RespireClient Client { get; } = client;
        public int Priority { get; } = priority;
        public int Order { get; } = order;
        public RespireEndpoint Endpoint => Client.Endpoint;
        public bool IsHealthy { get { lock (_gate) return _isHealthy; } }
        public int ConsecutiveFailures { get { lock (_gate) return _consecutiveFailures; } }
        public DateTimeOffset? CircuitOpenUntil { get { lock (_gate) return _circuitOpenUntil; } }
        public DateTimeOffset? HealthySince { get { lock (_gate) return _healthySince; } }

        public RespireFailoverEndpointStatus Snapshot()
        {
            lock (_gate)
            {
                return new RespireFailoverEndpointStatus(
                    Endpoint,
                    Priority,
                    _isHealthy,
                    _consecutiveFailures,
                    _circuitOpenUntil,
                    _lastErrorType);
            }
        }

        public void MarkHealthy(DateTimeOffset now)
        {
            lock (_gate)
            {
                if (!_isHealthy) _healthySince = now;
                _isHealthy = true;
                _consecutiveFailures = 0;
                _circuitOpenUntil = null;
                _lastErrorType = null;
            }
        }

        public void MarkFailed(Exception error, DateTimeOffset now, RespireFailoverGroupOptions options)
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= options.FailureThreshold)
                {
                    _isHealthy = false;
                    _healthySince = null;
                    _circuitOpenUntil = now + options.CircuitOpenDuration;
                }
                _lastErrorType = error.GetType().Name;
            }
        }
    }
}
