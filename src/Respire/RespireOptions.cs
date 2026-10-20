using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

/// <summary>A Redis TCP endpoint, or a Unix domain socket whose Host is its path and Port is zero.</summary>
public readonly record struct RespireEndpoint(string Host, int Port = 6379)
{
    /// <summary>Whether this endpoint identifies a Unix domain socket rather than a TCP port.</summary>
    public bool IsUnixSocket => Port == 0;

    /// <summary>Creates a Unix domain socket endpoint. Relative paths are resolved against the current directory.</summary>
    public static RespireEndpoint UnixSocket(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0'))
            throw new ArgumentException("A Unix socket path cannot contain NUL characters.", nameof(path));
        if (!path.StartsWith('/') && !Path.IsPathFullyQualified(path))
            path = Path.GetFullPath(path);
        if (!IsValidUnixPath(path))
            throw new ArgumentException("A Unix socket requires a non-root filesystem path.", nameof(path));
        return new RespireEndpoint(path, 0);
    }

    internal static bool IsValidUnixPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.Length > 1 && !path.Contains('\0')
            && (path.StartsWith('/') || Path.IsPathFullyQualified(path));

    internal static bool IsUnixScheme(string scheme)
        => scheme.Equals("unix", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("redis+unix", StringComparison.OrdinalIgnoreCase);

    internal static RespireEndpoint FromUnixUri(Uri uri)
    {
        if (uri.Host.Length != 0 || uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0)
            throw new ArgumentException("A Unix socket URI must have a local path without authority or fragment.", nameof(uri));
        return UnixSocket(Uri.UnescapeDataString(uri.AbsolutePath));
    }

    /// <summary>Parses a TCP endpoint, a unix:/// or redis+unix:/// URI, or a StackExchange.Redis !path socket endpoint.</summary>
    public static RespireEndpoint Parse(string value) => Parse(value, 6379);

    internal static RespireEndpoint Parse(string value, int defaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();

        if (value.StartsWith("!@", StringComparison.Ordinal))
            throw new RespireConfigurationException("Endpoint '!@name' uses a Linux abstract Unix socket, which Respire does not support. Use a filesystem socket (!/path) instead.");
        if (value[0] == '!') return UnixSocket(value[1..]);
        if (value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("redis+unix://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(value, UriKind.Absolute);
            if (uri.Query.Length != 0)
                throw new ArgumentException("Endpoint URIs cannot contain options; use RespireOptions.Parse.", nameof(value));
            return FromUnixUri(uri);
        }

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

    /// <summary>Parses a TCP or Unix socket endpoint string.</summary>
    public static implicit operator RespireEndpoint(string value) => Parse(value);

    /// <inheritdoc/>
    public override string ToString()
    {
        if (IsUnixSocket) return $"!{Host}";
        return Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }
}

/// <summary>RESP protocol selection or automatic negotiation policy.</summary>
public enum RespireProtocol
{
    /// <summary>Prefer RESP3; use RESP2 only when the server rejects HELLO as unsupported.</summary>
    Auto = 0,

    /// <summary>RESP2, supported by Redis 2.0 and later.</summary>
    Resp2 = 2,

    /// <summary>HELLO 3 — required for push-based features such as client-side caching. Redis 6+.</summary>
    Resp3 = 3,
}

/// <summary>What happens when a subscription's buffer is full because the consumer is slow.</summary>
public enum RespireSubscriptionOverflow
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
    /// <summary>Include the conflicting physical key as hex in local cross-slot exceptions. Default: false.</summary>
    /// <remarks>Explicit diagnostic opt-in: keys may contain secrets or personal data.
    /// Hex is reversible. Key context is never added to telemetry tags or exception messages.</remarks>
    public bool IncludeCrossSlotKeyContext { get; init; }

    /// <summary>Split immediate cross-slot MGET, MSET, DEL, UNLINK, EXISTS and TOUCH commands by slot. Default: false.</summary>
    /// <remarks>Groups run concurrently and are not atomic together. A failure can leave some writes applied.
    /// Transactions and deferred batches still require each command's keys to share one slot.</remarks>
    public bool SplitCrossSlotCommands { get; init; }

    /// <summary>Returns a diagnostic representation without credentials or nested configuration material.</summary>
    public override string ToString() => "RespireOptions { <redacted> }";

    private const string ConnectionStringParameterName = "connectionString";

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Optionally opens a connected duplex stream for each physical connection, before TLS and Redis setup.</summary>
    /// <remarks>
    /// Receives the destination endpoint and the combined caller/connect-timeout token. Calls may run concurrently
    /// and repeat for reconnects, discovered nodes, Sentinel, pub/sub, and dedicated connections.
    /// Return a fresh readable and writable stream; Respire owns and disposes it, including on setup failure.
    /// Writes must reach the peer without flushing; Respire does not call Flush or FlushAsync on the stream.
    /// Dispose resources yourself if the factory throws. Observe cancellation promptly. Respire applies
    /// <see cref="UseTls"/> and <see cref="TlsOptions"/> to the returned stream. Address mapping applies before
    /// the factory runs; TLS keeps the logical host unless <see cref="UseMappedHostForTls"/> selects the mapped host.
    /// The factory owns DNS, socket options, and tunnel negotiation; built-in TCP options do not configure its stream.
    /// Null uses Respire's ordinary socket transport.
    /// </remarks>
    public Func<RespireEndpoint, CancellationToken, ValueTask<Stream>>? ConnectionFactory { get; init; }

    // Compatibility adapter for friend tests. All connections use the public factory path.
    internal Func<string, int, CancellationToken, ValueTask<Stream>>? TestingStreamFactory
    {
        get => ConnectionFactory is { } factory ? (host, port, token) => factory(new(host, port), token) : null;
        init => ConnectionFactory = value is { } factory ? (endpoint, token) => factory(endpoint.Host, endpoint.Port, token) : null;
    }
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

    /// <summary>Maps logical TCP endpoints to reachable socket addresses. Unlisted endpoints are unchanged.</summary>
    /// <remarks>Applies to seeds, discovered nodes, redirects, replicas, Sentinel peers, and maintenance handoffs.
    /// Routing, diagnostics, and TLS retain the original endpoint. Host matching ignores case; ports match exactly.
    /// The map is copied when connecting.
    /// Cannot be combined with <see cref="NodeAddressResolver"/>.</remarks>
    public IReadOnlyDictionary<RespireEndpoint, RespireEndpoint>? NodeAddressMap { get; init; }

    /// <summary>Resolves each logical TCP endpoint immediately before connecting its socket.</summary>
    /// <remarks>Return the input for unchanged endpoints. The callback must be thread-safe and return a valid TCP endpoint.
    /// Cannot be combined with <see cref="NodeAddressMap"/>. Unix sockets bypass address mapping.</remarks>
    public Func<RespireEndpoint, RespireEndpoint>? NodeAddressResolver { get; init; }

    /// <summary>Use the mapped socket hostname for TLS SNI and certificate validation. Default: false.</summary>
    /// <remarks>An explicit <see cref="SslClientAuthenticationOptions.TargetHost"/> still takes precedence.</remarks>
    public bool UseMappedHostForTls { get; init; }

    /// <summary>Optional key namespace for the root client. Empty (the default) disables prefixing.</summary>
    /// <remarks>Text and binary prefixes follow the same contracts as <see cref="IRespireClient.WithKeyPrefix(RespireKey)"/>.
    /// Client creation snapshots binary storage. Derived views append to this prefix.
    /// Regular pub/sub channels and server-wide operations are unchanged.</remarks>
    public RespireKey KeyPrefix { get; init; }

    /// <summary>Optional, separate namespace for typed pub/sub publishes and subscriptions. Empty disables prefixing.</summary>
    /// <remarks>Binary storage is copied at client creation. Derived pub/sub views append to this prefix.
    /// Text must be valid UTF-16. Keys, notification descriptors and raw/server commands are unchanged.
    /// Received messages and subscription targets retain physical wire identity.</remarks>
    public RespireKey PubSubPrefix { get; init; }

    /// <summary>Default routing policy for catalog commands whose metadata confirms they are read-only.</summary>
    public RespireReadFrom ReadFrom { get; init; } = RespireReadFrom.Primary;

    /// <summary>
    /// Optional latency band for replica selection and <see cref="RespireReadFrom.Nearest"/>.
    /// Within the best role, replication-link and availability-zone tier, rotate among measured
    /// candidates whose PING RTT is at most this much above the fastest. Null preserves existing
    /// routing; zero selects only fastest ties. Must be nonnegative. Unknown RTTs rotate only
    /// when the tier has no measured candidate. Pending probes remain excluded.
    /// </summary>
    public TimeSpan? ReplicaLatencyTolerance { get; init; }

    /// <summary>Optional bounded duplication of slow idempotent reads. Null (the default) disables hedging.</summary>
    [Experimental("RSP0003", UrlFormat = "https://respire.tomlonghurst.dev/docs/diagnostics/{0}")]
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
    /// Library name advertised with CLIENT SETINFO, alongside this package's version. Defaults to Respire.
    /// Wrappers can use a name such as Respire(se-compat). Set null to disable both metadata commands.
    /// Server errors from this optional setup are ignored. Names must be nonempty and contain only ASCII characters from ! through ~.
    /// </summary>
    public string? LibraryName { get; init; } = "Respire";

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
    public RespireProtocol Protocol { get; init; } = RespireProtocol.Auto;

    /// <summary>Opt-in RESP3 maintenance notifications. Disabled by default; Auto tolerates an unsupported server.</summary>
    public RespireMaintenanceNotificationMode MaintenanceNotifications { get; init; }

    /// <summary>Minimum command/receive timeout during maintenance. Never shortens an existing timeout.</summary>
    public TimeSpan MaintenanceRelaxedTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum maintenance window when its completion notification is lost.</summary>
    public TimeSpan MaintenanceWindowTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // Shared by all data transports of one failover candidate, including replacements.
    // RespireFailoverGroup replaces this value with its own tracker when it snapshots each member.
    internal FailoverMaintenanceWindows? FailoverMaintenance { get; init; }

    /// <summary>Timeout for the initial TCP connect (per connection).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum age before a multiplexed connection is replaced or an idle dedicated connection is closed. Null disables recycling.</summary>
    /// <remarks>Replacement connects first and drains accepted work without replay. Busy dedicated leases finish before recycling.</remarks>
    public TimeSpan? ConnectionMaxLifetime { get; init; }

    /// <summary>Random additional lifetime, uniformly selected between zero and this value for each new connection. Defaults to zero.</summary>
    public TimeSpan ConnectionMaxLifetimeJitter { get; init; }

    internal TimeProvider ConnectionLifetimeTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Optional command, dedicated, and pub/sub recovery backoff and Cluster/Sentinel discovery policy. Null preserves existing scheduling.</summary>
    /// <remarks>Dedicated rentals retry failed acquisition with independent budgets. Sentinel fallback shares one budget per resolution.
    /// Cluster discovery shares one fallback budget across nested node and seed selection per round.</remarks>
    public RespireReconnectPolicy? ReconnectPolicy { get; init; }

    /// <summary>Dedicated connection limits and idle retention for blocking commands, transactions and streaming leases.</summary>
    public RespireDedicatedPoolOptions DedicatedPool { get; init; } = new();

    /// <summary>Admission policy during standalone, Sentinel primary, replica-read and Cluster recovery. Defaults to bounded FIFO waiting.</summary>
    /// <remarks>Cluster queues preserve slot ordering and reselect the current owner and configured read policy.
    /// Replica reads reapply their original policy to the current topology. Does not replay accepted commands
    /// or watched transactions. Dedicated connections retain their existing recovery behavior.</remarks>
    public RespireBacklogPolicy BacklogPolicy { get; init; } = RespireBacklogPolicy.Queue;

    /// <summary>Maximum waiting operations per standalone endpoint, Sentinel primary, replica router or Cluster slot during reconnect.
    /// A batch slot group or unwatched transaction counts as one.
    /// Defaults to 1024; overflow fails with <see cref="RespireConnectionException"/>.</summary>
    public int BacklogCapacity { get; init; } = 1024;
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

    // Test seam: advances READONLY recovery phases independently of socket setup.
    internal TimeProvider ClusterRecoveryClock { get; init; } = TimeProvider.System;

    // Test seam: ceiling on replica missing-function retries, before any shorter CommandTimeout.
    internal TimeSpan FunctionPropagationLimit { get; init; } = FunctionCommands.DefaultFunctionPropagationLimit;

    /// <summary>Optional hook around every physical connection attempt, including reconnects and topology connections.</summary>
    /// <remarks>Null adds no hook context, delegate or async-wrapper allocations. Callbacks may run concurrently.</remarks>
    public RespireConnectionHook? ConnectionHook { get; init; }

    /// <summary>Use TLS. Enabled automatically for <c>rediss://</c> connection strings.</summary>
    public bool UseTls { get; init; }

    /// <summary>Optional TLS authentication and certificate-validation settings.</summary>
    public SslClientAuthenticationOptions? TlsOptions { get; init; }

    /// <summary>
    /// Aborts a connection when commands are awaiting replies and no bytes arrive within this
    /// period. Null (default) disables the receive watchdog.
    /// Also supplies the gathered-write stall bound when <see cref="GatheredWriteStallTimeout"/>
    /// is null, preserving the combined watchdog behavior for existing callers.
    /// </summary>
    public TimeSpan? ConnectionIdleReadTimeout
    {
        get => _connectionIdleReadTimeout;
        init => _connectionIdleReadTimeout = value;
    }

    /// <summary>
    /// Aborts the whole connection when borrowed large-SET memory is queued or being sent and
    /// no socket send completes within this period. Null (default) inherits
    /// <see cref="ConnectionIdleReadTimeout"/>; <see cref="Timeout.InfiniteTimeSpan"/> disables
    /// only the write bound. A finite value must be at least one millisecond and overrides the
    /// receive timeout. Paused stream consumers suppress only receive checks.
    /// </summary>
    /// <remarks>
    /// Watches from queue admission until every byte in the owning buffer is sent, including
    /// preceding copied writes and trailing commands. Completed sends advance progress;
    /// additional commands do not extend an existing stall. Abort fails other pending commands,
    /// and Redis execution may be uncertain. Keep caller memory unchanged until public completion,
    /// which still waits for the kernel to release it, even after cancellation or a deadline.
    /// With both bounds disabled, memory can remain owned until the peer resumes or the connection closes.
    /// </remarks>
    public TimeSpan? GatheredWriteStallTimeout { get; init; }

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

    /// <summary>Optional admission and completion hook for explicit pipelines and transaction commits.</summary>
    /// <remarks>Shared by client views. Does not intercept individual commands. Null adds no hook state or async wrapper.</remarks>
    public RespirePipelineHook? PipelineHook { get; init; }

    /// <summary>Optional admission/completion hook for ordinary command dispatch. Null disables it.</summary>
    /// <remarks>See <see cref="RespireCommandHook"/> for supported command and resource boundaries; this is not a client-wide blocking policy.</remarks>
    public RespireCommandHook? CommandHook { get; init; }

    /// <summary>
    /// Opt-in retries for buffered immediate commands on standalone, Sentinel and Cluster clients,
    /// and complete unwatched transactions on standalone and Sentinel clients.
    /// Null disables retries.
    /// </summary>
    /// <remarks>One deadline spans all attempts. Caller-supplied commands, scripts and functions retry only
    /// with a declared category. Every transaction member must permit replay of the whole unit.
    /// Watched transaction helpers replay whole callbacks when every queued category permits the
    /// commit's write outcome. Standalone and Sentinel batches retry as complete units only when
    /// every member's category is permitted. Direct watched commits, Cluster batches and transactions,
    /// hash import sessions, cursor continuations and streams retain their existing behavior.
    /// A lost EXEC reply can mean execution;
    /// permitting accumulating writes accepts repeated transaction effects.
    /// Retrying cannot recover a lost reply or guarantee exactly-once execution.</remarks>
    public RespireRetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Producer identity attached to automatic idempotent stream appends. Null (the default)
    /// generates a random identity once per client.
    /// </summary>
    /// <remarks>
    /// When <see cref="RetryPolicy"/> selects connection-loss retries and permits
    /// <see cref="RespireCommandRetryCategory.WriteChecked"/> or a higher write category, typed
    /// <c>Streams.AddAsync</c> calls without explicit idempotency or entry ID send
    /// <c>XADD key IDMP producer message-id</c> on Redis 8.6+. A resend after a lost reply then
    /// returns the original entry instead of appending another. Each call uses a new random
    /// message ID, so a stable producer identity across restarts cannot collide with earlier
    /// appends. Servers that reject IDMP receive ordinary XADD, which keeps its conservative
    /// <see cref="RespireCommandRetryCategory.WriteAccumulating"/> category.
    /// </remarks>
    public string? StreamProducerId { get; init; }

    /// <summary>Optional factory for Respire diagnostic logs.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    // Retained through record copies so callers can attach a logger after parsing.
    internal string[] IgnoredConnectionStringOptions { get; init; } = [];

    /// <summary>
    /// Enables TCP keepalive on every connection and sets how long a connection may sit idle
    /// before the kernel starts probing. Whole seconds, minimum one second. Null (the default)
    /// leaves keepalive off. Recommended (e.g. 60 seconds) when connections idle behind NATs
    /// or load balancers that silently drop stale flows.
    /// The upper bound is OS-dependent: Linux permits at most 32,767 seconds. Values outside
    /// the OS range can fail connection creation with a SocketException.
    /// </summary>
    public TimeSpan? TcpKeepAliveTime { get; init; }

    /// <summary>
    /// Interval between keepalive probes once <see cref="TcpKeepAliveTime"/> has elapsed
    /// without traffic. Whole seconds, minimum one second. Null keeps the OS default; requires
    /// <see cref="TcpKeepAliveTime"/>.
    /// The upper bound is OS-dependent: Linux permits at most 32,767 seconds. Values outside
    /// the OS range can fail connection creation with a SocketException.
    /// </summary>
    public TimeSpan? TcpKeepAliveInterval { get; init; }

    /// <summary>
    /// Unanswered keepalive probes before the kernel declares the connection dead. Null keeps
    /// the OS default; requires <see cref="TcpKeepAliveTime"/>.
    /// The upper bound is OS-dependent: Linux permits at most 127 probes. Values outside
    /// the OS range can fail connection creation with a SocketException.
    /// </summary>
    public int? TcpKeepAliveRetryCount { get; init; }

    /// <summary>Buffered messages per subscription before <see cref="SubscriptionOverflow"/> applies.</summary>
    public int SubscriptionBufferSize { get; init; } = 1024;

    /// <summary>Policy used when a subscription consumer falls behind.</summary>
    public RespireSubscriptionOverflow SubscriptionOverflow { get; init; } = RespireSubscriptionOverflow.DropOldest;

    // Advanced wire tuning — the defaults are right for almost everyone.

    /// <summary>Initial size of the pooled parse buffer each connection reads into.</summary>
    public int ReceiveBufferSize { get; init; } = 64 * 1024;

    /// <summary>Initial size of each connection's two coalescing write buffers.</summary>
    public int WriteBufferSize { get; init; } = 64 * 1024;

    /// <summary>Maximum commands awaiting responses per connection.</summary>
    public int MaxInflightCommands { get; init; } = 16 * 1024;

    internal RespireEndpoint PrimaryEndpoint
        => Endpoints.Count > 0 ? Endpoints[0] : new RespireEndpoint("localhost");

    /// <summary>Opt-in circuit admission for standalone, Sentinel and Cluster data commands. Null disables circuit breaking.</summary>
    /// <remarks>Configured standalone replica routing is not supported. Sentinel and Cluster data endpoints
    /// have independent histories; Sentinel discovery and primary validation are not gated. Each batch command acquires
    /// admission independently; a transaction acquires one permit for its MULTI/EXEC sequence.
    /// WATCH setup and durability acknowledgements acquire independent admission.</remarks>
    public RespireCircuitBreakerOptions? CircuitBreaker { get; init; }

    internal RespireOptions ValidateAndSnapshot()
    {
        if (NodeAddressMap is not null && NodeAddressResolver is not null)
            throw new RespireConfigurationException("Specify either NodeAddressMap or NodeAddressResolver, not both.");
        var addressMap = NodeAddressMapping.Snapshot(NodeAddressMap);
        if (Endpoints is null || Endpoints.Count == 0)
        {
            throw new RespireConfigurationException("At least one Redis endpoint is required.");
        }
        if (ReplicaEndpoints is null)
            throw new RespireConfigurationException("RespireOptions.ReplicaEndpoints cannot be null.");

        if (UseCluster && !string.IsNullOrWhiteSpace(SentinelPrimaryName))
            throw new RespireConfigurationException("Cluster and Sentinel routing cannot be enabled together.");

        Require(Enum.IsDefined(ReadFrom), nameof(ReadFrom), "must be a defined RespireReadFrom policy");
        Require(ReplicaLatencyTolerance is null || ReplicaLatencyTolerance >= TimeSpan.Zero,
            nameof(ReplicaLatencyTolerance), "must be nonnegative when provided");
        Require(ClientAvailabilityZone is null || !string.IsNullOrWhiteSpace(ClientAvailabilityZone),
            nameof(ClientAvailabilityZone), "must be nonempty when provided");
        Require(LibraryName is null || LibraryName.Length > 0 && LibraryName.All(static character => character is >= '!' and <= '~'),
            nameof(LibraryName), "must be nonempty and contain only ASCII characters from ! through ~ when provided");
        Require(ReadFrom is not (RespireReadFrom.AzAffinity or RespireReadFrom.AzAffinityReplicasAndPrimary)
            || ClientAvailabilityZone is not null, nameof(ClientAvailabilityZone), "is required for AZ-affinity reads");
        HedgedReads?.Validate();
        CircuitBreaker?.Validate();
        RetryPolicy?.Validate();
        Require(StreamProducerId is null || StreamProducerId.Length != 0,
            nameof(StreamProducerId), "must be nonempty when provided");
        Require(CircuitBreaker is null || ReplicaEndpoints.Count == 0,
            nameof(CircuitBreaker), "requires standalone, Sentinel or Cluster routing without configured replica endpoints");
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

        Require(Protocol is RespireProtocol.Auto or RespireProtocol.Resp2 or RespireProtocol.Resp3, nameof(Protocol), "must be Auto, Resp2, or Resp3");
        var effectiveProtocol = ClientSideCache is null ? Protocol : RespireProtocol.Resp3;
        Require(Enum.IsDefined(MaintenanceNotifications), nameof(MaintenanceNotifications), "must be Disabled, Auto, or Enabled");
        Require(MaintenanceNotifications != RespireMaintenanceNotificationMode.Enabled || effectiveProtocol != RespireProtocol.Resp2,
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
        Require(ConnectionMaxLifetime is null || ConnectionMaxLifetime >= TimeSpan.FromMilliseconds(1),
            nameof(ConnectionMaxLifetime), "must be at least one millisecond or null");
        Require(ConnectionMaxLifetimeJitter >= TimeSpan.Zero
                && (ConnectionMaxLifetime is { } lifetime
                    ? ConnectionMaxLifetimeJitter <= TimeSpan.MaxValue - lifetime
                    : ConnectionMaxLifetimeJitter == TimeSpan.Zero),
            nameof(ConnectionMaxLifetimeJitter), "must be non-negative, fit within the total lifetime, and be zero when ConnectionMaxLifetime is null");
        if (DedicatedPool is null) throw new RespireConfigurationException("RespireOptions.DedicatedPool cannot be null.");
        DedicatedPool.Validate();
        Require(Enum.IsDefined(BacklogPolicy), nameof(BacklogPolicy), "must be a defined backlog policy");
        Require(BacklogCapacity > 0, nameof(BacklogCapacity), "must be positive");
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
        Require(
            GatheredWriteStallTimeout is null || GatheredWriteStallTimeout == Timeout.InfiniteTimeSpan
                || GatheredWriteStallTimeout >= TimeSpan.FromMilliseconds(1),
            nameof(GatheredWriteStallTimeout), "must be at least one millisecond, Timeout.InfiniteTimeSpan, or null");
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
            // OPTIN/OPTOUT Cluster ASK redirects can send ASKING, CACHING, and the read atomically.
            var hasCachingPrelude = cache.TrackingMode != RespireClientTrackingMode.Broadcast;
            var requiredInflightCommands = hasCachingPrelude ? 2 : 1;
            if (UseCluster) requiredInflightCommands = hasCachingPrelude ? 3 : 2;
            var requirement = requiredInflightCommands switch
            {
                3 => "must be at least three for OPTIN/OPTOUT caching with Cluster ASK redirects",
                2 => "must be at least two for OPTIN/OPTOUT caching or Cluster ASK redirects",
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
            if (endpoint.IsUnixSocket)
            {
                ValidateUnixEndpoint(endpoint, nameof(Endpoints));
                continue;
            }
            if (endpoint.Port is < 1 or > 65535)
            {
                throw new RespireConfigurationException(
                    $"RespireOptions.Endpoints contains invalid TCP port {endpoint.Port}.");
            }
        }

        foreach (var endpoint in ReplicaEndpoints)
        {
            if (endpoint.IsUnixSocket)
            {
                ValidateUnixEndpoint(endpoint, nameof(ReplicaEndpoints));
                continue;
            }
            if (endpoint.Port is < 1 or > 65535)
                throw new RespireConfigurationException($"RespireOptions.ReplicaEndpoints contains invalid TCP port {endpoint.Port}.");
        }

        if (PubSubPrefix.Text is { } pubSubPrefixText) Internal.Utf8RouteName.Validate(pubSubPrefixText);
        return this with
        {
            Endpoints = new List<RespireEndpoint>(Endpoints),
            ReplicaEndpoints = new List<RespireEndpoint>(ReplicaEndpoints),
            NodeAddressMap = addressMap,
            Protocol = effectiveProtocol,
            ClientSideCache = ClientSideCache?.ValidateAndSnapshot(),
            KeyPrefix = KeyPrefix.IsEmpty ? default : KeyPrefix.Snapshot(),
            PubSubPrefix = PubSubPrefix.IsEmpty ? default : PubSubPrefix.Snapshot(),
        };
    }

    private void ValidateUnixEndpoint(RespireEndpoint endpoint, string optionName)
    {
        UnixSocketConfiguration.Validate(endpoint.Host, UseTls, MaintenanceNotifications, $"RespireOptions.{optionName}");
        if (UseCluster || !string.IsNullOrWhiteSpace(SentinelPrimaryName))
            throw new RespireConfigurationException("Unix socket endpoints cannot be mixed with Cluster or Sentinel discovery, which advertises TCP endpoints.");
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
        => ToTransportConnectionOptions() with
        {
            ConnectionMaxLifetime = ConnectionMaxLifetime,
            ConnectionMaxLifetimeJitter = ConnectionMaxLifetimeJitter,
            ConnectionLifetimeTimeProvider = ConnectionLifetimeTimeProvider,
            MaintenanceNotifications = enableMaintenanceNotifications ? MaintenanceNotifications : RespireMaintenanceNotificationMode.Disabled,
            FailoverMaintenance = enableMaintenanceNotifications ? FailoverMaintenance : null,
            ClientName = ClientName,
            LibraryName = LibraryName,
            Database = Database,
            DedicatedPool = DedicatedPool,
            RequireClusterDatabaseSupport = UseCluster && Database != 0,
            DiscoverAvailabilityZone = ClientAvailabilityZone is not null,
            Protocol = Protocol,
            PushHandler = pushHandler,
            EnableClientTracking = enableClientTracking,
            ClientTrackingOptions = enableClientTracking && ClientSideCache is { } cache
                ? new(cache.TrackingMode, cache.KeyPrefixes, cache.NoLoop) : default,
        };

    // BUSY permits AUTH but can block all other setup. Construct control options from
    // transport/authentication fields only, so new ordinary setup is not inherited.
    internal RespireConnectionOptions ToControlConnectionOptions()
        => ToTransportConnectionOptions() with { Protocol = RespireProtocol.Resp2 };

    private RespireConnectionOptions ToTransportConnectionOptions()
        => new()
        {
            NodeAddressMap = NodeAddressMap,
            NodeAddressResolver = NodeAddressResolver,
            UseMappedHostForTls = UseMappedHostForTls,
            MaintenanceRelaxedTimeout = MaintenanceRelaxedTimeout,
            MaintenanceWindowTimeout = MaintenanceWindowTimeout,
            ConnectionFactory = ConnectionFactory,
            ConnectionHook = ConnectionHook,
            ConnectTimeout = ConnectTimeout,
            ReconnectPolicy = ReconnectPolicy,
            ResponseTimeout = ConnectionIdleReadTimeout,
            GatheredWriteStallTimeout = GatheredWriteStallTimeout,
            CommandTimeout = CommandTimeout,
            UseTls = UseTls,
            TlsOptions = TlsOptions,
            Username = Username,
            Password = Password,
            CredentialProvider = CredentialProvider,
            CredentialRefreshBeforeExpiry = CredentialRefreshBeforeExpiry,
            CredentialRefreshRetryDelay = CredentialRefreshRetryDelay,
            CredentialTimeProvider = CredentialTimeProvider,
            TcpKeepAliveTime = TcpKeepAliveTime,
            TcpKeepAliveInterval = TcpKeepAliveInterval,
            TcpKeepAliveRetryCount = TcpKeepAliveRetryCount,
            ReceiveBufferSize = ReceiveBufferSize,
            WriteBufferSize = WriteBufferSize,
            MaxInflightCommands = MaxInflightCommands,
        };

    /// <summary>
    /// Parses a connection string: "host", "host:port", a StackExchange.Redis-compatible
    /// comma-delimited string, or a <c>redis://[user[:password]@]host[:port][/database]</c> URI.
    /// Comma-delimited strings accept multiple endpoints only with <c>cluster=true</c> or
    /// <c>serviceName</c>. Options include <c>user</c>, <c>password</c>, <c>ssl</c>,
    /// <c>sslHost</c>, <c>sslProtocols</c>, <c>checkCertificateRevocation</c>, <c>clientName</c>
    /// (or <c>name</c>), <c>defaultDatabase</c>, <c>connectTimeout</c>, <c>asyncTimeout</c>,
    /// <c>syncTimeout</c>, <c>protocol</c>, <c>allowAdmin</c>, <c>keyPrefix</c>, <c>pubSubPrefix</c>, and Sentinel credentials/TLS.
    /// Recognized URI query parameters:
    /// <c>clientName</c>, <c>connections</c>, <c>connectTimeoutMs</c>, <c>commandTimeoutMs</c>,
    /// <c>connectionIdleReadTimeoutMs</c>, <c>gatheredWriteStallTimeoutMs</c> (-1 disables the
    /// gathered-write bound; omitted inherits <c>connectionIdleReadTimeoutMs</c>),
    /// <c>protocol</c> (2/resp2 or 3/resp3), <c>db</c>,
    /// <c>useCluster</c> (true or false), <c>sentinelPrimaryName</c>, <c>sentinelUser</c>,
    /// <c>sentinelPassword</c>, <c>sentinelTls</c> (true or false),
    /// <c>nodeAddressMap</c> (semicolon-separated source=target TCP endpoints), <c>useMappedHostForTls</c>,
    /// <c>allowAdmin</c> (true or false), <c>keyPrefix</c>, and <c>pubSubPrefix</c>.
    /// In both forms, keyPrefix and pubSubPrefix are percent-decoded once and used as independent UTF-8 text namespaces.
    /// The comma-delimited channelPrefix alias is literal text.
    /// Empty disables prefixing; duplicate values use the last entry. Escape delimiters and literal percent signs.
    /// Use <c>rediss://</c> to enable TLS. Unix sockets accept <c>unix:///path</c>,
    /// <c>redis+unix:///path</c>, or <c>!/path</c> in comma-delimited strings.
    /// A Unix URI uses its path for the socket and <c>?db=N</c> for the database.
    /// In comma-delimited strings, <c>sslHost</c> enables TLS unless <c>ssl=false</c> is explicit;
    /// <c>sentinelSslHost</c> similarly enables Sentinel TLS unless <c>sentinelTls=false</c> is explicit.
    /// URI connections contain one endpoint. Comma-delimited seed lists preserve endpoint order;
    /// they never imply failover between independent standalone deployments.
    /// Comma-delimited strings also map channelPrefix, keepAlive, connectRetry, responseTimeout,
    /// configCheckSeconds, and maintNotifications. abortConnect=false accepts lazy Create semantics;
    /// ConnectAsync always waits. Ignored keys log once per client through LoggerFactory.
    /// </summary>
    /// <exception cref="RespireConfigurationException">A connection string setting or endpoint is invalid.</exception>
    public static RespireOptions Parse(string connectionString)
    {
        try
        {
            return ParseCore(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new RespireConfigurationException($"Invalid connectionString: {exception.Message}", exception);
        }
    }

    private static RespireOptions ParseCore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (connectionString.TrimStart().StartsWith('!'))
            return ParseStackExchangeConnectionString(connectionString);

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
        var useUnixSocket = RespireEndpoint.IsUnixScheme(uri.Scheme);
        if (!useUnixSocket && !useTls && !uri.Scheme.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported scheme '{uri.Scheme}' — expected redis://, rediss://, unix:// or redis+unix://.", nameof(connectionString));
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
        if (!useUnixSocket && path.Length > 0)
        {
            database = ParseNonNegativeIntegerOption("database", path);
        }

        string? clientName = null;
        RespireKey keyPrefix = default;
        RespireKey pubSubPrefix = default;
        var connections = 1;
        TimeSpan connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? commandTimeout = DefaultCommandTimeout;
        TimeSpan? responseTimeout = null;
        TimeSpan? gatheredWriteStallTimeout = null;
        var protocol = RespireProtocol.Auto;
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
                case "keyprefix":
                    keyPrefix = value;
                    break;
                case "pubsubprefix":
                    pubSubPrefix = value;
                    break;
                case "connections":
                    connections = ParseIntegerOption(name, value);
                    if (connections < 1)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(connectionString), $"Option '{name}' must be at least one.");
                    }

                    break;
                case "connecttimeoutms":
                    connectTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "commandtimeoutms":
                    commandTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "responsetimeoutms":
                case "connectionidlereadtimeoutms":
                    responseTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "protocol":
                    protocol = ParseProtocolOption(name, value);
                    break;
                case "gatheredwritestalltimeoutms":
                    gatheredWriteStallTimeout = TimeSpan.FromMilliseconds(ParseIntegerOption(name, value));
                    break;
                case "db":
                    database = ParseNonNegativeIntegerOption(name, value);
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
            Endpoints = { useUnixSocket ? RespireEndpoint.FromUnixUri(uri)
                : new RespireEndpoint(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port) },
            Username = username,
            Password = password,
            SentinelUsername = mode.SentinelUsername,
            SentinelPassword = mode.SentinelPassword,
            SentinelUseTls = mode.SentinelUseTls,
            NodeAddressMap = mode.NodeAddressMap,
            UseMappedHostForTls = mode.UseMappedHostForTls,
            ClientName = clientName,
            KeyPrefix = keyPrefix,
            PubSubPrefix = pubSubPrefix,
            Database = database,
            Connections = connections,
            ConnectTimeout = connectTimeout,
            UseTls = useTls,
            CommandTimeout = commandTimeout,
            ConnectionIdleReadTimeout = responseTimeout,
            GatheredWriteStallTimeout = gatheredWriteStallTimeout,
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
        public IReadOnlyDictionary<RespireEndpoint, RespireEndpoint>? NodeAddressMap { get; private set; }
        public bool UseMappedHostForTls { get; private set; }

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
                case "nodeaddressmap":
                    NodeAddressMap = NodeAddressMapping.Parse(value);
                    break;
                case "usemappedhostfortls":
                    UseMappedHostForTls = ParseBooleanOption(name, value);
                    break;
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

    private static RespireProtocol ParseProtocolOption(string name, string value)
        => value.ToLowerInvariant() switch
        {
            "auto" => RespireProtocol.Auto,
            "2" or "resp2" => RespireProtocol.Resp2,
            "3" or "resp3" => RespireProtocol.Resp3,
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
        RespireKey keyPrefix = default;
        RespireKey pubSubPrefix = default;
        var database = 0;
        bool? useTls = null;
        var connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? asyncTimeout = null;
        TimeSpan? syncTimeout = null;
        var protocol = RespireProtocol.Auto;
        var allowAdmin = false;
        var mode = new ConnectionStringMode();
        SslClientAuthenticationOptions? tlsOptions = null;
        string? sentinelSslHost = null;
        TimeSpan? keepAlive = null;
        TimeSpan? responseTimeout = null;
        TimeSpan? topologyRefresh = TimeSpan.FromSeconds(60);
        var maintenanceTimeout = TimeSpan.FromSeconds(30);
        var maintenanceWindow = TimeSpan.FromSeconds(60);
        RespireReconnectPolicy? reconnectPolicy = null;
        var maintenance = RespireMaintenanceNotificationMode.Disabled;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                case "keyprefix":
                    keyPrefix = Uri.UnescapeDataString(value);
                    break;
                case "pubsubprefix":
                    pubSubPrefix = Uri.UnescapeDataString(value);
                    break;
                case "channelprefix":
                    // StackExchange.Redis channel prefixes are literal, not URI-escaped.
                    pubSubPrefix = value;
                    break;
                case "abortconnect":
                    // Create is always lazy; ConnectAsync always waits for a connection.
                    if (ParseBooleanOption(name, value)) ignored.Add(name);
                    else ignored.Remove(name);
                    break;
                case "keepalive":
                    var seconds = ParseIntegerOption(name, value);
                    if (seconds < -1)
                        throw new RespireConfigurationException($"Option '{name}' requires -1, zero, or a positive number of seconds.");
                    keepAlive = seconds <= 0 ? null : TimeSpan.FromSeconds(seconds);
                    break;
                case "connectretry":
                    reconnectPolicy = new RespireReconnectPolicy { MaxAttempts = ParsePositiveIntegerOption(name, value) };
                    break;
                case "responsetimeout":
                    responseTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "configcheckseconds":
                case "topologyrefreshseconds":
                    var refreshSeconds = ParseIntegerOption(name, value);
                    if (refreshSeconds < 0)
                        throw new RespireConfigurationException($"Option '{name}' requires a non-negative number of seconds.");
                    topologyRefresh = TimeSpan.FromSeconds(refreshSeconds);
                    break;
                case "maintnotifications":
                    if (!Enum.TryParse(value, true, out maintenance) || !Enum.IsDefined(maintenance))
                        throw new RespireConfigurationException($"Option '{name}' requires Disabled, Auto, or Enabled.");
                    break;
                case "maintrelaxedtimeout":
                    maintenanceTimeout = ParseMaintenanceSeconds(name, value);
                    break;
                case "maintrelaxedwindowmax":
                    maintenanceWindow = ParseMaintenanceSeconds(name, value);
                    break;
                case "proxy":
                    if (!value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        throw new RespireConfigurationException($"Option '{name}' only supports None; proxy command routing is not supported.");
                    break;
                case "tunnel":
                    if (value.Length != 0)
                        throw new RespireConfigurationException($"Option '{name}' is not supported; configure a direct Redis endpoint.");
                    break;
                case "defaultdatabase":
                case "db":
                    database = ParseNonNegativeIntegerOption(name, value);
                    break;
                case "connecttimeout":
                    connectTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "asynctimeout":
                    asyncTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
                    break;
                case "synctimeout":
                    syncTimeout = TimeSpan.FromMilliseconds(ParsePositiveIntegerOption(name, value));
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
                    if (name.StartsWith('$'))
                        throw new RespireConfigurationException($"Option '{name}' remaps a Redis command, which Respire does not support.");
                    if (name.Length == 0)
                        throw new RespireConfigurationException("A connection string option key cannot be empty.");
                    ignored.Add(name);
                    break;
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

        var effectiveUseTls = useTls ?? !string.IsNullOrEmpty(tlsOptions?.TargetHost);
        var defaultPort = mode.ServiceName is not null ? 26379 : effectiveUseTls ? 6380 : 6379;
        var endpoints = endpointTexts.Select(endpoint => RespireEndpoint.Parse(endpoint, defaultPort)).ToList();

        if (maintenance == RespireMaintenanceNotificationMode.Enabled && protocol == RespireProtocol.Resp2)
            throw new RespireConfigurationException("Option 'maintNotifications=Enabled' requires RESP3; protocol=2 is incompatible.");

        return new RespireOptions
        {
            Endpoints = endpoints,
            Username = username,
            Password = password,
            ClientName = clientName,
            KeyPrefix = keyPrefix,
            PubSubPrefix = pubSubPrefix,
            Database = database,
            ConnectTimeout = connectTimeout,
            UseTls = effectiveUseTls,
            TlsOptions = tlsOptions,
            UseCluster = mode.UseCluster,
            SentinelPrimaryName = mode.ServiceName,
            SentinelUsername = mode.SentinelUsername,
            NodeAddressMap = mode.NodeAddressMap,
            UseMappedHostForTls = mode.UseMappedHostForTls,
            SentinelPassword = mode.SentinelPassword,
            SentinelUseTls = mode.SentinelUseTls ?? (sentinelSslHost is null ? null : true),
            SentinelTlsOptions = sentinelSslHost is null ? null
                : RespireConnection.CreateTlsOptions(tlsOptions, sentinelSslHost, overrideTargetHost: true),
            CommandTimeout = asyncTimeout ?? syncTimeout ?? DefaultCommandTimeout,
            Protocol = protocol,
            AllowAdmin = allowAdmin,
            TcpKeepAliveTime = keepAlive,
            ConnectionIdleReadTimeout = responseTimeout,
            ClusterTopologyRefreshInterval = topologyRefresh,
            ReconnectPolicy = reconnectPolicy,
            MaintenanceNotifications = maintenance,
            MaintenanceRelaxedTimeout = maintenanceTimeout,
            MaintenanceWindowTimeout = maintenanceWindow,
            IgnoredConnectionStringOptions = ignored.ToArray(),
        }.ValidateAndSnapshot();
    }

    private static int ParsePositiveIntegerOption(string name, string value)
    {
        var result = ParseIntegerOption(name, value);
        if (result <= 0)
            throw new RespireConfigurationException($"Option '{name}' requires a positive 32-bit integer.");
        return result;
    }

    private static int ParseNonNegativeIntegerOption(string name, string value)
    {
        var result = ParseIntegerOption(name, value);
        if (result < 0)
            throw new RespireConfigurationException($"Option '{name}' requires a non-negative 32-bit integer.");
        return result;
    }

    private static TimeSpan ParseMaintenanceSeconds(string name, string value)
    {
        var seconds = ParsePositiveIntegerOption(name, value);
        if (seconds > 86400)
            throw new RespireConfigurationException($"Option '{name}' requires at most 86400 seconds (one day).");
        return TimeSpan.FromSeconds(seconds);
    }
}
