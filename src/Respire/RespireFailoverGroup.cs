using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An independently operated Redis deployment in a failover group.</summary>
/// <param name="Options">Connection settings for one standalone, Sentinel, or Redis Cluster deployment.</param>
/// <param name="Priority">Lower values have higher priority. Equal priorities keep input order.</param>
public sealed record RespireFailoverCandidate(RespireOptions Options, int Priority = 0);

/// <summary>Health and failback settings for a Redis failover group.</summary>
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
/// <param name="Endpoint">Validated Redis data endpoint, or null until Sentinel publishes a live primary.</param>
/// <param name="Priority">Candidate priority.</param>
/// <param name="IsHealthy">Whether latest health checks consider endpoint healthy.</param>
/// <param name="ConsecutiveFailures">Consecutive failed health probes.</param>
/// <param name="CircuitOpenUntil">Time when an open endpoint circuit may probe again.</param>
/// <param name="LastErrorType">Type name of most recent probe error.</param>
public sealed record RespireFailoverEndpointStatus(
    RespireEndpoint? Endpoint,
    int Priority,
    bool IsHealthy,
    int ConsecutiveFailures,
    DateTimeOffset? CircuitOpenUntil,
    string? LastErrorType);

/// <summary>Reasons reported by <see cref="RespireFailoverSwitch.Reason"/>.</summary>
public static class RespireFailoverSwitchReasons
{
    /// <summary>The group selected its first healthy endpoint.</summary>
    /// <remarks>
    /// The first selection happens inside <see cref="RespireFailoverGroup.ConnectAsync(IEnumerable{RespireFailoverCandidate}, RespireFailoverGroupOptions?, CancellationToken)"/>,
    /// before callers can subscribe to <see cref="RespireFailoverGroup.EndpointSwitched"/>, so this reason is
    /// reported only through the <c>respire.failover.endpoint.switches</c> metric and logs.
    /// </remarks>
    public const string FirstHealthy = "first-healthy";

    /// <summary>An endpoint became healthy after the group had no healthy endpoint.</summary>
    public const string RecoveredFromNoHealthyEndpoint = "recovered-from-no-healthy-endpoint";

    /// <summary>The active endpoint became unhealthy and another healthy endpoint was selected.</summary>
    public const string ActiveEndpointUnhealthy = "active-endpoint-unhealthy";

    /// <summary>The active endpoint became unhealthy and no healthy endpoint remains.</summary>
    public const string NoHealthyEndpoint = "no-healthy-endpoint";

    /// <summary>A higher-priority endpoint stayed healthy for the failback grace period.</summary>
    public const string HigherPriorityEndpointRecovered = "higher-priority-endpoint-recovered";

    /// <summary>An earlier endpoint with equal priority stayed healthy for the failback grace period.</summary>
    public const string EarlierEqualPriorityEndpointRecovered = "earlier-equal-priority-endpoint-recovered";
}

/// <summary>Describes an active endpoint change.</summary>
/// <param name="PreviousEndpoint">
/// The endpoint selected before the change, if any. For a Sentinel candidate this is the primary observed when
/// that candidate was selected, even if Sentinel has since moved the deployment to another primary.
/// </param>
/// <param name="CurrentEndpoint">
/// The endpoint selected after the change, if any. For a Sentinel candidate this is its current validated primary.
/// </param>
/// <param name="Reason">One of the <see cref="RespireFailoverSwitchReasons"/> values.</param>
/// <param name="ChangedAt">When the group made the change.</param>
public sealed record RespireFailoverSwitch(
    RespireEndpoint? PreviousEndpoint,
    RespireEndpoint? CurrentEndpoint,
    string Reason,
    DateTimeOffset ChangedAt);

/// <summary>
/// Maintains independent standalone, Sentinel, or Redis Cluster clients and selects a healthy deployment for new work.
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
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _disposeLock = new();
    private Task? _monitor;
    private Task? _disposal;
    private CandidateState? _active;
    // Guarded by _gate: the endpoint observed when _active was selected.
    private RespireEndpoint? _activeEndpoint;
    private bool _hasSelected;
    private volatile bool _disposed;

    /// <summary>Friend-test access; never used by normal failover coordination.</summary>
    internal TestAccess ForTests => new(this);

    /// <summary>Read-only inspection for tests that control shutdown callbacks before disposal.</summary>
    internal readonly struct TestAccess(RespireFailoverGroup group)
    {
        internal CancellationToken StopToken => group._stop.Token;
    }

    private RespireFailoverGroup(CandidateState[] candidates, RespireFailoverGroupOptions options, TimeProvider clock, ILogger? logger)
    {
        _candidates = candidates;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised when health policy changes the selected endpoint after <see cref="ConnectAsync(IEnumerable{RespireFailoverCandidate}, RespireFailoverGroupOptions?, CancellationToken)"/> returns.</summary>
    /// <remarks>
    /// The initial selection made during connection is not raised; read <see cref="ActiveClient"/> after connecting instead.
    /// Handlers run synchronously on the health monitor, so a slow handler delays the next probe round.
    /// Keep handlers short, and never wait for <see cref="DisposeAsync"/> from a handler, synchronously or
    /// asynchronously: disposal waits for the monitor, which is running the handler. Handler exceptions are
    /// ignored, counted by <c>respire.failover.monitor.errors</c> with <c>respire.failover.error.source</c> = <c>handler</c>,
    /// and logged as warnings through the first candidate that sets <see cref="RespireOptions.LoggerFactory"/>.
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
        var configured = new ConfiguredEndpointRegistry();
        ILogger? logger = null;
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
                var isSentinel = !string.IsNullOrWhiteSpace(snapshot.SentinelPrimaryName);
                if (snapshot.Endpoints.Count == 0 || (isSentinel && snapshot.UseCluster)
                    || (!isSentinel && !snapshot.UseCluster && snapshot.Endpoints.Count != 1))
                {
                    throw new RespireConfigurationException(
                        "Failover candidates require one endpoint in standalone mode, one or more Sentinel endpoints with a primary service name, or one or more Cluster seeds.");
                }
                var fallbackEndpoint = snapshot.Endpoints[0];
                if (isSentinel) configured.AddSentinel(snapshot.SentinelPrimaryName!, snapshot.Endpoints);
                else configured.AddData(snapshot.Endpoints);
                if (snapshot.ClientSideCache is not null)
                {
                    throw new RespireConfigurationException(
                        "Client-side caching is not supported across independent failover deployments.");
                }

                logger ??= snapshot.CreateLogger("Respire.FailoverGroup");
                var client = RespireClient.Create(snapshot);
                states.Add(new CandidateState(client, candidate.Priority, states.Count, fallbackEndpoint,
                    snapshot.SentinelPrimaryName, snapshot.Endpoints));
            }

            if (states.Count == 0) throw new ArgumentException("At least one failover candidate is required.", nameof(candidates));

            var created = group = new RespireFailoverGroup(states.ToArray(), settings, clock, logger);
            await Task.WhenAll(states.Select(state => created.ProbeAsync(state, cancellationToken))).ConfigureAwait(false);
            // Discovery has now run once. Reject a configuration whose candidates already share a deployment;
            // later convergence is handled by each probe instead (see FindDeploymentConflict).
            foreach (var state in states)
            {
                if (FindDeploymentConflict(state, states) is { } conflict) throw new RespireConfigurationException(conflict);
            }
            await group.SelectActiveAsync().ConfigureAwait(false);
            if (Volatile.Read(ref group._active) is null)
            {
                throw new RespireConnectionException("Unable to connect to any failover candidate.");
            }

            group._monitor = group.MonitorAsync();
            return group;
        }
        catch (Exception constructionError)
        {
            List<Exception>? cleanupFailures = null;
            foreach (var state in states)
            {
                try { await state.Client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            }
            if (group is not null)
            {
                try { group._stop.Dispose(); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
                try { group._gate.Dispose(); }
                catch (Exception error) { (cleanupFailures ??= []).Add(error); }
            }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, constructionError);
                throw new AggregateException("Failover group construction and cleanup both failed.", cleanupFailures);
            }
            throw;
        }
    }

    /// <summary>
    /// Decides which of two candidates that resolve to one deployment loses. A healthy incumbent keeps
    /// serving while a recovering duplicate stays failed. When both have the same health, the candidate
    /// with lower precedence (priority, then input order) fails, so concurrent probes never fail both.
    /// </summary>
    internal static bool ShouldFailCandidateForDeploymentConflict(bool candidateIsHealthy, bool otherIsHealthy, bool otherPrecedes)
        => candidateIsHealthy == otherIsHealthy ? otherPrecedes : otherIsHealthy;

    /// <summary>
    /// Returns why <paramref name="candidate"/> duplicates another candidate's deployment, or null.
    /// Discovery can change after connection (a recovered candidate, a learned Sentinel peer, or a
    /// Sentinel failover), so probes repeat this check against the latest discovered state.
    /// </summary>
    private static string? FindDeploymentConflict(CandidateState candidate, IReadOnlyList<CandidateState> candidates)
    {
        var comparer = RespireEndpointComparer.Instance;
        var primary = candidate.Endpoint;
        var discoveredSentinels = candidate.DiscoveredSentinels;
        foreach (var other in candidates)
        {
            if (ReferenceEquals(candidate, other)) continue;
            var otherPrimary = other.Endpoint;
            if (candidate.IsSentinel && other.IsSentinel)
            {
                var samePrimary = primary is { } current && otherPrimary is { } existing && comparer.Equals(current, existing);
                var otherSentinels = other.DiscoveredSentinels;
                var sameServiceOverlaps = string.Equals(candidate.SentinelPrimaryName, other.SentinelPrimaryName, StringComparison.Ordinal)
                    && discoveredSentinels.Any(endpoint => otherSentinels.Contains(endpoint, comparer));
                if ((samePrimary || sameServiceOverlaps) && ShouldFail(candidate, other))
                {
                    return $"Failover candidates for Sentinel service '{candidate.SentinelPrimaryName}' discovered the same primary or overlapping Sentinel endpoints.";
                }
            }
            else if (candidate.IsSentinel)
            {
                // A data endpoint that is a learned Sentinel peer is reported by that data candidate's own check.
                if (primary is { } current && other.DataEndpoints.Contains(current) && ShouldFail(candidate, other))
                {
                    return $"Sentinel candidate '{candidate.SentinelPrimaryName}' discovered primary '{current}', which another failover candidate uses as its data endpoint.";
                }
            }
            else if (other.IsSentinel)
            {
                // A Sentinel is a control-plane server: it answers PING but cannot serve application commands.
                foreach (var sentinel in other.DiscoveredSentinels)
                {
                    if (candidate.DataEndpoints.Contains(sentinel))
                        return $"Failover candidate data endpoint '{sentinel}' is a Sentinel discovered for service '{other.SentinelPrimaryName}'.";
                }
                if (otherPrimary is { } existing && candidate.DataEndpoints.Contains(existing) && ShouldFail(candidate, other))
                {
                    return $"Failover candidate data endpoint '{existing}' is the primary discovered for Sentinel service '{other.SentinelPrimaryName}'.";
                }
            }
            // Standalone and Cluster data endpoints were checked for duplicates during configuration.
        }
        return null;

        static bool ShouldFail(CandidateState candidate, CandidateState other)
            => ShouldFailCandidateForDeploymentConflict(candidate.IsHealthy, other.IsHealthy, other.Precedes(candidate));
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
                catch (Exception error)
                {
                    RecordMonitorError("monitor", error);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(CandidateState candidate, CancellationToken cancellationToken)
    {
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeTimeout);
        var started = Stopwatch.GetTimestamp();
        string conflict;
        try
        {
            await candidate.ProbeAsync(timeout.Token, observation).ConfigureAwait(false);
            var found = FindDeploymentConflict(candidate, _candidates);
            if (found is null)
            {
                candidate.MarkHealthy(_clock.GetTimestamp());
                RespireTelemetry.RecordFailoverProbe(
                    candidate.TelemetryEndpoint,
                    succeeded: true,
                    Stopwatch.GetElapsedTime(started).TotalSeconds);
                return;
            }
            conflict = found;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            observation.Handled(error);
            candidate.MarkFailed(error, _clock.GetUtcNow(), _clock.GetTimestamp(), _options);
            RespireTelemetry.RecordFailoverProbe(
                candidate.TelemetryEndpoint,
                succeeded: false,
                Stopwatch.GetElapsedTime(started).TotalSeconds);
            return;
        }

        // A duplicate deployment adds no redundancy, so it is unhealthy at once instead of after FailureThreshold
        // probes. It is checked again whenever its circuit allows the next probe.
        var conflictError = new RespireConfigurationException(conflict);
        observation.Handled(conflictError);
        candidate.MarkFailed(conflictError, _clock.GetUtcNow(), _clock.GetTimestamp(),
            _options, openCircuit: true);
        RespireTelemetry.RecordFailoverProbe(
            candidate.TelemetryEndpoint,
            succeeded: false,
            Stopwatch.GetElapsedTime(started).TotalSeconds);
        try
        {
            _logger?.FailoverDuplicateDeployment(candidate.TelemetryEndpoint, conflict);
        }
        catch { /* Logging must not stop health monitoring. */ }
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
                reason = best is null ? null
                    : _hasSelected ? RespireFailoverSwitchReasons.RecoveredFromNoHealthyEndpoint
                    : RespireFailoverSwitchReasons.FirstHealthy;
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
                    reason = recovered.Priority == active.Priority
                        ? RespireFailoverSwitchReasons.EarlierEqualPriorityEndpointRecovered
                        : RespireFailoverSwitchReasons.HigherPriorityEndpointRecovered;
                }
            }

            if (!ReferenceEquals(selected, active))
            {
                _hasSelected |= selected is not null;
                Volatile.Write(ref _active, selected);
                // A Sentinel candidate's live endpoint can already show its new primary, or null while
                // rediscovering, so report the endpoint captured when the previous candidate was selected.
                var selectedEndpoint = selected?.Endpoint;
                change = new RespireFailoverSwitch(
                    _activeEndpoint,
                    selectedEndpoint,
                    reason ?? RespireFailoverSwitchReasons.NoHealthyEndpoint,
                    now);
                _activeEndpoint = selectedEndpoint;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (change is { } switched)
        {
            RespireTelemetry.RecordFailoverSwitch(switched.PreviousEndpoint, switched.CurrentEndpoint, switched.Reason);
            try
            {
                _logger?.FailoverEndpointSwitched(switched.PreviousEndpoint, switched.CurrentEndpoint, switched.Reason);
            }
            catch { /* Logging must not stop health monitoring. */ }
            var handlers = EndpointSwitched;
            if (handlers is not null)
            {
                foreach (Action<RespireFailoverSwitch> handler in handlers.GetInvocationList())
                {
                    try { handler(switched); }
                    catch (Exception error) { RecordMonitorError("handler", error); }
                }
            }
        }
    }

    private void RecordMonitorError(string source, Exception error)
    {
        RespireTelemetry.RecordFailoverMonitorError(source, error);
        try
        {
            if (source == "handler") _logger?.FailoverSwitchObserverFailed(error);
            else _logger?.FailoverMonitorFailed(error);
        }
        catch { /* Logging must not stop health monitoring. */ }
    }

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
            _activeEndpoint = null;
        }
        finally
        {
            _gate.Release();
        }

        List<Exception>? failures = null;
        try
        {
            // Selection is already closed. A failing callback must not skip candidate cleanup.
            try { await _stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }

            if (_monitor is { } monitor)
            {
                try { await monitor.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            foreach (var candidate in _candidates)
            {
                try { await candidate.Client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
        }
        finally
        {
            _stop.Dispose();
            _gate.Dispose();
        }
        if (failures is { Count: 1 }) throw failures[0];
        if (failures is { Count: > 1 }) throw new AggregateException(failures);
    }

    /// <summary>Rejects configured endpoints that make two candidates share one deployment.</summary>
    private sealed class ConfiguredEndpointRegistry
    {
        private readonly HashSet<RespireEndpoint> _dataEndpoints = new(RespireEndpointComparer.Instance);
        private readonly HashSet<RespireEndpoint> _sentinelSeeds = new(RespireEndpointComparer.Instance);
        private readonly Dictionary<string, HashSet<RespireEndpoint>> _seedsByService = new(StringComparer.Ordinal);

        public void AddSentinel(string service, IEnumerable<RespireEndpoint> seeds)
        {
            var normalized = new HashSet<RespireEndpoint>(seeds, RespireEndpointComparer.Instance);
            if (normalized.Overlaps(_dataEndpoints))
            {
                throw new RespireConfigurationException(
                    "Failover candidates cannot reuse a Sentinel seed as a standalone or Cluster data endpoint.");
            }
            if (_seedsByService.TryGetValue(service, out var serviceSeeds))
            {
                if (serviceSeeds.Overlaps(normalized))
                    throw new RespireConfigurationException("Failover candidates for the same Sentinel service cannot use overlapping seed endpoints.");
                serviceSeeds.UnionWith(normalized);
            }
            else
            {
                _seedsByService.Add(service, normalized);
            }
            _sentinelSeeds.UnionWith(normalized);
        }

        // Standalone candidates use one endpoint; Cluster candidates use every seed.
        public void AddData(IEnumerable<RespireEndpoint> endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                if (_sentinelSeeds.Contains(endpoint))
                {
                    throw new RespireConfigurationException(
                        $"Failover candidates cannot reuse Sentinel seed '{endpoint}' as a standalone or Cluster data endpoint.");
                }
                if (!_dataEndpoints.Add(endpoint))
                {
                    throw new RespireConfigurationException(
                        $"Failover candidates must use distinct configured endpoints; '{endpoint}' is listed more than once.");
                }
            }
        }
    }

    private sealed class CandidateState(RespireClient client, int priority, int order, RespireEndpoint fallbackEndpoint,
        string? sentinelPrimaryName, IEnumerable<RespireEndpoint> configuredEndpoints)
    {
        private readonly Lock _gate = new();
        // Standalone and Cluster clients have a fixed endpoint; a Sentinel candidate's endpoint is its current primary.
        private readonly RespireEndpoint? _fixedEndpoint = client.Core.Sentinel is null ? client.Endpoint : (RespireEndpoint?)null;
        private bool _isHealthy;
        private int _consecutiveFailures;
        private DateTimeOffset? _circuitOpenUntil;
        private long? _circuitOpenedAt;
        private TimeSpan _circuitOpenDuration;
        private long? _healthySince;
        private string? _lastErrorType;

        public RespireClient Client { get; } = client;
        public string? SentinelPrimaryName { get; } = sentinelPrimaryName;
        public bool IsSentinel => Client.Core.Sentinel is not null;
        /// <summary>Configured standalone endpoint or Cluster seeds; empty for a Sentinel candidate.</summary>
        public HashSet<RespireEndpoint> DataEndpoints { get; } = client.Core.Sentinel is null
            ? new(configuredEndpoints, RespireEndpointComparer.Instance)
            : new(RespireEndpointComparer.Instance);
        /// <summary>Configured and learned Sentinel endpoints; empty for a standalone or Cluster candidate.</summary>
        public RespireEndpoint[] DiscoveredSentinels => Client.Core.Sentinel?.DiscoveredEndpoints ?? [];
        public int Priority { get; } = priority;
        public int Order { get; } = order;
        public RespireEndpoint? Endpoint => Client.Core.Sentinel is { } sentinel
            ? sentinel.Current is { IsRetired: false } generation ? (RespireEndpoint?)generation.Endpoint : null
            : _fixedEndpoint;
        public RespireEndpoint TelemetryEndpoint => Endpoint ?? fallbackEndpoint;

        /// <summary>Whether this candidate is selected before <paramref name="other"/> when both are healthy.</summary>
        public bool Precedes(CandidateState other)
            => Priority < other.Priority || Priority == other.Priority && Order < other.Order;

        private static readonly CmdN ClusterInfoCommand = new(new Verb(-1, "CLUSTER", "INFO"), []);

        /// <summary>Runs the health check for this candidate's deployment type. Throws when it is unhealthy.</summary>
        public async ValueTask ProbeAsync(CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation)
        {
            if (Client.Core.Cluster is not null)
            {
                // A keyless PING reaches one arbitrary node, which can answer while slots are unserved.
                var info = await Client.ConvertUnobservedResponseAsync(
                    "CLUSTER INFO", ClusterInfoCommand, cancellationToken, 0,
                    static (int _, in RespValue value) => ClusterInspectionParser.Info(in value), observation).ConfigureAwait(false);
                if (!string.Equals(info.State, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new RespireConnectionException($"Cluster reports cluster_state:{info.State}.");
                return;
            }
            // ROLE proves the node is still the elected primary, which a demoted node answering PING does not.
            // PING still runs afterwards so a node that reports the primary ROLE but rejects PING (for example
            // through ACL rules or a proxy) is unhealthy, as the documented health contract says.
            if (Client.Core.Sentinel is { } sentinel)
                await sentinel.EnsureValidatedPrimaryAsync(cancellationToken).ConfigureAwait(false);
            await Client.ConvertUnobservedResponseAsync(
                "PING", new RawCommand(RespCommands.Ping), cancellationToken, 0,
                static (int _, in RespValue _) => true, observation).ConfigureAwait(false);
        }
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

        private static bool HasElapsed(TimeProvider clock, long started, long now, TimeSpan duration)
            => clock.GetElapsedTime(started, now) >= duration;

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

        public void MarkFailed(Exception error, DateTimeOffset now, long timestamp, RespireFailoverGroupOptions options,
            bool openCircuit = false)
        {
            lock (_gate)
            {
                _consecutiveFailures++;
                // Any failed probe restarts the failback grace period, even before the circuit opens.
                _healthySince = null;
                if (openCircuit || _consecutiveFailures >= options.FailureThreshold)
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
