using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal static class SentinelResolver
{
    private static readonly Verb SentinelMaster = new(-1, "SENTINEL", "MASTER");
    private static readonly Verb SentinelPeers = new(-1, "SENTINEL", "SENTINELS");

    /// <summary>
    /// Returns the replica set reported by the first Sentinel that answers with a well-formed
    /// <c>SENTINEL REPLICAS</c> reply, trying Sentinels in the same order as primary discovery.
    /// Replies are not merged: during a failover or partition, Sentinels can disagree, and a union
    /// would keep replicas that only a stale Sentinel still lists. A reply with a malformed row is
    /// treated as a failed Sentinel, so a broken reply cannot retire every known replica; an empty
    /// array is an authoritative "no replicas".
    /// </summary>
    internal static async ValueTask<RespireEndpoint[]> DiscoverReplicaEndpointsAsync(
        RespireOptions options, IEnumerable<RespireEndpoint> sentinels, CancellationToken cancellationToken)
    {
        var connectionOptions = CreateSentinelConnectionOptions(options);
        var logger = options.CreateLogger("Respire.Sentinel");
        foreach (var sentinel in sentinels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.CommandTimeout ?? options.ConnectTimeout);
            try
            {
                await using var connection = await RespireConnection.ConnectAsync(
                    sentinel.Host, sentinel.Port, connectionOptions, logger, deadline.Token).ConfigureAwait(false);
                using var reply = await connection.SendAsync(
                    new Cmd1(Verbs.SentinelReplicas, options.SentinelPrimaryName!), deadline.Token).ConfigureAwait(false);
                if (TryParseReplicaList(in reply, out var replicas)) return replicas;
                try { logger?.LogDebug("Sentinel {Endpoint} returned a malformed SENTINEL REPLICAS reply", sentinel); }
                catch (Exception) { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { continue; }
            catch (Exception error)
            {
                try { logger?.LogDebug(error, "Optional Sentinel replica discovery failed at {Endpoint}", sentinel); }
                catch (Exception) { }
            }
        }
        throw new RespireConnectionException("Sentinel replica discovery failed for every configured Sentinel endpoint.");
    }

    /// <summary>
    /// Parses a <c>SENTINEL REPLICAS</c> reply. Rows flagged <c>s_down</c>, <c>o_down</c> or
    /// <c>disconnected</c> are left out. Returns false when the reply is an error, not an array, or
    /// has any row that is not a field/value map with a usable <c>ip</c> and <c>port</c>.
    /// </summary>
    internal static bool TryParseReplicaList(in RespValue reply, out RespireEndpoint[] replicas)
    {
        replicas = [];
        if (reply.IsError || reply.Type != RespDataType.Array) return false;
        var discovered = new List<RespireEndpoint>();
        foreach (ref readonly var row in reply.AsArray())
        {
            if (row.Type != RespDataType.Array) return false;
            var fields = row.AsArray();
            if (fields.Length % 2 != 0) return false;
            string? host = null, port = null, flags = null;
            for (var index = 0; index < fields.Length; index += 2)
            {
                if (fields[index].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
                var name = fields[index].AsString();
                if (name is not ("ip" or "port" or "flags")) continue;
                // Other fields may carry any type; the ones routing depends on must be strings.
                if (fields[index + 1].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
                var value = fields[index + 1].AsString();
                switch (name)
                {
                    case "ip": host = value; break;
                    case "port": port = value; break;
                    default: flags = value; break;
                }
            }
            if (flags is not null && (flags.Contains("s_down", StringComparison.Ordinal)
                || flags.Contains("o_down", StringComparison.Ordinal)
                || flags.Contains("disconnected", StringComparison.Ordinal))) continue;
            if (!TryParseEndpoint(host, port, out var endpoint)) return false;
            if (!discovered.Contains(endpoint, RespireEndpointComparer.Instance)) discovered.Add(endpoint);
        }
        replicas = discovered.ToArray();
        return true;
    }

    public static async ValueTask<TResult> ResolveAndConnectPrimaryAsync<TResult>(
        RespireOptions options,
        Func<RespireOptions, CancellationToken, ValueTask<TResult>> connectPrimaryAsync,
        CancellationToken cancellationToken,
        SentinelDiscoveryState? discoveryState = null,
        RespireEndpoint? preferredSentinel = null,
        RespireEndpoint? previouslyValidatedPrimary = null,
        RespireEndpoint? preferredTarget = null,
        SentinelHint? notificationHint = null)
    {
        if (string.IsNullOrWhiteSpace(options.SentinelPrimaryName))
        {
            return await connectPrimaryAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (options.UseCluster)
        {
            throw new ArgumentException(
                "Redis Sentinel discovery and Redis Cluster routing cannot both be enabled.",
                nameof(options));
        }

        discoveryState ??= new SentinelDiscoveryState(options.Endpoints.Count == 0
            ? [new RespireEndpoint("localhost", 26379)] : options.Endpoints);
        var sentinelEndpoints = discoveryState.Snapshot().ToList();
        if (preferredSentinel is { } preferred)
        {
            var preferredIndex = sentinelEndpoints.FindIndex(endpoint =>
                SentinelDiscoveryState.EndpointComparer.Instance.Equals(endpoint, preferred));
            if (preferredIndex > 0)
            {
                sentinelEndpoints.RemoveAt(preferredIndex);
                sentinelEndpoints.Insert(0, preferred);
            }
        }
        var initialCount = sentinelEndpoints.Count;
        var sentinelOptions = CreateSentinelConnectionOptions(options);
        var logger = options.CreateLogger("Respire.Sentinel");
        Exception? lastError = null;
        var lastErrorIsDiscoveryTimeout = false;
        var fallbackBudget = new SentinelFallbackBudget(options.ReconnectPolicy, logger);

        for (var index = 0; index < sentinelEndpoints.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = sentinelEndpoints[index];
            if (fallbackBudget.Schedule(index, endpoint) is { } delay)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            var discoveryTimeout = options.CommandTimeout ?? options.ConnectTimeout;
            using var discoveryTimeoutSource = CommandTimeoutCancellation.Create(cancellationToken, discoveryTimeout);
            var discoveryCompleted = false;
            try
            {
                var observation = await QueryPrimaryAsync(
                        endpoint,
                        options.SentinelPrimaryName!,
                        sentinelOptions,
                        logger,
                        discoveryTimeoutSource.Token,
                        cancellationToken,
                        index < initialCount ? AddPeer : null)
                    .ConfigureAwait(false);
                var primary = observation.Endpoint;
                discoveryCompleted = true;
                discoveryTimeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
                // Switch evidence names its source, not whichever healthy generation application
                // traffic may have published while the notification was waiting to retry.
                var matchesSwitchSource = notificationHint is { } hint
                    ? MatchesSwitchSource(primary, in hint)
                    : previouslyValidatedPrimary is { } previous && RespireEndpointComparer.Instance.Equals(primary, previous);
                var contradictsSwitch = matchesSwitchSource && preferredTarget is { } target
                    && !RespireEndpointComparer.Instance.Equals(target, primary)
                    && !discoveryState.IsNewerConfiguration(observation.Epoch);
                if (observation.Epoch is null) discoveryState.WarnMissingEpoch(logger, endpoint);
                if (contradictsSwitch || !discoveryState.TryObserveConfiguration(primary, observation.Epoch))
                {
                    // A rejected view consumes the same fallback budget as a failed ROLE check.
                    throw new RespireConnectionException($"Sentinel {endpoint} reported a stale configuration for {primary}.");
                }
                var primaryOptions = options with
                {
                    Endpoints = new List<RespireEndpoint> { primary },
                    SentinelPrimaryName = null,
                };
                using var connectTimeoutSource = CommandTimeoutCancellation.Create(
                    cancellationToken,
                    options.ConnectTimeout);
                try
                {
                    var result = await connectPrimaryAsync(primaryOptions, connectTimeoutSource.Token).ConfigureAwait(false);
                    discoveryState.AcceptConfiguration(primary, observation.Epoch);
                    return result;
                }
                catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                    error, cancellationToken, connectTimeoutSource.Token))
                {
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
                }
                catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested
                    && connectTimeoutSource.IsCancellationRequested)
                {
                    // The connection deadline fired while the caller token stayed live.
                    throw new RespireTimeoutException(
                        "CONNECT", options.ConnectTimeout, error,
                        RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting));
                }
            }
            catch (OperationCanceledException error) when (CommandTimeoutCancellation.IsFromLinkedToken(
                error, cancellationToken, discoveryTimeoutSource.Token))
            {
                // Preserve the upstream token identity for callers that distinguish their own
                // deadline from Sentinel's candidate-discovery deadline.
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The discovery deadline usually equals the caller's command timeout. When it
                // fires first, report the same timeout the caller's deadline would have raised.
                lastErrorIsDiscoveryTimeout = !discoveryCompleted && discoveryTimeoutSource.IsCancellationRequested
                    && (ex is RespireTimeoutException || ContainsCancellation(ex));
                lastError = lastErrorIsDiscoveryTimeout
                    ? new RespireTimeoutException(
                        "SENTINEL GET-MASTER-ADDR-BY-NAME", discoveryTimeout, ex,
                        RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting))
                    : ex;
                logger?.LogWarning(
                    lastError,
                    "Redis Sentinel discovery or primary connection failed through {Host}:{Port}",
                    endpoint.Host,
                    endpoint.Port);
                if (fallbackBudget.StopAfterFailure(endpoint, index + 1 < sentinelEndpoints.Count))
                {
                    break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lastErrorIsDiscoveryTimeout && lastError is RespireTimeoutException timeoutError) throw timeoutError;
        var message =
            $"Unable to discover and connect to Redis Sentinel service '{options.SentinelPrimaryName}' " +
            $"from {sentinelEndpoints.Count} endpoint(s).";
        throw lastError is null
            ? new RespireConnectionException(message)
            : new RespireConnectionException(message, lastError);

        void AddPeer(RespireEndpoint endpoint)
        {
            if (discoveryState.TryAdd(endpoint)) sentinelEndpoints.Add(endpoint);
        }
    }

    private static bool ContainsCancellation(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is OperationCanceledException) return true;
        return false;
    }

    private struct SentinelFallbackBudget(RespireReconnectPolicy? policy, ILogger? logger)
    {
        private const string ReconnectScope = "sentinel-discovery";
        private int _attempts;

        internal TimeSpan? Schedule(int candidateIndex, RespireEndpoint endpoint)
        {
            // Initial setup is immediate. Configured/learned peers and ROLE rejection
            // share one replacement budget for this resolution.
            if (candidateIndex == 0 || policy is null) return null;
            var delay = policy.GetDelay(++_attempts);
            RespireTelemetry.RecordDiscoveryReconnect(endpoint, ReconnectScope, _attempts, delay, logger);
            return delay;
        }

        internal readonly bool StopAfterFailure(RespireEndpoint endpoint, bool hasRemainingCandidate)
        {
            if (_attempts == 0 || policy?.IsExhausted(_attempts) != true) return false;
            // Exhaustion means the policy prevented trying a remaining candidate.
            // Reaching the same count on the final candidate is ordinary depletion.
            if (hasRemainingCandidate)
                RespireTelemetry.RecordDiscoveryReconnect(endpoint, ReconnectScope, _attempts, null, logger);
            return true;
        }
    }

    internal static RespireConnectionOptions CreateSentinelConnectionOptions(RespireOptions options)
    {
        var authenticationDisabled = options.SentinelPassword is { Length: 0 };
        return options.ToConnectionOptions() with
        {
            Username = authenticationDisabled ? null : options.SentinelUsername ?? options.Username,
            Password = authenticationDisabled ? null : options.SentinelPassword ?? options.Password,
            CredentialProvider = authenticationDisabled ? null : options.SentinelCredentialProvider
                ?? (options.SentinelPassword is null && options.SentinelUsername is null ? options.CredentialProvider : null),
            ClientName = null,
            Database = 0,
            Protocol = RespProtocol.Resp2,
            // The discovery deadline already bounds GET-MASTER-ADDR-BY-NAME. A second
            // command timer can win the same deadline and hide its timeout classification.
            CommandTimeout = null,
            UseTls = options.SentinelUseTls ?? options.UseTls,
            TlsOptions = options.SentinelTlsOptions ?? options.TlsOptions,
            PushHandler = null,
        };
    }

    private static async ValueTask<(RespireEndpoint Endpoint, long? Epoch)> QueryPrimaryAsync(
        RespireEndpoint sentinel,
        string serviceName,
        RespireConnectionOptions options,
        ILogger? logger,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        Action<RespireEndpoint>? addPeer)
    {
        await using var connection = await RespireConnection.ConnectAsync(
                sentinel.Host,
                sentinel.Port,
                options,
                logger,
                cancellationToken)
            .ConfigureAwait(false);
        var reply = await connection.SendAsync(
                new Cmd1(Verbs.SentinelGetMasterAddressByName, serviceName),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Peers may rescue a stale or missing primary response. Only endpoints known at
            // the start of this attempt expand discovery, preventing recursive exploration.
            if (addPeer is not null)
            {
                try { await DiscoverPeersAsync(connection, serviceName, addPeer, logger, cancellationToken).ConfigureAwait(false); }
                catch (Exception error)
                {
                    callerCancellationToken.ThrowIfCancellationRequested();
                    // Keep the completed primary reply even if optional peer discovery times
                    // out, drops the socket, or returns malformed RESP. Caller cancellation wins.
                    logger?.LogDebug(error, "Optional Sentinel peer discovery failed at {Host}:{Port}", sentinel.Host, sentinel.Port);
                }
            }
            var primary = ParsePrimaryAddress(in reply, sentinel, serviceName);

            // Correlate the address with its epoch. If failover changes it between commands,
            // retry discovery instead of assigning metadata to a different primary.
            RespireEndpoint? configuredPrimary = null;
            long? configurationEpoch = null;
            try
            {
                using var metadata = await connection.SendAsync(new Cmd1(SentinelMaster, serviceName), cancellationToken)
                    .ConfigureAwait(false);
                if (TryParsePrimaryConfiguration(in metadata, out var configured, out var epoch))
                {
                    configuredPrimary = configured;
                    configurationEpoch = epoch;
                }
            }
            catch (Exception error)
            {
                callerCancellationToken.ThrowIfCancellationRequested();
                logger?.LogDebug(error, "Optional Sentinel configuration discovery failed at {Sentinel}", sentinel);
            }
            if (configuredPrimary is { } snapshot && !RespireEndpointComparer.Instance.Equals(primary, snapshot))
                throw new RespireConnectionException("Sentinel primary changed while reading its configuration epoch.");
            if (configurationEpoch is not null)
            {
                // During promotion, MASTER can expose the new epoch with the old address
                // while GET-MASTER-ADDR-BY-NAME already returns the promoted replica.
                // Bracket metadata with address reads before remembering its epoch.
                using var confirmation = await connection.SendAsync(
                    new Cmd1(Verbs.SentinelGetMasterAddressByName, serviceName), cancellationToken).ConfigureAwait(false);
                var confirmed = ParsePrimaryAddress(in confirmation, sentinel, serviceName);
                if (!RespireEndpointComparer.Instance.Equals(primary, confirmed))
                    throw new RespireConnectionException("Sentinel primary changed while reading its configuration epoch.");
            }
            return (primary, configurationEpoch);
        }
        finally
        {
            reply.Dispose();
        }
    }

    private static RespireEndpoint ParsePrimaryAddress(in RespValue reply, RespireEndpoint sentinel, string serviceName)
    {
        if (reply.IsError)
        {
            throw new RespireServerException(reply.GetErrorMessage(), "SENTINEL GET-MASTER-ADDR-BY-NAME");
        }

        if (reply.IsNull)
        {
            throw new RespireConnectionException(
                $"Redis Sentinel service '{serviceName}' was not found on {sentinel}.");
        }

        var parts = reply.AsArray();
        if (parts.Length < 2)
        {
            throw new RespireProtocolException(
                $"Redis Sentinel returned {parts.Length} fields for service '{serviceName}', expected host and port.");
        }

        var host = parts[0].AsString();
        var portText = parts[1].AsString();
        if (!TryParseEndpoint(host, portText, out var primary))
        {
            throw new RespireProtocolException(
                $"Redis Sentinel returned an invalid host or port for service '{serviceName}'.");
        }
        return primary;
    }

    internal static bool MatchesSwitchSource(RespireEndpoint candidate, in SentinelHint hint)
    {
        var candidateHost = NormalizeHost(candidate.Host);
        if (hint.OldPrimary is { } first && Matches(first, hint.OldPrimaryAddresses)) return true;
        if (hint.AdditionalSources is { } additional)
            foreach (var source in additional)
                if (Matches(source.Endpoint, source.Addresses)) return true;
        return false;

        bool Matches(RespireEndpoint source, string[]? addresses)
            => source.Port == candidate.Port && (StringComparer.OrdinalIgnoreCase.Equals(candidateHost, NormalizeHost(source.Host))
                || addresses is not null && addresses.Contains(candidateHost, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeHost(string host)
        => IPAddress.TryParse(host, out var address) ? NormalizeAddress(address) : host;

    internal static string NormalizeAddress(IPAddress address)
        => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    internal static bool TryParsePrimaryConfiguration(in RespValue reply, out RespireEndpoint endpoint, out long epoch)
    {
        endpoint = default;
        epoch = -1;
        if (reply.Type != RespDataType.Array) return false;
        var fields = reply.AsArray();
        if (fields.Length % 2 != 0) return false;
        string? host = null, port = null;
        for (var index = 0; index < fields.Length; index += 2)
        {
            if (fields[index].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
            var name = fields[index].AsString();
            if (name is not ("ip" or "port" or "config-epoch")) continue;
            if (fields[index + 1].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) return false;
            var value = fields[index + 1].AsString();
            if (name == "ip") host = value;
            else if (name == "port") port = value;
            else if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out epoch)) return false;
        }
        return epoch >= 0 && TryParseEndpoint(host, port, out endpoint);
    }

    private static async ValueTask DiscoverPeersAsync(RespireConnection connection, string serviceName,
        Action<RespireEndpoint> addPeer, ILogger? logger, CancellationToken cancellationToken)
    {
        using var reply = await connection.SendAsync(
            new Cmd1(SentinelPeers, serviceName), cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            logger?.LogDebug("Sentinel peer discovery was unavailable: {Error}", reply.GetErrorMessage());
            return; // Optional discovery permissions must not reject a usable configured Sentinel.
        }
        if (reply.Type != RespDataType.Array) return;
        foreach (ref readonly var peer in reply.AsArray())
        {
            if (peer.Type != RespDataType.Array) continue;
            var fields = peer.AsArray();
            if (fields.Length % 2 != 0) continue;
            string? host = null, port = null;
            for (var field = 0; field < fields.Length; field += 2)
            {
                if (fields[field].Type is not (RespDataType.BulkString or RespDataType.SimpleString)
                    || fields[field + 1].Type is not (RespDataType.BulkString or RespDataType.SimpleString)) continue;
                var name = fields[field].AsString();
                if (name == "ip") host = fields[field + 1].AsString();
                else if (name == "port") port = fields[field + 1].AsString();
            }
            if (TryParseEndpoint(host, port, out var endpoint)) addPeer(endpoint);
        }
    }

    private static bool TryParseEndpoint(string? host, string? portText, out RespireEndpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('\0')
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535) return false;
        endpoint = new(host, port);
        return true;
    }
}

// Reusable discovery state for runtime failover. Configured endpoints are never evicted;
// learned peers are bounded, deduplicated by host/port, and copied before asynchronous work.
internal sealed class SentinelDiscoveryState
{
    internal const int MaximumDiscoveredEndpoints = 64;
    private readonly object _gate = new();
    private readonly List<RespireEndpoint> _endpoints = [];
    private readonly HashSet<RespireEndpoint> _known = new(EndpointComparer.Instance);
    private readonly int _configuredCount;
    private long? _acceptedEpoch;
    private RespireEndpoint? _observedPrimary;
    private long? _observedEpoch;
    private int _missingEpochWarning;

    internal bool IsNewerConfiguration(long? epoch)
    {
        lock (_gate) return epoch is { } candidate && _acceptedEpoch is { } accepted && candidate > accepted;
    }

    internal bool IsCurrentConfiguration(RespireEndpoint primary, long? epoch)
    {
        lock (_gate) return IsCurrentConfigurationLocked(primary, epoch);
    }

    private bool IsCurrentConfigurationLocked(RespireEndpoint primary, long? epoch)
    {
        // Servers that never expose epochs retain ROLE/switch-evidence discovery. Once an
        // epoch is observed, a missing epoch cannot erase that ordering evidence.
        if (_observedEpoch is not { } observed) return true;
        if (epoch is { } candidate && candidate > observed) return true;
        return (epoch is null || epoch == observed) && _observedPrimary is { } current
            && EndpointComparer.Instance.Equals(primary, current);
    }

    internal bool TryObserveConfiguration(RespireEndpoint primary, long? epoch)
    {
        lock (_gate)
        {
            if (!IsCurrentConfigurationLocked(primary, epoch)) return false;
            if (epoch is { } candidate && (_observedEpoch is null || candidate > _observedEpoch))
            {
                _observedEpoch = candidate;
                _observedPrimary = primary;
            }
            return true;
        }
    }

    internal void WarnMissingEpoch(ILogger? logger, RespireEndpoint sentinel)
    {
        if (logger is null || Interlocked.Exchange(ref _missingEpochWarning, 1) != 0) return;
        try
        {
            logger.LogWarning("Sentinel {Sentinel} did not provide a configuration epoch. Discovery relies on ROLE and switch evidence; any previously observed epoch remains enforced.", sentinel);
        }
        catch { /* Diagnostic providers must not prevent failover. */ }
    }

    internal void AcceptConfiguration(RespireEndpoint primary, long? epoch)
    {
        lock (_gate)
        {
            if (!TryObserveConfiguration(primary, epoch))
                throw new RespireConnectionException($"Sentinel configuration for {primary} was superseded during validation.");
            _acceptedEpoch = epoch ?? _observedEpoch;
        }
    }


    internal SentinelDiscoveryState(IEnumerable<RespireEndpoint> configured)
    {
        foreach (var endpoint in configured)
            if (_known.Add(endpoint)) _endpoints.Add(endpoint);
        _configuredCount = _known.Count;
    }

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RespireEndpoint[] Snapshot() { lock (_gate) return _endpoints.ToArray(); }

    // Returns the endpoints and a task that completes when a later TryAdd learns a new endpoint.
    // Endpoints are never removed, so consumers only need to react to additions.
    internal RespireEndpoint[] Snapshot(out Task changed)
    {
        lock (_gate)
        {
            changed = _changed.Task;
            return _endpoints.ToArray();
        }
    }

    internal bool TryAdd(RespireEndpoint endpoint)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_known.Count - _configuredCount == MaximumDiscoveredEndpoints || !_known.Add(endpoint)) return false;
            _endpoints.Add(endpoint);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return true;
    }

    internal sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly EndpointComparer Instance = new();
        public bool Equals(RespireEndpoint x, RespireEndpoint y)
            => x.Port == y.Port && StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host);
        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
    }
}
