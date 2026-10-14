using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ConnectionStringCompatibilityTests
{
    [Test]
    [Arguments("example.redis.cache.windows.net:6380,password=secret==,ssl=True,abortConnect=False")]
    [Arguments("example.cache.amazonaws.com:6379,ssl=true,abortConnect=false,connectRetry=3,connectTimeout=5000")]
    [Arguments("localhost,allowAdmin=true,asyncTimeout=5000,syncTimeout=5000,defaultDatabase=1,name=app,user=default,password=secret,protocol=resp3")]
    public async Task PastedConnectionStringsCreateLazyClients(string connectionString)
    {
        var options = RespireOptions.Parse(connectionString);
        await using var client = RespireClient.Create(options);
        await Assert.That(options.Endpoints.Count).IsEqualTo(1);
        await Assert.That(options.IgnoredConnectionStringOptions.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MappedOptionsPreserveUnitsLiteralPrefixAndLastValue()
    {
        var options = RespireOptions.Parse("localhost,channelPrefix=first,CHANNELPREFIX=literal%3A," +
            "keepAlive=30,connectRetry=4,responseTimeout=1200,configCheckSeconds=15,maintNotifications=Auto," +
            "maintRelaxedTimeout=10,maintRelaxedWindowMax=20");
        await Assert.That(options.PubSubPrefix.Text).IsEqualTo("literal%3A");
        await Assert.That(options.TcpKeepAliveTime).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(options.ReconnectPolicy!.MaxAttempts).IsEqualTo(4);
        await Assert.That(options.ConnectionIdleReadTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1200));
        await Assert.That(options.ClusterTopologyRefreshInterval).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(options.MaintenanceNotifications).IsEqualTo(RespireMaintenanceNotificationMode.Auto);
        await Assert.That(options.MaintenanceRelaxedTimeout).IsEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(options.MaintenanceWindowTimeout).IsEqualTo(TimeSpan.FromSeconds(20));
    }

    [Test]
    [Arguments("Disabled", RespireMaintenanceNotificationMode.Disabled)]
    [Arguments("AUTO", RespireMaintenanceNotificationMode.Auto)]
    [Arguments("enabled", RespireMaintenanceNotificationMode.Enabled)]
    public async Task MaintenanceModeIsMapped(string value, RespireMaintenanceNotificationMode expected)
        => await Assert.That(RespireOptions.Parse($"localhost,maintNotifications={value}").MaintenanceNotifications).IsEqualTo(expected);

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    public async Task DisabledKeepAliveUsesOperatingSystemDefault(int value)
        => await Assert.That(RespireOptions.Parse($"localhost,keepAlive={value}").TcpKeepAliveTime).IsNull();

    // Together with RespireOptionsTests, this inventory covers ConfigurationOptions.OptionKeys
    // in StackExchange.Redis, including obsolete and newer 3.x options.
    [Test]
    [Arguments("configChannel")]
    [Arguments("tiebreaker")]
    [Arguments("resolveDns")]
    [Arguments("version")]
    [Arguments("setlib")]
    [Arguments("highPriorityThreads")]
    [Arguments("preserveAsyncOrder")]
    [Arguments("writeBuffer")]
    [Arguments("defaults")]
    [Arguments("highIntegrity")]
    [Arguments("sharedSubscriptionConnection")]
    [Arguments("tcpKeepAlive")]
    [Arguments("maintMovingEndpointType")]
    [Arguments("maintPostEventRelaxed")]
    [Arguments("futureOption")]
    public async Task IgnoredKeysLogOncePerClientWithoutValues(string key)
    {
        using var logger = new CaptureLoggerFactory();
        var options = RespireOptions.Parse($"localhost,{key}=private-value,{key.ToUpperInvariant()}=private-value")
            with { LoggerFactory = logger };
        options.ValidateAndSnapshot();
        await using var client = RespireClient.Create(options);
        _ = client.WithKeyPrefix("tenant:");
        await Assert.That(logger.Messages.Count).IsEqualTo(1);
        await Assert.That(logger.Messages[0]).Contains(key);
        await Assert.That(logger.Messages[0].Contains("private-value", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task AbortConnectTrueWarnsThatTheApiControlsConnectionLifecycle()
    {
        using var logger = new CaptureLoggerFactory();
        await using var client = RespireClient.Create(RespireOptions.Parse("localhost,abortConnect=true") with { LoggerFactory = logger });
        await Assert.That(logger.Messages.Single()).Contains("abortConnect");
    }

    [Test]
    [Arguments("abortConnect=invalid", "abortConnect")]
    [Arguments("connectRetry=0", "connectRetry")]
    [Arguments("connectRetry=-1", "connectRetry")]
    [Arguments("keepAlive=-2", "keepAlive")]
    [Arguments("responseTimeout=0", "responseTimeout")]
    [Arguments("defaultDatabase=-1", "defaultDatabase")]
    [Arguments("connectTimeout=0", "connectTimeout")]
    [Arguments("asyncTimeout=-1", "asyncTimeout")]
    [Arguments("syncTimeout=0", "syncTimeout")]
    [Arguments("configCheckSeconds=-1", "configCheckSeconds")]
    [Arguments("maintNotifications=invalid", "maintNotifications")]
    [Arguments("maintNotifications=99", "maintNotifications")]
    [Arguments("protocol=2,maintNotifications=Enabled", "maintNotifications")]
    [Arguments("maintRelaxedTimeout=0", "maintRelaxedTimeout")]
    [Arguments("maintRelaxedWindowMax=86401", "maintRelaxedWindowMax")]
    [Arguments("proxy=Twemproxy", "proxy")]
    [Arguments("tunnel=http:proxy:80", "tunnel")]
    [Arguments("$GET=RENAMED", "$GET")]
    [Arguments("=missing-key", "key")]
    public async Task RejectedOptionsNameKeyAndReason(string option, string key)
    {
        var error = Assert.Throws<RespireConfigurationException>(() => RespireOptions.Parse($"localhost,{option}"));
        await Assert.That(error.Message).Contains(key);
    }

    [Test]
    public async Task AbstractSocketIsNeverTreatedAsARelativeFilesystemPath()
    {
        var error = Assert.Throws<RespireConfigurationException>(() => RespireOptions.Parse("!@redis,password=secret"));
        await Assert.That(error.Message).Contains("abstract Unix socket");
        await Assert.That(error.Message).Contains("!/path");
    }

    [Test]
    public async Task NoOpTransportOptionsAndZeroRefreshAreAccepted()
    {
        var options = RespireOptions.Parse("localhost,proxy=None,tunnel=,topologyRefreshSeconds=0");
        await Assert.That(options.ClusterTopologyRefreshInterval).IsEqualTo(TimeSpan.Zero);
        await Assert.That(options.MaintenanceNotifications).IsEqualTo(RespireMaintenanceNotificationMode.Disabled);
    }

    [Test]
    public async Task OmittedCompatibilityOptionsKeepRespireDefaults()
    {
        var defaults = new RespireOptions();
        var parsed = RespireOptions.Parse("localhost,abortConnect=false");
        await Assert.That(parsed.ClusterTopologyRefreshInterval).IsEqualTo(defaults.ClusterTopologyRefreshInterval);
        await Assert.That(parsed.MaintenanceNotifications).IsEqualTo(defaults.MaintenanceNotifications);
        await Assert.That(parsed.MaintenanceRelaxedTimeout).IsEqualTo(defaults.MaintenanceRelaxedTimeout);
        await Assert.That(parsed.MaintenanceWindowTimeout).IsEqualTo(defaults.MaintenanceWindowTimeout);
        await Assert.That(parsed.ReconnectPolicy).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("redis://[invalid")]
    [Arguments("localhost:invalid")]
    [Arguments("http://localhost")]
    public async Task AllParseFailuresUseConfigurationException(string? value)
        => await Assert.That(() => RespireOptions.Parse(value!)).ThrowsExactly<RespireConfigurationException>();

    private sealed class CaptureLoggerFactory : ILoggerFactory, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Messages.Add(formatter(state, exception));
        }
    }
}
