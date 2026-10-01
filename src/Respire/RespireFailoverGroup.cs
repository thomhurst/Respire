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

    /// <summary>
    /// Maximum duration of one health probe. Defaults to two seconds. Probes share the candidate
    /// client's connection with application traffic, so a heavily loaded endpoint can miss probes.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Consecutive failed probes required to open an endpoint circuit. Defaults to two.</summary>
    public int FailureThreshold { get; init; } = 2;

    /// <summary>Time an open circuit waits before one recovery probe. Defaults to five seconds.</summary>
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time a recovered higher-priority endpoint must stay healthy before failback. Defaults to ten seconds.</summary>
    public TimeSpan FailbackGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (!IsTimerCompatible(ProbeInterval)) throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
        if (!IsTimerCompatible(ProbeTimeout)) throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        if (FailureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(FailureThreshold));
        if (CircuitOpenDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CircuitOpenDuration));
        if (FailbackGracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailbackGracePeriod));
    }

    // PeriodicTimer and CancellationTokenSource.CancelAfter reject periods above uint.MaxValue - 1 milliseconds.
    private static bool IsTimerCompatible(TimeSpan value)
        => value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1d;
}

/// <summary>Current endpoint health and circuit state.</summary>
public sealed record RespireFailoverEndpointStatus(
    RespireEndpoint Endpoint,
    int Priority,
    bool IsHealthy,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil,
    string? LastErrorType);

/// <summary>Reasons reported by <see cref="RespireFailoverSwitch.Reason"/>.</summary>
public static class RespireFailoverSwitchReasons
{
    /// <summary>The group selected its first healthy endpoint.</summary>
    public const string FirstHealthy = "first-healthy";

    /// <summary>The active endpoint became unhealthy and another healthy endpoint was selected.</summary>
    public const string ActiveEndpointUnhealthy = "active-endpoint-unhealthy";

    /// <summary>The active endpoint became unhealthy and no healthy endpoint remains.</summary>
    public const string NoHealthyEndpoint = "no-healthy-endpoint";

    /// <summary>A higher-priority endpoint stayed healthy for the failback grace period.</summary>
    public const string HigherPriorityEndpointRecovered = "higher-priority-endpoint-recovered";
}

/// <summary>Describes an active endpoint change.</summary>
/// <param name="PreviousEndpoint">The endpoint selected before the change, if any.</param>
/// <param name="CurrentEndpoint">The endpoint selected after the change, if any.</param>
/// <param name="Reason">One of the <see cref="RespireFailoverSwitchReasons"/> values.</param>
/// <param name="ChangedAt">When the group made the change.</param>
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
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeLock = new();
    private Task? _monitor;
    private Task? _disposal;
    private CandidateState? _active;
    private volatile bool _disposed;

    private RespireFailoverGroup(CandidateState[] candidates, RespireFailoverGroupOptions options, TimeProvider clock)
    {
        _candidates = candidates;
        _options = options;
        _clock = clock;
    }

    /// <summary>Raised when health policy changes the selected endpoint.</summary>
    /// <remarks>
    /// Handlers run synchronously on the health monitor, so a slow handler delays the next probe round.
    /// Keep handlers short, and never block synchronously on <see cref="DisposeAsync"/> from a handler.
    /// </remarks>
    public event Action<RespireFailoverSwitch>? EndpointSwitched;

    /// <summary>Whether the group currently has a healthy endpoint.</summary>
    /// <remarks>
    /// Health changes only when probes complete, so this value can remain <see langword="true"/> for up to
    /// <see cref="RespireFailoverGroupOptions.FailureThreshold"/> probe rounds after an endpoint stops responding.
    /// </remarks>
    public bool IsConnected => Volatile.Read(ref _active) is not null && !_disposed;

    /// <summary>The client selected for new operations. Read this property again after a switch.</summary>
    /// <exception cref="RespireConnectionException">
    /// No healthy endpoint is currently available. Check <see cref="IsConnected"/> to avoid the exception.
    /// </exception>
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
    /// <remarks>
    /// Each candidate receives one initial probe. A candidate that fails it starts unhealthy and is
    /// reconsidered by the next background probe round.
    /// </remarks>
    public static ValueTask<RespireFailoverGroup> ConnectAsync(
        IEnumerable<RespireFailoverCandidate> candidates,
        RespireFailoverGroupOptions? options = null,
        CancellationToken cancellationToken = default)
        => ConnectCoreAsync(candidates, options, TimeProvider.System, cancellationToken);

    internal static ValueTask<RespireFailoverGroup> ConnectAsync(
        IEnumerable<RespireFailoverCandidate> candidates,
        RespireFailoverGroupOptions? options,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
        => ConnectCoreAsync(candidates, options, clock, cancellationToken);

    private static async ValueTask<RespireFailoverGroup> ConnectCoreAsync(
        IEnumerable<RespireFailoverCandidate> candidates,
        RespireFailoverGroupOptions? options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(clock);
        var settings = options ?? new RespireFailoverGroupOptions();
        settings.Validate();

        var states = new List<CandidateState>();
        var endpoints = new List<RespireEndpoint>();
        RespireFailoverGroup? group = null;
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate is null) throw new ArgumentException("Failover candidates cannot contain null entries.", nameof(candidates));
                ArgumentNullException.ThrowIfNull(candidate.Options);
                var snapshot = candidate.Options.ValidateAndSnapshot();
                if (snapshot.ReconnectPolicy is { MaxAttempts: not null })
                {
                    throw new RespireConfigurationException(
                        "Failover group candidates require an unlimited reconnect policy (MaxAttempts = null) so a candidate can recover after an outage.");
                }
                if (snapshot.Endpoints.Count != 1 || snapshot.UseCluster || !string.IsNullOrWhiteSpace(snapshot.SentinelPrimaryName))
                {
                    throw new RespireConfigurationException(
                        "Standalone failover candidates require exactly one endpoint and cannot enable Cluster or Sentinel mode.");
                }
                var endpoint = snapshot.Endpoints[0];
                if (endpoints.Any(existing => existing.Port == endpoint.Port
                    && string.Equals(existing.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new RespireConfigurationException(
                        $"Standalone failover candidates must use distinct endpoints; '{endpoint}' is listed more than once.");
                }
                endpoints.Add(endpoint);
                if (snapshot.ClientSideCache is not null)
                {
                    throw new RespireConfigurationException(
                        "Client-side caching is not supported across independent failover deployments.");
                }

                var client = RespireClient.Create(snapshot);
                states.Add(new CandidateState(client, candidate.Priority, states.Count));
            }

            if (states.Count == 0) throw new ArgumentException("At least one failover candidate is required.", nameof(candidates));

            var created = group = new RespireFailoverGroup(states.ToArray(), settings, clock);
            await Task.WhenAll(states.Select(state => created.ProbeAsync(state, cancellationToken))).ConfigureAwait(false);
            await group.SelectActiveAsync().ConfigureAwait(false);
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
            if (group is not null)
            {
                group._stop.Dispose();
                group._gate.Dispose();
            }
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
                    var now = _clock.GetTimestamp();
                    await Task.WhenAll(_candidates
                        .Where(candidate => candidate.CanProbe(_clock, now))
                        .Select(candidate => ProbeAsync(candidate, _stop.Token))).ConfigureAwait(false);
                    await SelectActiveAsync().ConfigureAwait(false);
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
            candidate.MarkHealthy(_clock.GetTimestamp());
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
            candidate.MarkFailed(error, _clock.GetUtcNow(), _clock.GetTimestamp(), _options);
            RespireTelemetry.RecordFailoverProbe(
                candidate.Endpoint,
                succeeded: false,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async ValueTask SelectActiveAsync()
    {
        RespireFailoverSwitch? change = null;
        await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            // Disposal clears the selection under this gate; a late monitor round must not restore it.
            if (_disposed) return;
            var now = _clock.GetUtcNow();
            var nowTimestamp = _clock.GetTimestamp();
            var active = _active;
            var healthy = _candidates
                .Where(static candidate => candidate.IsHealthy)
                .OrderBy(static candidate => candidate.Priority)
                .ThenBy(static candidate => candidate.Order)
                .ToArray();
            var best = healthy.FirstOrDefault();

            CandidateState? selected = active;
            string? reason = null;
            if (active is null)
            {
                selected = best;
                reason = best is null ? null : RespireFailoverSwitchReasons.FirstHealthy;
            }
            else if (!active.IsHealthy)
            {
                selected = best;
                reason = best is null
                    ? RespireFailoverSwitchReasons.NoHealthyEndpoint
                    : RespireFailoverSwitchReasons.ActiveEndpointUnhealthy;
            }
            else
            {
                // Fail back to the highest-priority candidate that has completed its grace period, so an
                // unstable top-priority endpoint cannot block failback to a stable intermediate one.
                var recovered = healthy.FirstOrDefault(candidate =>
                    (candidate.Priority < active.Priority
                        || candidate.Priority == active.Priority && candidate.Order < active.Order)
                    && candidate.HasCompletedFailbackGrace(_clock, nowTimestamp, _options.FailbackGracePeriod));
                if (recovered is not null)
                {
                    selected = recovered;
                    reason = RespireFailoverSwitchReasons.HigherPriorityEndpointRecovered;
                }
            }

            if (!ReferenceEquals(selected, active))
            {
                Volatile.Write(ref _active, selected);
                change = new RespireFailoverSwitch(
                    active?.Endpoint,
                    selected?.Endpoint,
                    reason ?? RespireFailoverSwitchReasons.NoHealthyEndpoint,
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

    internal static bool HasElapsed(TimeProvider clock, long started, long now, TimeSpan duration)
        => clock.GetElapsedTime(started, now) >= duration;

    /// <summary>Stops health probes and disposes every candidate client.</summary>
    /// <remarks>Concurrent and repeated calls all complete when the first disposal finishes.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
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
        _stop.Dispose();
        _gate.Dispose();
    }

    private sealed class CandidateState(RespireClient client, int priority, int order)
    {
        private readonly object _gate = new();
        private bool _isHealthy;
        private int _consecutiveFailures;
        private DateTimeOffset? _circuitOpenUntil;
        private long? _circuitOpenedAt;
        private TimeSpan _circuitOpenDuration;
        private long? _healthySince;
        private string? _lastErrorType;

        public RespireClient Client { get; } = client;
        public int Priority { get; } = priority;
        public int Order { get; } = order;
        public RespireEndpoint Endpoint => Client.Endpoint;
        public bool IsHealthy { get { lock (_gate) return _isHealthy; } }
        public int ConsecutiveFailures { get { lock (_gate) return _consecutiveFailures; } }
        public DateTimeOffset? CircuitOpenUntil { get { lock (_gate) return _circuitOpenUntil; } }

        public bool CanProbe(TimeProvider clock, long now)
        {
            lock (_gate)
            {
                return _circuitOpenedAt is not { } openedAt
                    || HasElapsed(clock, openedAt, now, _circuitOpenDuration);
            }
        }

        public bool HasCompletedFailbackGrace(TimeProvider clock, long now, TimeSpan gracePeriod)
        {
            lock (_gate)
            {
                return _healthySince is { } healthySince && HasElapsed(clock, healthySince, now, gracePeriod);
            }
        }

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

        public void MarkHealthy(long timestamp)
        {
            lock (_gate)
            {
                // MarkFailed clears _healthySince below the threshold too, so the grace period restarts here.
                if (!_isHealthy || _healthySince is null) _healthySince = timestamp;
                _isHealthy = true;
                _consecutiveFailures = 0;
                _circuitOpenUntil = null;
                _circuitOpenedAt = null;
                _lastErrorType = null;
            }
        }

        public void MarkFailed(Exception error, DateTimeOffset now, long timestamp, RespireFailoverGroupOptions options)
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                // Any failed probe restarts the failback grace period, even before the circuit opens.
                _healthySince = null;
                if (_consecutiveFailures >= options.FailureThreshold)
                {
                    _isHealthy = false;
                    _circuitOpenedAt = timestamp;
                    _circuitOpenDuration = options.CircuitOpenDuration;
                    _circuitOpenUntil = options.CircuitOpenDuration >= DateTimeOffset.MaxValue - now
                        ? DateTimeOffset.MaxValue
                        : now + options.CircuitOpenDuration;
                }
                _lastErrorType = error.GetType().Name;
            }
        }
    }
}
