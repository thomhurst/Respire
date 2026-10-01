using System.Globalization;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal static class SentinelResolver
{
    private static readonly Verb SentinelPeers = new(-1, "SENTINEL", "SENTINELS");
    public static async ValueTask<TResult> ResolveAndConnectPrimaryAsync<TResult>(
        RespireOptions options,
        Func<RespireOptions, CancellationToken, ValueTask<TResult>> connectPrimaryAsync,
        CancellationToken cancellationToken,
        SentinelDiscoveryState? discoveryState = null,
        RespireEndpoint? preferredSentinel = null,
        RespireEndpoint? expectedPrimary = null)
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
            sentinelEndpoints.Remove(preferred);
            sentinelEndpoints.Insert(0, preferred);
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
                var primary = await QueryPrimaryAsync(
                        endpoint,
                        options.SentinelPrimaryName!,
                        sentinelOptions,
                        logger,
                        discoveryTimeoutSource.Token,
                        cancellationToken,
                        index < initialCount ? AddPeer : null)
                    .ConfigureAwait(false);
                discoveryCompleted = true;
                discoveryTimeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
                if (expectedPrimary is { } expected && !SameEndpoint(primary, expected))
                {
                    throw new RespireConnectionException(
                        $"Sentinel {endpoint} returned stale primary {primary}; failover event announced {expected}.");
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
                    return await connectPrimaryAsync(primaryOptions, connectTimeoutSource.Token).ConfigureAwait(false);
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
            catch (OperationCanceledException error) when (
                discoveryTimeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new RespireTimeoutException(
                    "SENTINEL GET-MASTER-ADDR-BY-NAME", discoveryTimeout, error,
                    RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting));
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

    private static bool SameEndpoint(RespireEndpoint left, RespireEndpoint right)
        => left.Port == right.Port && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);

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

    private static async ValueTask<RespireEndpoint> QueryPrimaryAsync(
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
        finally
        {
            reply.Dispose();
        }
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

    internal SentinelDiscoveryState(IEnumerable<RespireEndpoint> configured)
    {
        foreach (var endpoint in configured)
            if (_known.Add(endpoint)) _endpoints.Add(endpoint);
        _configuredCount = _known.Count;
    }

    internal RespireEndpoint[] Snapshot() { lock (_gate) return _endpoints.ToArray(); }

    internal bool TryAdd(RespireEndpoint endpoint)
    {
        lock (_gate)
        {
            if (_known.Count - _configuredCount == MaximumDiscoveredEndpoints || !_known.Add(endpoint)) return false;
            _endpoints.Add(endpoint);
            return true;
        }
    }

    private sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly EndpointComparer Instance = new();
        public bool Equals(RespireEndpoint x, RespireEndpoint y)
            => x.Port == y.Port && StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host);
        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.Host), endpoint.Port);
    }
}
