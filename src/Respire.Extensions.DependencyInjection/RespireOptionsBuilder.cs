using System.Net.Security;
using Microsoft.Extensions.Logging;
using Respire.Serialization;

namespace Respire.Extensions.DependencyInjection;

/// <summary>
/// Mutable builder for <see cref="RespireOptions"/>. It is used by the action-based dependency
/// injection overload while keeping <see cref="RespireOptions"/> itself immutable after creation.
/// </summary>
public sealed class RespireOptionsBuilder
{
    public IList<RespireEndpoint> Endpoints { get; } = [];

    public bool Cluster { get; set; }

    public string? ServiceName { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? SentinelUsername { get; set; }

    public string? SentinelPassword { get; set; }

    public bool? SentinelUseTls { get; set; }

    public SslClientAuthenticationOptions? SentinelTlsOptions { get; set; }

    public string? ClientName { get; set; }

    public int Database { get; set; }

    public bool AllowAdmin { get; set; }

    public RespProtocol Protocol { get; set; } = RespProtocol.Resp2;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool UseTls { get; set; }

    public SslClientAuthenticationOptions? TlsOptions { get; set; }

    public TimeSpan? ResponseTimeout { get; set; }

    public TimeSpan? CommandTimeout { get; set; }

    public int Connections { get; set; }

    public IRespireSerializer Serializer { get; set; } = RespireSerializer.Default;

    public ILoggerFactory? LoggerFactory { get; set; }

    public TimeSpan? TcpKeepAliveTime { get; set; }

    public TimeSpan? TcpKeepAliveInterval { get; set; }

    public int? TcpKeepAliveRetryCount { get; set; }

    public int SubscriptionBufferSize { get; set; } = 1024;

    public SubscriptionOverflow SubscriptionOverflow { get; set; } = SubscriptionOverflow.DropOldest;

    public int ReceiveBufferSize { get; set; } = 64 * 1024;

    public int WriteBufferSize { get; set; } = 64 * 1024;

    public int MaxInflightCommands { get; set; } = 16 * 1024;

    internal RespireOptions Build() => new()
    {
        Endpoints = Endpoints.ToArray(),
        Cluster = Cluster,
        ServiceName = ServiceName,
        Username = Username,
        Password = Password,
        SentinelUsername = SentinelUsername,
        SentinelPassword = SentinelPassword,
        SentinelUseTls = SentinelUseTls,
        SentinelTlsOptions = SentinelTlsOptions,
        ClientName = ClientName,
        Database = Database,
        AllowAdmin = AllowAdmin,
        Protocol = Protocol,
        ConnectTimeout = ConnectTimeout,
        UseTls = UseTls,
        TlsOptions = TlsOptions,
        ResponseTimeout = ResponseTimeout,
        CommandTimeout = CommandTimeout,
        Connections = Connections,
        Serializer = Serializer,
        LoggerFactory = LoggerFactory,
        TcpKeepAliveTime = TcpKeepAliveTime,
        TcpKeepAliveInterval = TcpKeepAliveInterval,
        TcpKeepAliveRetryCount = TcpKeepAliveRetryCount,
        SubscriptionBufferSize = SubscriptionBufferSize,
        SubscriptionOverflow = SubscriptionOverflow,
        ReceiveBufferSize = ReceiveBufferSize,
        WriteBufferSize = WriteBufferSize,
        MaxInflightCommands = MaxInflightCommands,
    };
}
