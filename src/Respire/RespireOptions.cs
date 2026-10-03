using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

/// <summary>A Redis endpoint (host and port).</summary>
public readonly record struct RespireEndpoint(string Host, int Port = 6379)
{
    /// <summary>Parses "host", "host:port", or an IPv6 address.</summary>
    public static RespireEndpoint Parse(string value) => Parse(value, 6379);

    internal static RespireEndpoint Parse(string value, int defaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (value[0] == '[')
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket < 0)
            {
                throw new ArgumentException("An IPv6 endpoint is missing its closing bracket.", nameof(value));
            }

            var host = value[1..closingBracket];
            if (closingBracket == value.Length - 1)
            {
                return new RespireEndpoint(host, defaultPort);
            }

            if (value[closingBracket + 1] != ':'
                || !int.TryParse(value.AsSpan(closingBracket + 2), CultureInfo.InvariantCulture, out var ipv6Port)
                || !IsValidPort(ipv6Port))
            {
                throw new ArgumentException($"Invalid port in IPv6 endpoint '{value}'.", nameof(value));
            }

            return new RespireEndpoint(host, ipv6Port);
        }

        var colon = value.IndexOf(':');
        if (colon > 0 && colon == value.LastIndexOf(':'))
        {
            if (!int.TryParse(value.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var port)
                || !IsValidPort(port))
            {
                throw new ArgumentException($"Invalid port in endpoint '{value}'.", nameof(value));
            }

            return new RespireEndpoint(value[..colon], port);
        }

        return new RespireEndpoint(value, defaultPort);
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>Parses a host or host-and-port string.</summary>
    public static implicit operator RespireEndpoint(string value) => Parse(value);

    /// <inheritdoc/>
    public override string ToString() => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}

/// <summary>RESP protocol selection or automatic negotiation policy.</summary>
public enum RespProtocol
{
    /// <summary>Prefer RESP3; use RESP2 only when the server rejects HELLO as unsupported.</summary>
    Auto = 0,

    /// <summary>RESP2, supported by Redis 2.0 and later.</summary>
    Resp2 = 2,

    /// <summary>HELLO 3 — required for push-based features such as client-side caching. Redis 6+.</summary>
    Resp3 = 3,
}

/// <summary>What happens when a subscription's buffer is full because the consumer is slow.</summary>
public enum SubscriptionOverflow
{
    /// <summary>Drop the oldest buffered message to admit the new one (default).</summary>
    DropOldest,

    /// <summary>Drop the incoming message, keeping the buffered backlog.</summary>
    DropNewest,
}

/// <summary>
/// Configuration for a <see cref="RespireClient"/>. Prefer
/// <see cref="RespireClient.ConnectAsync(string, CancellationToken)"/> with a
/// <c>redis://</c> URI for the common cases; this record carries every knob.
/// </summary>
public sealed record RespireOptions
{
    // Internal seam for Respire.Testing. Ordinary TCP/TLS connections retain their direct path.
    private const string ConnectionStringParameterName = "connectionString";

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    internal Func<string, int, CancellationToken, ValueTask<Stream>>? TestingStreamFactory { get; init; }
    private bool _useCluster;
    private string? _sentinelPrimaryName;
    private TimeSpan? _connectionIdleReadTimeout;

    /// <summary>
    /// Cluster seed nodes or Sentinel discovery endpoints. Standalone mode requires exactly one
    /// endpoint; use ConnectAnyAsync for connection-time fallback between independent deployments.
    /// </summary>
    public IList<RespireEndpoint> Endpoints { get; init; } = [];

    /// <summary>Explicit read replicas for standalone primary/replica deployments. Sentinel ignores this list.</summary>
    public IList<RespireEndpoint> ReplicaEndpoints { get; init; } = [];

    /// <summary>Default routing policy for catalog commands whose metadata confirms they are read-only.</summary>
    public RespireReadFrom ReadFrom { get; init; } = RespireReadFrom.Primary;

    /// <summary>Optional bounded duplication of slow idempotent reads. Null (the default) disables hedging.</summary>
    public RespireHedgedReadOptions? HedgedReads { get; init; }

    /// <summary>
    /// Client availability zone used by AZ-affinity read policies. Zone names are compared
    /// ordinally. Configure this before connecting, including when selecting a policy through a view.
    /// Servers without zone metadata remain eligible for fallback.
    /// </summary>
    public string? ClientAvailabilityZone { get; init; }

    /// <summary>
    /// Bounds how stale replica read topology can be. A connection's <c>ROLE</c> check is reused
    /// for this long, Sentinel replica discovery refreshes at most this often, and a replica that
    /// failed a connection or role check is skipped for this long. Defaults to one second;
    /// <see cref="TimeSpan.Zero"/> revalidates on every read.
    /// </summary>
    public TimeSpan ReplicaRefreshInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Age after which a Cluster replica read starts a background topology refresh, so a failover
    /// that promotes a replica is noticed even when no redirect occurs. Tests shorten it.
    /// </summary>
    internal TimeSpan ReplicaRouteRevalidationInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enables Redis Cluster routing. MOVED and ASK redirects are followed automatically and
    /// learned hash slots are routed directly on later commands.
    /// </summary>
    public bool UseCluster
    {
        get => _useCluster;
        init => _useCluster = value;
    }

    /// <summary>
    /// Redis Sentinel primary service name. When set, <see cref="Endpoints"/> identifies Sentinel
    /// nodes. Discovery runs eagerly with ConnectAsync or on the first operation with Create.
    /// Data connections must confirm a primary ROLE before use; the data credentials need ROLE
    /// permission. Disconnects and READONLY replies retire the current generation so subsequent
    /// operations discover a validated primary without replaying accepted commands.
    /// </summary>
    public string? SentinelPrimaryName
    {
        get => _sentinelPrimaryName;
        init => _sentinelPrimaryName = value;
    }

    /// <summary>ACL username. Defaults to Redis's "default" user when only a password is set.</summary>
    public string? Username { get; init; }

    /// <summary>Password sent by AUTH or HELLO during connection setup.</summary>
    public string? Password { get; init; }

    /// <summary>Optional caller-owned credentials for data connections; overrides static username/password.</summary>
    public IRespireCredentialProvider? CredentialProvider { get; init; }

    /// <summary>Optional independent Sentinel credentials; null uses Sentinel static credentials or the data provider.</summary>
    public IRespireCredentialProvider? SentinelCredentialProvider { get; init; }

    /// <summary>Time before credential expiry at which renewal starts. Defaults to five minutes.</summary>
    public TimeSpan CredentialRefreshBeforeExpiry { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before retrying failed acquisition or unchanged credentials. Defaults to five seconds.</summary>
    public TimeSpan CredentialRefreshRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeProvider CredentialTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Optional ACL username for Sentinel endpoints; falls back to <see cref="Username"/>.</summary>
    public string? SentinelUsername { get; init; }

    /// <summary>
    /// Optional password for Sentinel endpoints. Null falls back to <see cref="Password"/>;
    /// an empty string explicitly disables Sentinel authentication.
    /// </summary>
    public string? SentinelPassword { get; init; }

    /// <summary>
    /// Whether Sentinel discovery uses TLS. Null inherits <see cref="UseTls"/>; false allows a
    /// plaintext Sentinel to discover a TLS primary.
    /// </summary>
    public bool? SentinelUseTls { get; init; }

    /// <summary>
    /// Optional TLS settings for Sentinel endpoints. Null inherits <see cref="TlsOptions"/>.
    /// </summary>
    public SslClientAuthenticationOptions? SentinelTlsOptions { get; init; }

    /// <summary>When set, CLIENT SETNAME runs during the handshake — invaluable in CLIENT LIST.</summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Logical database SELECTed during every connection handshake. Non-zero Cluster databases
    /// require Valkey 9+ with cluster-databases configured and INFO/SELECT permissions.
    /// </summary>
    public int Database { get; init; }

    /// <summary>Allows high-risk administrative commands such as FLUSHDB, FLUSHALL, and CONFIG SET.</summary>
    public bool AllowAdmin { get; init; }

    /// <summary>Protocol negotiation policy. Auto prefers RESP3 and falls back to RESP2 only for unsupported HELLO.</summary>
    /// <remarks>Explicit Resp2 skips HELLO; explicit Resp3 requires RESP3. Authentication, transport, timeout,
    /// and malformed-reply failures never cause fallback. Client-side caching always requires RESP3.</remarks>
    public RespProtocol Protocol { get; init; } = RespProtocol.Auto;

    /// <summary>Opt-in RESP3 maintenance notifications. Disabled by default; Auto tolerates an unsupported server.</summary>
    public RespireMaintenanceNotificationMode MaintenanceNotifications { get; init; }

    /// <summary>Minimum command/receive timeout during maintenance. Never shortens an existing timeout.</summary>
    public TimeSpan MaintenanceRelaxedTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum maintenance window when its completion notification is lost.</summary>
    public TimeSpan MaintenanceWindowTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout for the initial TCP connect (per connection).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional command, dedicated, and pub/sub recovery backoff and Cluster/Sentinel discovery policy. Null preserves existing scheduling.</summary>
    /// <remarks>Dedicated rentals retry failed acquisition with independent budgets. Sentinel fallback shares one budget per resolution.
    /// Cluster discovery shares one fallback budget across nested node and seed selection per round.</remarks>
    public RespireReconnectPolicy? ReconnectPolicy { get; init; }
    internal string? ReconnectTelemetryScope { get; init; }
    // Invoked synchronously under SubscriptionHub's recovery state lock. The callback must
    // be non-blocking and non-reentrant; capture state only, without acquiring other locks.
    internal Action? ReconnectEpisodeStarted { get; init; }

    /// <summary>Interval for background Redis Cluster topology refresh. Defaults to 60 seconds. Null,
    /// <see cref="TimeSpan.Zero"/>, or <see cref="Timeout.InfiniteTimeSpan"/> disables the periodic timer;
    /// other negative values are rejected.</summary>
    /// <remarks>
    /// <para>The worker starts after the client first connects, so <c>Create</c> stays lazy. Each periodic
    /// refresh sends one <c>CLUSTER SLOTS</c> to a single node, and the interval is shortened by up to 10%
    /// of random jitter so many clients do not refresh in step.</para>
    /// <para>Disabling the timer disables only periodic refresh. Primary disconnects (at most one refresh
    /// per second), <c>MOVED</c> redirects (debounced for 5 seconds) and failed-refresh retries (backoff
    /// from 5 to 60 seconds) still refresh the topology. While a failed-refresh retry is pending, periodic
    /// and redirect-driven refreshes wait for it. These timings are fixed. A single refresh pass
    /// is bounded to 60 seconds regardless of this interval, and a failed pass keeps the last published
    /// slot map.</para>
    /// </remarks>
    public TimeSpan? ClusterTopologyRefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    // Test seam: drives the Cluster topology refresh schedule, debounce and discovery deadlines.
    internal TimeProvider ClusterTopologyRefreshClock { get; init; } = TimeProvider.System;

    /// <summary>Clock used for cluster discovery retry delays.</summary>
    internal TimeProvider ClusterDiscoveryClock { get; init; } = TimeProvider.System;

    /// <summary>Use TLS. Enabled automatically for <c>rediss://</c> connection strings.</summary>
    public bool UseTls { get; init; }

    /// <summary>Optional TLS authentication and certificate-validation settings.</summary>
    public SslClientAuthenticationOptions? TlsOptions { get; init; }

    /// <summary>
    /// Aborts a connection when commands are awaiting replies and no bytes arrive within this
    /// period. Null (default) disables the receive watchdog.
    /// </summary>
    public TimeSpan? ConnectionIdleReadTimeout
    {
        get => _connectionIdleReadTimeout;
        init => _connectionIdleReadTimeout = value;
    }

    /// <summary>
    /// Client-side cap on how long a command waits for its response. Defaults to ten seconds;
    /// null disables the cap. Expiry throws <see cref="RespireTimeoutException"/>; the command may still
    /// execute server-side. Does not apply to intentionally blocking calls (BLPOP-style waits).
    /// Replica function propagation retries have a five-second budget, even when this cap is disabled.
    /// That retry budget does not cancel a function invocation already accepted by the server.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; } = DefaultCommandTimeout;

    /// <summary>Enables the shared process-wide thread-pool scheduling probe. Defaults to true.</summary>
    /// <remarks>One background thread samples once per second while enabled clients exist. Disabling this
    /// client removes its warning subscription; other clients may still provide process-wide timeout samples.</remarks>
    public bool ThreadPoolMonitoring { get; init; } = true;

    /// <summary>Scheduling delay that triggers a diagnostic warning. Defaults to 500 milliseconds.</summary>
    /// <remarks>Must be positive. Warnings are limited to one per client per 30 seconds.
    /// This is a diagnostic threshold; no thread-pool settings are changed.</remarks>
    public TimeSpan ThreadPoolWarningThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Multiplexed connections to open. Defaults to one. A single connection maximizes command
    /// coalescing per syscall and is fastest for typical workloads; under a 50-worker stress test,
    /// one connection doubled small-command PING throughput versus eight. Raise this only if
    /// profiling shows a single socket saturated.
    /// </summary>
    public int Connections { get; init; } = 1;

    /// <summary>Serializer behind non-primitive typed values. System.Text.Json by default.
    /// Wrap with RespireValueCodecSerializer to opt into framed value compression.</summary>
    public IRespireSerializer Serializer { get; init; } = RespireSerializer.Default;

    /// <summary>
    /// Enables RESP3 server-assisted client-side caching. Assign <c>new()</c> for bounded defaults.
    /// Null (default) disables caching with no tracking commands, invalidation processing, or storage overhead.
    /// </summary>
    public RespireClientSideCacheOptions? ClientSideCache { get; init; }

    /// <summary>Optional factory for Respire diagnostic logs.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Enables TCP keepalive on every connection and sets how long a connection may sit idle
    /// before the kernel starts probing. Whole seconds, minimum one second. Null (the default)
    /// leaves keepalive off. Recommended (e.g. 60 seconds) when connections idle behind NATs
    /// or load balancers that silently drop stale flows.
    /// </summary>
    public TimeSpan? TcpKeepAliveTime { get; init; }

    /// <summary>
    /// Interval between keepalive probes once <see cref="TcpKeepAliveTime"/> has elapsed
    /// without traffic. Whole seconds, minimum one second. Null keeps the OS default; requires
    /// <see cref="TcpKeepAliveTime"/>.
    /// </summary>
    public TimeSpan? TcpKeepAliveInterval { get; init; }

    /// <summary>
    /// Unanswered keepalive probes before the kernel declares the connection dead. Null keeps
    /// the OS default; requires <see cref="TcpKeepAliveTime"/>.
    /// </summary>
    public int? TcpKeepAliveRetryCount { get; init; }

    /// <summary>Buffered messages per subscription before <see cref="SubscriptionOverflow"/> applies.</summary>
    public int SubscriptionBufferSize { get; init; } = 1024;

    /// <summary>Policy used when a subscription consumer falls behind.</summary>
    public SubscriptionOverflow SubscriptionOverflow { get; init; } = SubscriptionOverflow.DropOldest;

    // Advanced wire tuning — the defaults are right for almost everyone.

    /// <summary>Initial size of the pooled parse buffer each connection reads into.</summary>
    public int ReceiveBufferSize { get; init; } = 64 * 1024;

    /// <summary>Initial size of each connection's two coalescing write buffers.</summary>
    public int WriteBufferSize { get; init; } = 64 * 1024;

    /// <summary>Maximum commands awaiting responses per connection.</summary>
    public int MaxInflightCommands { get; init; } = 16 * 1024;

    internal RespireEndpoint PrimaryEndpoint
        => Endpoints.Count > 0 ? Endpoints[0] : new RespireEndpoint("localhost");

    internal RespireOptions ValidateAndSnapshot()
    {
        if (Endpoints is null || Endpoints.Count == 0)
        {
            throw new RespireConfigurationException("At least one Redis endpoint is required.");
        }
        if (ReplicaEndpoints is null)
            throw new RespireConfigurationException("RespireOptions.ReplicaEndpoints cannot be null.");

        if (UseCluster && !string.IsNullOrWhiteSpace(SentinelPrimaryName))
            throw new RespireConfigurationException("Cluster and Sentinel routing cannot be enabled together.");

        Require(Enum.IsDefined(ReadFrom), nameof(ReadFrom), "must be a defined RespireReadFrom policy");
        Require(ClientAvailabilityZone is null || !string.IsNullOrWhiteSpace(ClientAvailabilityZone),
            nameof(ClientAvailabilityZone), "must be nonempty when provided");
        Require(ReadFrom is not (RespireReadFrom.AzAffinity or RespireReadFrom.AzAffinityReplicasAndPrimary)
            || ClientAvailabilityZone is not null, nameof(ClientAvailabilityZone), "is required for AZ-affinity reads");
        HedgedReads?.Validate();
        Require(
            ReplicaRefreshInterval >= TimeSpan.Zero && ReplicaRefreshInterval <= TimeSpan.FromHours(1),
            nameof(ReplicaRefreshInterval),
            "must be between zero and one hour");
        Require(
            ReplicaRouteRevalidationInterval >= TimeSpan.Zero && ReplicaRouteRevalidationInterval <= TimeSpan.FromHours(1),
            nameof(ReplicaRouteRevalidationInterval),
            "must be between zero and one hour");
        if (UseCluster && ReplicaEndpoints.Count != 0)
            throw new RespireConfigurationException("RespireOptions.ReplicaEndpoints is for standalone deployments; Redis Cluster discovers its own topology.");
        if (ReadFrom != RespireReadFrom.Primary && !UseCluster && string.IsNullOrWhiteSpace(SentinelPrimaryName)
            && ReplicaEndpoints.Count == 0)
            throw new RespireConfigurationException("RespireOptions.ReadFrom requires Sentinel discovery or at least one ReplicaEndpoints entry.");
        if (Endpoints.Count > 1 && !UseCluster && string.IsNullOrWhiteSpace(SentinelPrimaryName))
        {
            throw new RespireConfigurationException(
                $"The {Endpoints.Count} configured RespireOptions.Endpoints require UseCluster or SentinelPrimaryName. " +
                "Use RespireClient.ConnectAnyAsync for connection-time fallback between standalone deployments.");
        }

        Require(Protocol is RespProtocol.Auto or RespProtocol.Resp2 or RespProtocol.Resp3, nameof(Protocol), "must be Auto, Resp2, or Resp3");
        var effectiveProtocol = ClientSideCache is null ? Protocol : RespProtocol.Resp3;
        Require(Enum.IsDefined(MaintenanceNotifications), nameof(MaintenanceNotifications), "must be Disabled, Auto, or Enabled");
        Require(MaintenanceNotifications != RespireMaintenanceNotificationMode.Enabled || effectiveProtocol != RespProtocol.Resp2,
            nameof(MaintenanceNotifications), "requires RESP3 when Enabled");
        Require(MaintenanceRelaxedTimeout >= TimeSpan.FromMilliseconds(1) && MaintenanceRelaxedTimeout <= TimeSpan.FromDays(1),
            nameof(MaintenanceRelaxedTimeout), "must be between one millisecond and one day");
        Require(MaintenanceWindowTimeout >= TimeSpan.FromMilliseconds(1) && MaintenanceWindowTimeout <= TimeSpan.FromDays(1),
            nameof(MaintenanceWindowTimeout), "must be between one millisecond and one day");
        Require(Connections >= 1, nameof(Connections), "must be at least one");
        Require(Database >= 0, nameof(Database), "cannot be negative");
        Require(ConnectTimeout > TimeSpan.Zero, nameof(ConnectTimeout), "must be positive");
        Require(CredentialRefreshBeforeExpiry >= TimeSpan.FromMilliseconds(1), nameof(CredentialRefreshBeforeExpiry), "must be at least one millisecond");
        Require(CredentialRefreshRetryDelay >= TimeSpan.FromMilliseconds(1), nameof(CredentialRefreshRetryDelay), "must be at least one millisecond");
        Require(ThreadPoolWarningThreshold > TimeSpan.Zero, nameof(ThreadPoolWarningThreshold), "must be positive");
        ReconnectPolicy?.Validate();
        Require(ClusterTopologyRefreshInterval is null || ClusterTopologyRefreshInterval >= TimeSpan.Zero
                || ClusterTopologyRefreshInterval == Timeout.InfiniteTimeSpan,
            nameof(ClusterTopologyRefreshInterval), "must be non-negative, Timeout.InfiniteTimeSpan, or null");
        Require(
            CommandTimeout is null || CommandTimeout >= TimeSpan.FromMilliseconds(1),
            nameof(CommandTimeout),
            "must be at least one millisecond");
        Require(
            ConnectionIdleReadTimeout is null || ConnectionIdleReadTimeout >= TimeSpan.FromMilliseconds(1),
            nameof(ConnectionIdleReadTimeout),
            "must be at least one millisecond");
        Require(ReceiveBufferSize >= 1, nameof(ReceiveBufferSize), "must be at least one");
        Require(WriteBufferSize >= 1, nameof(WriteBufferSize), "must be at least one");
        Require(MaxInflightCommands >= 1, nameof(MaxInflightCommands), "must be at least one");
        Require(SubscriptionBufferSize >= 1, nameof(SubscriptionBufferSize), "must be at least one");
        Require(
            TcpKeepAliveTime is null || TcpKeepAliveTime >= TimeSpan.FromSeconds(1),
            nameof(TcpKeepAliveTime),
            "must be at least one second");
        Require(
            TcpKeepAliveInterval is null || TcpKeepAliveInterval >= TimeSpan.FromSeconds(1),
            nameof(TcpKeepAliveInterval),
            "must be at least one second");
        Require(
            TcpKeepAliveRetryCount is null or >= 1,
            nameof(TcpKeepAliveRetryCount),
            "must be at least one");

        if (ClientSideCache is { } cache)
        {
            // OPTIN Cluster ASK redirects send ASKING, CACHING YES, and the read atomically.
            var isOptIn = cache.TrackingMode != RespireClientTrackingMode.Broadcast;
            var requiredInflightCommands = isOptIn ? 2 : 1;
            if (UseCluster) requiredInflightCommands = isOptIn ? 3 : 2;
            var requirement = requiredInflightCommands switch
            {
                3 => "must be at least three for OPTIN caching with Cluster ASK redirects",
                2 => "must be at least two for OPTIN caching or Cluster ASK redirects",
                _ => "must be at least one",
            };
            Require(
                MaxInflightCommands >= requiredInflightCommands,
                nameof(MaxInflightCommands),
                requirement);
        }

        if (TcpKeepAliveTime is null
            && (TcpKeepAliveInterval is not null || TcpKeepAliveRetryCount is not null))
        {
            throw new RespireConfigurationException(
                "RespireOptions.TcpKeepAliveInterval and RespireOptions.TcpKeepAliveRetryCount " +
                "require RespireOptions.TcpKeepAliveTime.");
        }

        foreach (var endpoint in Endpoints)
        {
            if (endpoint.Port is < 1 or > 65535)
            {
                throw new RespireConfigurationException(
                    $"RespireOptions.Endpoints contains invalid TCP port {endpoint.Port}.");
            }
        }

        foreach (var endpoint in ReplicaEndpoints)
        {
            if (endpoint.Port is < 1 or > 65535)
                throw new RespireConfigurationException($"RespireOptions.ReplicaEndpoints contains invalid TCP port {endpoint.Port}.");
        }

        return this with
        {
            Endpoints = new List<RespireEndpoint>(Endpoints),
            ReplicaEndpoints = new List<RespireEndpoint>(ReplicaEndpoints),
            Protocol = effectiveProtocol,
            ClientSideCache = ClientSideCache?.ValidateAndSnapshot(),
        };
    }

    private static void Require(bool condition, string optionName, string requirement)
    {
        if (!condition)
        {
            throw new RespireConfigurationException($"RespireOptions.{optionName} {requirement}.");
        }
    }

    internal ILogger? CreateLogger(string category) => LoggerFactory?.CreateLogger(category);

    internal RespireConnectionOptions ToConnectionOptions(
        RespirePushHandler? pushHandler = null,
        bool enableClientTracking = false,
        bool enableMaintenanceNotifications = false)
        => new()
        {
            MaintenanceNotifications = enableMaintenanceNotifications ? MaintenanceNotifications : RespireMaintenanceNotificationMode.Disabled,
            MaintenanceRelaxedTimeout = MaintenanceRelaxedTimeout,
            MaintenanceWindowTimeout = MaintenanceWindowTimeout,
            TestingStreamFactory = TestingStreamFactory,
            ConnectTimeout = ConnectTimeout,
            ReconnectPolicy = ReconnectPolicy,
            ResponseTimeout = ConnectionIdleReadTimeout,
            CommandTimeout = CommandTimeout,
            UseTls = UseTls,
            TlsOptions = TlsOptions,
            Username = Username,
            Password = Password,
            CredentialProvider = CredentialProvider,
            CredentialRefreshBeforeExpiry = CredentialRefreshBeforeExpiry,
            CredentialRefreshRetryDelay = CredentialRefreshRetryDelay,
            CredentialTimeProvider = CredentialTimeProvider,
            ClientName = ClientName,
            Database = Database,
            RequireClusterDatabaseSupport = UseCluster && Database != 0,
            DiscoverAvailabilityZone = ClientAvailabilityZone is not null,
            Protocol = Protocol,
            TcpKeepAliveTime = TcpKeepAliveTime,
            TcpKeepAliveInterval = TcpKeepAliveInterval,
            TcpKeepAliveRetryCount = TcpKeepAliveRetryCount,
            ReceiveBufferSize = ReceiveBufferSize,
            WriteBufferSize = WriteBufferSize,
            MaxInflightCommands = MaxInflightCommands,
            PushHandler = pushHandler,
            EnableClientTracking = enableClientTracking,
            ClientTrackingOptions = enableClientTracking && ClientSideCache is { } cache
                ? new(cache.TrackingMode, cache.KeyPrefixes) : default,
        };

    /// <summary>
    /// Parses a connection string: "host", "host:port", a StackExchange.Redis-compatible
    /// comma-delimited string, or a <c>redis://[user[:password]@]host[:port][/database]</c> URI.
    /// Comma-delimited strings accept multiple endpoints only with <c>cluster=true</c> or
    /// <c>serviceName</c>. Options include <c>user</c>, <c>password</c>, <c>ssl</c>,
    /// <c>sslHost</c>, <c>sslProtocols</c>, <c>checkCertificateRevocation</c>, <c>clientName</c>
    /// (or <c>name</c>), <c>defaultDatabase</c>, <c>connectTimeout</c>, <c>asyncTimeout</c>,
    /// <c>syncTimeout</c>, <c>protocol</c>, <c>allowAdmin</c>, and Sentinel credentials/TLS.
    /// Recognized URI query parameters:
    /// <c>clientName</c>, <c>connections</c>, <c>connectTimeoutMs</c>, <c>commandTimeoutMs</c>,
    /// <c>connectionIdleReadTimeoutMs</c>, <c>protocol</c> (2/resp2 or 3/resp3), <c>db</c>,
    /// <c>useCluster</c> (true or false), <c>sentinelPrimaryName</c>, <c>sentinelUser</c>,
    /// <c>sentinelPassword</c>, <c>sentinelTls</c> (true or false), and
    /// <c>allowAdmin</c> (true or false).
    /// Use <c>rediss://</c> to enable TLS.
    /// In comma-delimited strings, <c>sslHost</c> enables TLS unless <c>ssl=false</c> is explicit;
    /// <c>sentinelSslHost</c> similarly enables Sentinel TLS unless <c>sentinelTls=false</c> is explicit.
    /// URI connections contain one endpoint. Comma-delimited seed lists preserve endpoint order;
    /// they never imply failover between independent standalone deployments.
    /// </summary>
    public static RespireOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var uriMarker = connectionString.IndexOf("://", StringComparison.Ordinal);
        var optionMarker = connectionString.IndexOf(',');
        var equalsMarker = connectionString.IndexOf('=');
        if (optionMarker < 0 || (equalsMarker >= 0 && equalsMarker < optionMarker))
        {
            optionMarker = equalsMarker;
        }

        if (optionMarker >= 0 && (uriMarker < 0 || optionMarker < uriMarker))
        {
            return ParseStackExchangeConnectionString(connectionString);
        }

        if (uriMarker < 0)
        {
            return new RespireOptions { Endpoints = { RespireEndpoint.Parse(connectionString) } }
                .ValidateAndSnapshot();
        }

        var uri = new Uri(connectionString, UriKind.Absolute);
        var useTls = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase);
        if (!useTls && !uri.Scheme.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported scheme '{uri.Scheme}' — expected redis:// or rediss://.", nameof(connectionString));
        }

        string? username = null;
        string? password = null;
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            if (colon < 0)
            {
                password = Uri.UnescapeDataString(uri.UserInfo);
            }
            else
            {
                username = colon == 0 ? null : Uri.UnescapeDataString(uri.UserInfo[..colon]);
                password = Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
            }
        }

        var database = 0;
        var path = uri.AbsolutePath.Trim('/');
        if (path.Length > 0)
        {
            database = ParseIntegerOption("database", path);
        }

        string? clientName = null;
        var connections = 1;
        TimeSpan connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? commandTimeout = DefaultCommandTimeout;
        TimeSpan? responseTimeout = null;
        var protocol = RespProtocol.Auto;
        var mode = new ConnectionStringMode();
        var allowAdmin = false;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (mode.TryApply(name, value))
            {
                continue;
            }

            switch (name.ToLowerInvariant())
            {
                case "clientname":
                    clientName = value;
                    break;
                case "connections":
                    connections = ParseIntegerOption(name, value);
                    if (connections < 1)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(connectionString), "Connection count must be at least one.");
                    }

                    break;
                case "connecttimeoutms":
                    connectTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "commandtimeoutms":
                    commandTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "responsetimeoutms":
                case "connectionidlereadtimeoutms":
                    responseTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "protocol":
                    protocol = ParseProtocolOption(name, value);
                    break;
                case "db":
                    database = ParseIntegerOption(name, value);
                    break;
                case "allowadmin":
                    allowAdmin = ParseBooleanOption(name, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown connection string parameter '{name}'.", nameof(connectionString));
            }
        }

        mode.Validate();
        var defaultPort = mode.ServiceName is null ? 6379 : 26379;
        return new RespireOptions
        {
            Endpoints = { new RespireEndpoint(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port) },
            Username = username,
            Password = password,
            SentinelUsername = mode.SentinelUsername,
            SentinelPassword = mode.SentinelPassword,
            SentinelUseTls = mode.SentinelUseTls,
            ClientName = clientName,
            Database = database,
            Connections = connections,
            ConnectTimeout = connectTimeout,
            UseTls = useTls,
            CommandTimeout = commandTimeout,
            ConnectionIdleReadTimeout = responseTimeout,
            Protocol = protocol,
            UseCluster = mode.UseCluster,
            SentinelPrimaryName = mode.ServiceName,
            AllowAdmin = allowAdmin,
        }.ValidateAndSnapshot();
    }

    private sealed class ConnectionStringMode
    {
        public bool UseCluster { get; private set; }
        public string? ServiceName { get; private set; }
        public string? SentinelUsername { get; private set; }
        public string? SentinelPassword { get; private set; }
        public bool? SentinelUseTls { get; private set; }

        public void Validate()
        {
            if (UseCluster && ServiceName is not null)
            {
                throw new ArgumentException(
                    "Redis Cluster (cluster=true) and Sentinel (serviceName) cannot both be selected.",
                    ConnectionStringParameterName);
            }
        }

        public bool TryApply(string name, string value)
        {
            switch (name.ToLowerInvariant())
            {
                case "cluster":
                case "usecluster":
                    UseCluster = ParseBooleanOption(name, value);
                    break;
                case "servicename":
                case "sentinelprimaryname":
                    RequireOptionValue(name, value);
                    ServiceName = value;
                    break;
                case "sentineluser":
                    SentinelUsername = value;
                    break;
                case "sentinelpassword":
                    SentinelPassword = value;
                    break;
                case "sentineltls":
                    SentinelUseTls = ParseBooleanOption(name, value);
                    break;
                default:
                    return false;
            }

            return true;
        }
    }

    private static void RequireOptionValue(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '{name}' requires a non-empty value.", ConnectionStringParameterName);
        }
    }

    private static bool ParseBooleanOption(string name, string value)
    {
        if (!bool.TryParse(value, out var result))
        {
            throw new ArgumentException($"Option '{name}' requires 'true' or 'false'.", ConnectionStringParameterName);
        }

        return result;
    }

    private static int ParseIntegerOption(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"Option '{name}' requires a 32-bit integer.", ConnectionStringParameterName);
        }

        return result;
    }

    private static RespProtocol ParseProtocolOption(string name, string value)
        => value.ToLowerInvariant() switch
        {
            "auto" => RespProtocol.Auto,
            "2" or "resp2" => RespProtocol.Resp2,
            "3" or "resp3" => RespProtocol.Resp3,
            _ => throw new ArgumentException(
                $"Option '{name}' requires 'auto', '2', 'resp2', '3', or 'resp3'.", ConnectionStringParameterName),
        };

    private static readonly SslProtocols[] DefinedSslProtocols = Enum.GetValues<SslProtocols>();

    private static bool IsCompleteSslProtocolMask(SslProtocols protocols)
    {
        // Individual protocols occupy multiple bits. A subset of those bits is not a protocol.
        foreach (var defined in DefinedSslProtocols)
        {
            if ((protocols & defined) == defined)
            {
                protocols &= ~defined;
            }
        }

        return protocols == SslProtocols.None;
    }

    private static SslProtocols ParseSslProtocols(string value)
    {
        var protocols = SslProtocols.None;
        foreach (var name in value.Split('|', StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SslProtocols>(name, ignoreCase: true, out var protocol) || !IsCompleteSslProtocolMask(protocol))
            {
                throw new ArgumentException($"Unsupported sslProtocols value '{name}'.", ConnectionStringParameterName);
            }

            protocols |= protocol;
        }

        return protocols;
    }

    private static RespireOptions ParseStackExchangeConnectionString(string connectionString)
    {
        List<string> endpointTexts = [];
        string? username = null;
        string? password = null;
        string? clientName = null;
        var database = 0;
        bool? useTls = null;
        var connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? asyncTimeout = null;
        TimeSpan? syncTimeout = null;
        var protocol = RespProtocol.Auto;
        var allowAdmin = false;
        var mode = new ConnectionStringMode();
        SslClientAuthenticationOptions? tlsOptions = null;
        string? sentinelSslHost = null;

        var segments = connectionString.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                endpointTexts.Add(segment);
                continue;
            }

            var name = segment[..equals].Trim();
            var value = segment[(equals + 1)..].Trim();
            if (mode.TryApply(name, value))
            {
                continue;
            }

            switch (name.ToLowerInvariant())
            {
                case "user":
                case "username":
                    username = value;
                    break;
                case "password":
                    password = value;
                    break;
                case "ssl":
                    useTls = ParseBooleanOption(name, value);
                    break;
                case "name":
                case "clientname":
                    clientName = value;
                    break;
                case "defaultdatabase":
                case "db":
                    database = ParseIntegerOption(name, value);
                    break;
                case "connecttimeout":
                    connectTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "asynctimeout":
                    asyncTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "synctimeout":
                    syncTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "protocol":
                    protocol = ParseProtocolOption(name, value);
                    break;
                case "sslhost":
                    RequireOptionValue(name, value);
                    (tlsOptions ??= new()).TargetHost = value;
                    break;
                case "sentinelsslhost":
                    RequireOptionValue(name, value);
                    sentinelSslHost = value;
                    break;
                case "sslprotocols":
                    (tlsOptions ??= new()).EnabledSslProtocols = ParseSslProtocols(value);
                    break;
                case "checkcertificaterevocation":
                    (tlsOptions ??= new()).CertificateRevocationCheckMode = ParseBooleanOption(name, value)
                        ? X509RevocationMode.Online
                        : X509RevocationMode.NoCheck;
                    break;
                case "allowadmin":
                    allowAdmin = ParseBooleanOption(name, value);
                    break;
                default:
                    throw new ArgumentException(
                        $"StackExchange.Redis connection string option '{name}' is not supported. " +
                        "Use a redis:// URI or configure RespireOptions directly.",
                        nameof(connectionString));
            }
        }

        if (endpointTexts.Count == 0)
        {
            throw new ArgumentException(
                "A StackExchange.Redis connection string must contain at least one endpoint.",
                nameof(connectionString));
        }

        mode.Validate();

        if (endpointTexts.Count > 1 && !mode.UseCluster && mode.ServiceName is null)
        {
            throw new ArgumentException(
                "Connection strings with multiple endpoints require cluster=true for Cluster seeds " +
                "or serviceName for Sentinel discovery. Use RespireClient.ConnectAnyAsync with separate " +
                "RespireOptions for connection-time fallback between standalone deployments.",
                nameof(connectionString));
        }

        var defaultPort = mode.ServiceName is null ? 6379 : 26379;
        var endpoints = endpointTexts.Select(endpoint => RespireEndpoint.Parse(endpoint, defaultPort)).ToList();

        return new RespireOptions
        {
            Endpoints = endpoints,
            Username = username,
            Password = password,
            ClientName = clientName,
            Database = database,
            ConnectTimeout = connectTimeout,
            UseTls = useTls ?? !string.IsNullOrEmpty(tlsOptions?.TargetHost),
            TlsOptions = tlsOptions,
            UseCluster = mode.UseCluster,
            SentinelPrimaryName = mode.ServiceName,
            SentinelUsername = mode.SentinelUsername,
            SentinelPassword = mode.SentinelPassword,
            SentinelUseTls = mode.SentinelUseTls ?? (sentinelSslHost is null ? null : true),
            SentinelTlsOptions = sentinelSslHost is null ? null
                : RespireConnection.CreateTlsOptions(tlsOptions, sentinelSslHost, overrideTargetHost: true),
            CommandTimeout = asyncTimeout ?? syncTimeout ?? DefaultCommandTimeout,
            Protocol = protocol,
            AllowAdmin = allowAdmin,
        }.ValidateAndSnapshot();
    }
}
