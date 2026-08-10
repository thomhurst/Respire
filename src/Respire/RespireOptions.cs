using System.ComponentModel;
using System.Globalization;
using System.Net.Security;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

/// <summary>A Redis endpoint (host and port).</summary>
public readonly record struct RespireEndpoint(string Host, int Port = 6379)
{
    /// <summary>Parses "host", "host:port", or an IPv6 address.</summary>
    public static RespireEndpoint Parse(string value)
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
                return new RespireEndpoint(host);
            }

            if (value[closingBracket + 1] != ':' ||
                !int.TryParse(value.AsSpan(closingBracket + 2), CultureInfo.InvariantCulture, out var ipv6Port))
            {
                throw new ArgumentException($"Invalid IPv6 endpoint '{value}'.", nameof(value));
            }

            return new RespireEndpoint(host, ipv6Port);
        }

        var colon = value.IndexOf(':');
        if (colon > 0 && colon == value.LastIndexOf(':') &&
            int.TryParse(value.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var port))
        {
            return new RespireEndpoint(value[..colon], port);
        }

        return new RespireEndpoint(value);
    }

    public static implicit operator RespireEndpoint(string value) => Parse(value);

    public override string ToString() => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";
}

/// <summary>RESP protocol version negotiated during the handshake.</summary>
public enum RespProtocol
{
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
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);
    private bool _useCluster;
    private string? _sentinelPrimaryName;
    private TimeSpan? _connectionIdleReadTimeout;

    /// <summary>
    /// Servers to connect to. In cluster mode these are seed nodes; otherwise the first endpoint is used.
    /// </summary>
    public IList<RespireEndpoint> Endpoints { get; init; } = [];

    /// <summary>
    /// Enables Redis Cluster routing. MOVED and ASK redirects are followed automatically and
    /// learned hash slots are routed directly on later commands.
    /// </summary>
    public bool UseCluster
    {
        get => _useCluster;
        init => _useCluster = value;
    }

    /// <summary>Compatibility alias for <see cref="UseCluster"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool Cluster
    {
        get => _useCluster;
        init => _useCluster = value;
    }

    /// <summary>
    /// Redis Sentinel primary service name. When set, <see cref="RespireClient.ConnectAsync(RespireOptions, CancellationToken)"/>
    /// treats <see cref="Endpoints"/> as Sentinel endpoints and discovers the current primary
    /// before opening Redis connections.
    /// </summary>
    public string? SentinelPrimaryName
    {
        get => _sentinelPrimaryName;
        init => _sentinelPrimaryName = value;
    }

    /// <summary>Compatibility alias for <see cref="SentinelPrimaryName"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? ServiceName
    {
        get => _sentinelPrimaryName;
        init => _sentinelPrimaryName = value;
    }

    /// <summary>ACL username. Defaults to Redis's "default" user when only a password is set.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

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

    /// <summary>Logical database SELECTed during the handshake.</summary>
    public int Database { get; init; }

    /// <summary>Allows high-risk administrative commands such as FLUSHDB, FLUSHALL, and CONFIG SET.</summary>
    public bool AllowAdmin { get; init; }

    public RespProtocol Protocol { get; init; } = RespProtocol.Resp2;

    /// <summary>Timeout for the initial TCP connect (per connection).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

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

    /// <summary>Compatibility alias for <see cref="ConnectionIdleReadTimeout"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TimeSpan? ResponseTimeout
    {
        get => _connectionIdleReadTimeout;
        init => _connectionIdleReadTimeout = value;
    }

    /// <summary>
    /// Client-side cap on how long a command waits for its response. Defaults to ten seconds;
    /// null disables the cap. Expiry throws <see cref="RespireTimeoutException"/>; the command may still
    /// execute server-side. Does not apply to intentionally blocking calls (BLPOP-style waits).
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; } = DefaultCommandTimeout;

    /// <summary>
    /// Multiplexed connections to open. Defaults to one. A single connection maximizes command
    /// coalescing per syscall and is fastest for typical workloads; under a 50-worker stress test,
    /// one connection doubled small-command PING throughput versus eight. Raise this only if
    /// profiling shows a single socket saturated.
    /// </summary>
    public int Connections { get; init; } = 1;

    /// <summary>Serializer behind non-primitive typed values. System.Text.Json by default.</summary>
    public IRespireSerializer Serializer { get; init; } = RespireSerializer.Default;

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

        if (Connections < 1)
        {
            throw new RespireConfigurationException("RespireOptions.Connections must be at least one.");
        }

        return this with { Endpoints = new List<RespireEndpoint>(Endpoints) };
    }

    internal ILogger? CreateLogger(string category) => LoggerFactory?.CreateLogger(category);

    internal RespireConnectionOptions ToConnectionOptions(RespirePushHandler? pushHandler = null)
        => new()
        {
            ConnectTimeout = ConnectTimeout,
            ResponseTimeout = ConnectionIdleReadTimeout,
            UseTls = UseTls,
            TlsOptions = TlsOptions,
            Username = Username,
            Password = Password,
            ClientName = ClientName,
            Database = Database,
            UseResp3 = Protocol == RespProtocol.Resp3,
            TcpKeepAliveTime = TcpKeepAliveTime,
            TcpKeepAliveInterval = TcpKeepAliveInterval,
            TcpKeepAliveRetryCount = TcpKeepAliveRetryCount,
            ReceiveBufferSize = ReceiveBufferSize,
            WriteBufferSize = WriteBufferSize,
            MaxInflightCommands = MaxInflightCommands,
            PushHandler = pushHandler,
        };

    /// <summary>
    /// Parses a connection string: "host", "host:port", a StackExchange.Redis-compatible
    /// comma-delimited string, or a <c>redis://[user[:password]@]host[:port][/database]</c> URI.
    /// Comma-delimited strings support one endpoint and the options <c>user</c>,
    /// <c>password</c>, <c>ssl</c>, <c>clientName</c>, <c>defaultDatabase</c>,
    /// <c>connectTimeout</c>, <c>asyncTimeout</c>, <c>syncTimeout</c>, <c>protocol</c>,
    /// and <c>allowAdmin</c>. Recognized URI query parameters:
    /// <c>clientName</c>, <c>connections</c>, <c>connectTimeoutMs</c>, <c>commandTimeoutMs</c>,
    /// <c>connectionIdleReadTimeoutMs</c>, <c>protocol</c> (2 or 3), <c>db</c>,
    /// <c>useCluster</c> (true or false), <c>sentinelPrimaryName</c>, <c>sentinelUser</c>,
    /// <c>sentinelPassword</c>, <c>sentinelTls</c> (true or false), and
    /// <c>allowAdmin</c> (true or false).
    /// Use <c>rediss://</c> to enable TLS.
    /// Connection strings contain one endpoint; configure <see cref="Endpoints"/> directly when
    /// cluster seed failover requires multiple endpoints.
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
        if (path.Length > 0 && !int.TryParse(path, out database))
        {
            throw new ArgumentException($"Invalid database index '{path}' in connection string.", nameof(connectionString));
        }

        string? clientName = null;
        var connections = 1;
        TimeSpan connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? commandTimeout = DefaultCommandTimeout;
        TimeSpan? responseTimeout = null;
        var protocol = RespProtocol.Resp2;
        var cluster = false;
        string? serviceName = null;
        string? sentinelUser = null;
        string? sentinelPassword = null;
        bool? sentinelUseTls = null;
        var allowAdmin = false;

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (name.ToLowerInvariant())
            {
                case "clientname":
                    clientName = value;
                    break;
                case "connections":
                    connections = int.Parse(value, CultureInfo.InvariantCulture);
                    if (connections < 1)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(connectionString), "Connection count must be at least one.");
                    }

                    break;
                case "connecttimeoutms":
                    connectTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "commandtimeoutms":
                    commandTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "responsetimeoutms":
                case "connectionidlereadtimeoutms":
                    responseTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "protocol":
                    protocol = value == "3" ? RespProtocol.Resp3 : RespProtocol.Resp2;
                    break;
                case "db":
                    database = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "cluster":
                case "usecluster":
                    cluster = bool.Parse(value);
                    break;
                case "servicename":
                case "sentinelprimaryname":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        throw new ArgumentException(
                            "Connection string parameter 'serviceName' cannot be empty.",
                            nameof(connectionString));
                    }

                    serviceName = value;
                    break;
                case "sentineluser":
                    sentinelUser = value;
                    break;
                case "sentinelpassword":
                    sentinelPassword = value;
                    break;
                case "sentineltls":
                    sentinelUseTls = bool.Parse(value);
                    break;
                case "allowadmin":
                    allowAdmin = bool.Parse(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown connection string parameter '{name}'.", nameof(connectionString));
            }
        }

        var defaultPort = serviceName is null ? 6379 : 26379;
        return new RespireOptions
        {
            Endpoints = { new RespireEndpoint(uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port) },
            Username = username,
            Password = password,
            SentinelUsername = sentinelUser,
            SentinelPassword = sentinelPassword,
            SentinelUseTls = sentinelUseTls,
            ClientName = clientName,
            Database = database,
            Connections = connections,
            ConnectTimeout = connectTimeout,
            UseTls = useTls,
            CommandTimeout = commandTimeout,
            ConnectionIdleReadTimeout = responseTimeout,
            Protocol = protocol,
            UseCluster = cluster,
            SentinelPrimaryName = serviceName,
            AllowAdmin = allowAdmin,
        }.ValidateAndSnapshot();
    }

    private static RespireOptions ParseStackExchangeConnectionString(string connectionString)
    {
        List<RespireEndpoint> endpoints = [];
        string? username = null;
        string? password = null;
        string? clientName = null;
        var database = 0;
        var useTls = false;
        var connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? asyncTimeout = null;
        TimeSpan? syncTimeout = null;
        var protocol = RespProtocol.Resp2;
        var allowAdmin = false;

        var segments = connectionString.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var equals = segment.IndexOf('=');
            if (equals < 0)
            {
                endpoints.Add(RespireEndpoint.Parse(segment));
                continue;
            }

            var name = segment[..equals].Trim();
            var value = segment[(equals + 1)..].Trim();
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
                    useTls = bool.Parse(value);
                    break;
                case "clientname":
                    clientName = value;
                    break;
                case "defaultdatabase":
                case "db":
                    database = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "connecttimeout":
                    connectTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "asynctimeout":
                    asyncTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "synctimeout":
                    syncTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "protocol":
                    protocol = value.ToLowerInvariant() switch
                    {
                        "2" or "resp2" => RespProtocol.Resp2,
                        "3" or "resp3" => RespProtocol.Resp3,
                        _ => throw new ArgumentException(
                            $"Unsupported protocol '{value}' in connection string.", nameof(connectionString)),
                    };
                    break;
                case "allowadmin":
                    allowAdmin = bool.Parse(value);
                    break;
                default:
                    throw new ArgumentException(
                        $"StackExchange.Redis connection string option '{name}' is not supported. " +
                        "Use a redis:// URI or configure RespireOptions directly.",
                        nameof(connectionString));
            }
        }

        if (endpoints.Count == 0)
        {
            throw new ArgumentException(
                "A StackExchange.Redis connection string must contain at least one endpoint.",
                nameof(connectionString));
        }

        if (endpoints.Count > 1)
        {
            throw new ArgumentException(
                "StackExchange.Redis connection strings with multiple endpoints are not supported. " +
                "Configure RespireOptions directly and select Redis Cluster or Sentinel mode.",
                nameof(connectionString));
        }

        return new RespireOptions
        {
            Endpoints = endpoints,
            Username = username,
            Password = password,
            ClientName = clientName,
            Database = database,
            ConnectTimeout = connectTimeout,
            UseTls = useTls,
            CommandTimeout = asyncTimeout ?? syncTimeout ?? DefaultCommandTimeout,
            Protocol = protocol,
            AllowAdmin = allowAdmin,
        }.ValidateAndSnapshot();
    }
}
