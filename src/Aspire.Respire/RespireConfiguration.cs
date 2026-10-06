using System.Globalization;
using System.Net.Security;
using Microsoft.Extensions.Configuration;
using global::Respire;

namespace Aspire.Respire;

// Explicit mapping keeps configuration binding trim-safe and avoids binding service interfaces,
// callbacks, certificate handles, or binary key internals as though they were configuration objects.
internal static class RespireConfiguration
{
    internal static RespireOptions Apply(IConfigurationSection section, RespireOptions options) => options with
    {
        Endpoints = Endpoints(section.GetSection("Endpoints"), options.Endpoints),
        ReplicaEndpoints = Endpoints(section.GetSection("ReplicaEndpoints"), options.ReplicaEndpoints),
        ReadFrom = section.GetValue("ReadFrom", options.ReadFrom),
        HedgedReads = HedgedReads(section.GetSection("HedgedReads"), options.HedgedReads),
        ClientAvailabilityZone = section.GetValue("ClientAvailabilityZone", options.ClientAvailabilityZone),
        ReplicaRefreshInterval = section.GetValue("ReplicaRefreshInterval", options.ReplicaRefreshInterval),
        UseCluster = section.GetValue("UseCluster", options.UseCluster),
        SentinelPrimaryName = section.GetValue("SentinelPrimaryName", options.SentinelPrimaryName),
        Username = section.GetValue("Username", options.Username),
        Password = section.GetValue("Password", options.Password),
        CredentialRefreshBeforeExpiry = section.GetValue("CredentialRefreshBeforeExpiry", options.CredentialRefreshBeforeExpiry),
        CredentialRefreshRetryDelay = section.GetValue("CredentialRefreshRetryDelay", options.CredentialRefreshRetryDelay),
        SentinelUsername = section.GetValue("SentinelUsername", options.SentinelUsername),
        SentinelPassword = section.GetValue("SentinelPassword", options.SentinelPassword),
        SentinelUseTls = section.GetValue("SentinelUseTls", options.SentinelUseTls),
        SentinelTlsOptions = Tls(section.GetSection("SentinelTlsOptions"), options.SentinelTlsOptions),
        ClientName = section.GetValue("ClientName", options.ClientName),
        Database = section.GetValue("Database", options.Database),
        AllowAdmin = section.GetValue("AllowAdmin", options.AllowAdmin),
        Protocol = section.GetValue("Protocol", options.Protocol),
        MaintenanceNotifications = section.GetValue("MaintenanceNotifications", options.MaintenanceNotifications),
        MaintenanceRelaxedTimeout = section.GetValue("MaintenanceRelaxedTimeout", options.MaintenanceRelaxedTimeout),
        MaintenanceWindowTimeout = section.GetValue("MaintenanceWindowTimeout", options.MaintenanceWindowTimeout),
        ConnectTimeout = section.GetValue("ConnectTimeout", options.ConnectTimeout),
        ReconnectPolicy = ReconnectPolicy(section.GetSection("ReconnectPolicy"), options.ReconnectPolicy),
        ClusterTopologyRefreshInterval = OptionalTimeSpan(section, "ClusterTopologyRefreshInterval", options.ClusterTopologyRefreshInterval),
        UseTls = section.GetValue("UseTls", options.UseTls),
        TlsOptions = Tls(section.GetSection("TlsOptions"), options.TlsOptions),
        ConnectionIdleReadTimeout = OptionalTimeSpan(section, "ConnectionIdleReadTimeout", options.ConnectionIdleReadTimeout),
        CommandTimeout = OptionalTimeSpan(section, "CommandTimeout", options.CommandTimeout),
        ThreadPoolMonitoring = section.GetValue("ThreadPoolMonitoring", options.ThreadPoolMonitoring),
        ThreadPoolWarningThreshold = section.GetValue("ThreadPoolWarningThreshold", options.ThreadPoolWarningThreshold),
        Connections = section.GetValue("Connections", options.Connections),
        ClientSideCache = ClientSideCache(section.GetSection("ClientSideCache"), options.ClientSideCache),
        TcpKeepAliveTime = OptionalTimeSpan(section, "TcpKeepAliveTime", options.TcpKeepAliveTime),
        TcpKeepAliveInterval = OptionalTimeSpan(section, "TcpKeepAliveInterval", options.TcpKeepAliveInterval),
        TcpKeepAliveRetryCount = OptionalInt32(section, "TcpKeepAliveRetryCount", options.TcpKeepAliveRetryCount),
        SubscriptionBufferSize = section.GetValue("SubscriptionBufferSize", options.SubscriptionBufferSize),
        SubscriptionOverflow = section.GetValue("SubscriptionOverflow", options.SubscriptionOverflow),
        ReceiveBufferSize = section.GetValue("ReceiveBufferSize", options.ReceiveBufferSize),
        WriteBufferSize = section.GetValue("WriteBufferSize", options.WriteBufferSize),
        MaxInflightCommands = section.GetValue("MaxInflightCommands", options.MaxInflightCommands),
    };

    private static IList<RespireEndpoint> Endpoints(IConfigurationSection section, IList<RespireEndpoint> fallback)
    {
        if (!section.Exists()) return fallback;
        return section.GetChildren().Select(ParseEndpoint).ToList();
    }

    private static RespireEndpoint ParseEndpoint(IConfigurationSection endpoint)
        => endpoint.Value is { } value ? RespireEndpoint.Parse(value)
            : new RespireEndpoint(endpoint["Host"] ?? throw new InvalidOperationException($"{endpoint.Path}:Host is required."),
                int.Parse(endpoint["Port"] ?? "6379", CultureInfo.InvariantCulture));

    private static RespireHedgedReadOptions? HedgedReads(IConfigurationSection section, RespireHedgedReadOptions? options)
    {
        if (!section.Exists()) return options;
        options ??= new();
        return options with
        {
            Delay = section.GetValue("Delay", options.Delay),
            MaximumExtraLoadPercent = section.GetValue("MaximumExtraLoadPercent", options.MaximumExtraLoadPercent),
        };
    }

    private static RespireReconnectPolicy? ReconnectPolicy(IConfigurationSection section, RespireReconnectPolicy? options)
    {
        if (!section.Exists()) return options;
        options ??= new();
        return options with
        {
            InitialDelay = section.GetValue("InitialDelay", options.InitialDelay),
            BackoffMultiplier = section.GetValue("BackoffMultiplier", options.BackoffMultiplier),
            MaxDelay = section.GetValue("MaxDelay", options.MaxDelay),
            JitterRatio = section.GetValue("JitterRatio", options.JitterRatio),
            MaxAttempts = OptionalInt32(section, "MaxAttempts", options.MaxAttempts),
        };
    }

    private static RespireClientSideCacheOptions? ClientSideCache(IConfigurationSection section, RespireClientSideCacheOptions? options)
    {
        if (!section.Exists()) return options;
        options ??= new();
        var prefixes = section.GetSection("KeyPrefixes");
        return options with
        {
            MaxEntries = section.GetValue("MaxEntries", options.MaxEntries),
            MaxSizeBytes = section.GetValue("MaxSizeBytes", options.MaxSizeBytes),
            LocalExpiration = OptionalTimeSpan(section, "LocalExpiration", options.LocalExpiration),
            KeyPrefixes = prefixes.Exists() ? prefixes.GetChildren().Select(prefix => (RespireKey)(prefix.Value
                ?? throw new InvalidOperationException($"{prefix.Path} must be a string."))).ToArray() : options.KeyPrefixes,
            TrackingMode = section.GetValue("TrackingMode", options.TrackingMode),
            CoalesceConcurrentMisses = section.GetValue("CoalesceConcurrentMisses", options.CoalesceConcurrentMisses),
            ReuseHashFields = section.GetValue("ReuseHashFields", options.ReuseHashFields),
        };
    }

    private static SslClientAuthenticationOptions? Tls(IConfigurationSection section, SslClientAuthenticationOptions? options)
    {
        if (!section.Exists()) return options;
        options ??= new();
        // Connection-string parsing can supply a TLS target host. Preserve it when only another
        // TLS field is overridden; callers configure certificate objects through configureOptions.
        return new SslClientAuthenticationOptions
        {
            TargetHost = section.GetValue("TargetHost", options.TargetHost),
            EnabledSslProtocols = section.GetValue("EnabledSslProtocols", options.EnabledSslProtocols),
            CertificateRevocationCheckMode = section.GetValue("CertificateRevocationCheckMode", options.CertificateRevocationCheckMode),
            EncryptionPolicy = section.GetValue("EncryptionPolicy", options.EncryptionPolicy),
            AllowRenegotiation = section.GetValue("AllowRenegotiation", options.AllowRenegotiation),
            AllowTlsResume = section.GetValue("AllowTlsResume", options.AllowTlsResume),
            RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback,
            LocalCertificateSelectionCallback = options.LocalCertificateSelectionCallback,
            ClientCertificates = options.ClientCertificates,
            ClientCertificateContext = options.ClientCertificateContext,
            CertificateChainPolicy = options.CertificateChainPolicy,
            ApplicationProtocols = options.ApplicationProtocols,
        };
    }

    // GetValue with a fallback treats an empty nullable value as absent. Here an empty string
    // explicitly clears the setting, allowing named configuration to disable global timeouts.
    private static TimeSpan? OptionalTimeSpan(IConfiguration section, string key, TimeSpan? fallback)
    {
        var value = section[key];
        if (value is null) return fallback;
        if (value.Length == 0) return null;
        return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int? OptionalInt32(IConfiguration section, string key, int? fallback)
    {
        var value = section[key];
        if (value is null) return fallback;
        if (value.Length == 0) return null;
        return int.Parse(value, CultureInfo.InvariantCulture);
    }
}
