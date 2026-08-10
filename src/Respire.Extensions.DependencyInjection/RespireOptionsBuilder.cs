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
    private bool _useCluster;
    private string? _sentinelPrimaryName;
    private TimeSpan? _connectionIdleReadTimeout;

    /// <inheritdoc cref="RespireOptions.Endpoints"/>
    public IList<RespireEndpoint> Endpoints { get; } = [];

    /// <inheritdoc cref="RespireOptions.UseCluster"/>
    public bool UseCluster
    {
        get => _useCluster;
        set => _useCluster = value;
    }

    /// <inheritdoc cref="RespireOptions.SentinelPrimaryName"/>
    public string? SentinelPrimaryName
    {
        get => _sentinelPrimaryName;
        set => _sentinelPrimaryName = value;
    }

    /// <inheritdoc cref="RespireOptions.Username"/>
    public string? Username { get; set; }

    /// <inheritdoc cref="RespireOptions.Password"/>
    public string? Password { get; set; }

    /// <inheritdoc cref="RespireOptions.SentinelUsername"/>
    public string? SentinelUsername { get; set; }

    /// <inheritdoc cref="RespireOptions.SentinelPassword"/>
    public string? SentinelPassword { get; set; }

    /// <inheritdoc cref="RespireOptions.SentinelUseTls"/>
    public bool? SentinelUseTls { get; set; }

    /// <inheritdoc cref="RespireOptions.SentinelTlsOptions"/>
    public SslClientAuthenticationOptions? SentinelTlsOptions { get; set; }

    /// <inheritdoc cref="RespireOptions.ClientName"/>
    public string? ClientName { get; set; }

    /// <inheritdoc cref="RespireOptions.Database"/>
    public int Database { get; set; }

    /// <inheritdoc cref="RespireOptions.AllowAdmin"/>
    public bool AllowAdmin { get; set; }

    /// <inheritdoc cref="RespireOptions.Protocol"/>
    public RespProtocol Protocol { get; set; } = RespProtocol.Resp2;

    /// <inheritdoc cref="RespireOptions.ConnectTimeout"/>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc cref="RespireOptions.UseTls"/>
    public bool UseTls { get; set; }

    /// <inheritdoc cref="RespireOptions.TlsOptions"/>
    public SslClientAuthenticationOptions? TlsOptions { get; set; }

    /// <inheritdoc cref="RespireOptions.ConnectionIdleReadTimeout"/>
    public TimeSpan? ConnectionIdleReadTimeout
    {
        get => _connectionIdleReadTimeout;
        set => _connectionIdleReadTimeout = value;
    }

    /// <inheritdoc cref="RespireOptions.CommandTimeout"/>
    public TimeSpan? CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc cref="RespireOptions.Connections"/>
    public int Connections { get; set; } = 1;

    /// <inheritdoc cref="RespireOptions.Serializer"/>
    public IRespireSerializer Serializer { get; set; } = RespireSerializer.Default;

    /// <inheritdoc cref="RespireOptions.LoggerFactory"/>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <inheritdoc cref="RespireOptions.TcpKeepAliveTime"/>
    public TimeSpan? TcpKeepAliveTime { get; set; }

    /// <inheritdoc cref="RespireOptions.TcpKeepAliveInterval"/>
    public TimeSpan? TcpKeepAliveInterval { get; set; }

    /// <inheritdoc cref="RespireOptions.TcpKeepAliveRetryCount"/>
    public int? TcpKeepAliveRetryCount { get; set; }

    /// <inheritdoc cref="RespireOptions.SubscriptionBufferSize"/>
    public int SubscriptionBufferSize { get; set; } = 1024;

    /// <inheritdoc cref="RespireOptions.SubscriptionOverflow"/>
    public SubscriptionOverflow SubscriptionOverflow { get; set; } = SubscriptionOverflow.DropOldest;

    /// <inheritdoc cref="RespireOptions.ReceiveBufferSize"/>
    public int ReceiveBufferSize { get; set; } = 64 * 1024;

    /// <inheritdoc cref="RespireOptions.WriteBufferSize"/>
    public int WriteBufferSize { get; set; } = 64 * 1024;

    /// <inheritdoc cref="RespireOptions.MaxInflightCommands"/>
    public int MaxInflightCommands { get; set; } = 16 * 1024;

    internal RespireOptions Build() => new()
    {
        Endpoints = Endpoints.ToArray(),
        UseCluster = UseCluster,
        SentinelPrimaryName = SentinelPrimaryName,
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
        ConnectionIdleReadTimeout = ConnectionIdleReadTimeout,
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
