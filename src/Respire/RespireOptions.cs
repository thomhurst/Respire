using System.Globalization;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Serialization;

namespace Respire;

/// <summary>A Redis endpoint (host and port).</summary>
public readonly record struct RespireEndpoint(string Host, int Port = 6379)
{
    /// <summary>Parses "host" or "host:port".</summary>
    public static RespireEndpoint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value.AsSpan(colon + 1), out var port))
        {
            return new RespireEndpoint(value[..colon], port);
        }

        return new RespireEndpoint(value);
    }

    public static implicit operator RespireEndpoint(string value) => Parse(value);

    public override string ToString() => $"{Host}:{Port}";
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
    /// <summary>
    /// Servers to connect to. In cluster mode these are seed nodes; otherwise the first endpoint is used.
    /// </summary>
    public IList<RespireEndpoint> Endpoints { get; init; } = [];

    /// <summary>
    /// Enables Redis Cluster routing. MOVED and ASK redirects are followed automatically and
    /// learned hash slots are routed directly on later commands.
    /// </summary>
    public bool Cluster { get; init; }

    /// <summary>ACL username. Defaults to Redis's "default" user when only a password is set.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>When set, CLIENT SETNAME runs during the handshake — invaluable in CLIENT LIST.</summary>
    public string? ClientName { get; init; }

    /// <summary>Logical database SELECTed during the handshake.</summary>
    public int Database { get; init; }

    public RespProtocol Protocol { get; init; } = RespProtocol.Resp2;

    /// <summary>Timeout for the initial TCP connect (per connection).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Client-side cap on how long a command waits for its response. Null (default) disables the
    /// cap. Expiry of the cap throws <see cref="RespireTimeoutException"/>; the command may still
    /// execute server-side. Does not apply to intentionally blocking calls (BLPOP-style waits).
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>
    /// Multiplexed connections to open; 0 (the default) opens one. A single connection
    /// maximizes command coalescing per syscall and is fastest for typical workloads;
    /// raise it only if profiling shows a single socket saturated.
    /// </summary>
    public int Connections { get; init; }

    /// <summary>Serializer behind non-primitive typed values. System.Text.Json by default.</summary>
    public IRespireSerializer Serializer { get; init; } = RespireSerializer.Default;

    public ILoggerFactory? LoggerFactory { get; init; }

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

    internal ILogger? CreateLogger(string category) => LoggerFactory?.CreateLogger(category);

    internal RespireConnectionOptions ToConnectionOptions(RespirePushHandler? pushHandler = null)
        => new()
        {
            ConnectTimeout = ConnectTimeout,
            Username = Username,
            Password = Password,
            ClientName = ClientName,
            Database = Database,
            UseResp3 = Protocol == RespProtocol.Resp3,
            ReceiveBufferSize = ReceiveBufferSize,
            WriteBufferSize = WriteBufferSize,
            MaxInflightCommands = MaxInflightCommands,
            PushHandler = pushHandler,
        };

    /// <summary>
    /// Parses a connection string: "host", "host:port", or a
    /// <c>redis://[user[:password]@]host[:port][/database]</c> URI. Recognized query parameters:
    /// <c>clientName</c>, <c>connections</c>, <c>connectTimeoutMs</c>, <c>commandTimeoutMs</c>,
    /// <c>protocol</c> (2 or 3), <c>db</c>, <c>cluster</c> (true or false).
    /// <c>rediss://</c> (TLS) is not supported yet.
    /// </summary>
    public static RespireOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!connectionString.Contains("://", StringComparison.Ordinal))
        {
            return new RespireOptions { Endpoints = { RespireEndpoint.Parse(connectionString) } };
        }

        var uri = new Uri(connectionString, UriKind.Absolute);
        if (uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "rediss:// (TLS) is not supported yet — track https://github.com/thomhurst/Respire/issues for progress.");
        }

        if (!uri.Scheme.Equals("redis", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported scheme '{uri.Scheme}' — expected redis://.", nameof(connectionString));
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
        var connections = 0;
        TimeSpan connectTimeout = TimeSpan.FromSeconds(10);
        TimeSpan? commandTimeout = null;
        var protocol = RespProtocol.Resp2;
        var cluster = false;

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
                    break;
                case "connecttimeoutms":
                    connectTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "commandtimeoutms":
                    commandTimeout = TimeSpan.FromMilliseconds(int.Parse(value, CultureInfo.InvariantCulture));
                    break;
                case "protocol":
                    protocol = value == "3" ? RespProtocol.Resp3 : RespProtocol.Resp2;
                    break;
                case "db":
                    database = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "cluster":
                    cluster = bool.Parse(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown connection string parameter '{name}'.", nameof(connectionString));
            }
        }

        return new RespireOptions
        {
            Endpoints = { new RespireEndpoint(uri.Host, uri.IsDefaultPort ? 6379 : uri.Port) },
            Username = username,
            Password = password,
            ClientName = clientName,
            Database = database,
            Connections = connections,
            ConnectTimeout = connectTimeout,
            CommandTimeout = commandTimeout,
            Protocol = protocol,
            Cluster = cluster,
        };
    }
}
