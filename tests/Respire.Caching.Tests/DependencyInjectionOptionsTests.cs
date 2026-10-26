using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Respire.DependencyInjection;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class DependencyInjectionOptionsTests
{
    [Test]
    public async Task Configuration_InvalidLatencyToleranceIncludesPath()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RespireConfiguration.Apply(
            Section(new() { ["ReplicaLatencyTolerance"] = "invalid" }), new RespireOptions()));
        await Assert.That(error!.Message).Contains("Redis:ReplicaLatencyTolerance");
    }

    [Test]
    [Arguments(null, 5)]
    [Arguments("", null)]
    [Arguments("00:00:00", 0)]
    [Arguments("00:00:00.002", 2)]
    public async Task Configuration_LatencyTolerance_CanClearInheritedValue(string? value, int? expectedMilliseconds)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null) values["ReplicaLatencyTolerance"] = value;
        var options = RespireConfiguration.Apply(Section(values),
            new RespireOptions { ReplicaLatencyTolerance = TimeSpan.FromMilliseconds(5) });
        await Assert.That(options.ReplicaLatencyTolerance).IsEqualTo(
            expectedMilliseconds is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : (TimeSpan?)null);
    }

    [Test]
    public async Task Builder_HasEveryPublicOption_WithCoreDefaults_AndForwardsEverySetter()
    {
        var builder = new RespireOptionsBuilder();
        var defaults = new RespireOptions();
        foreach (var property in typeof(RespireOptions).GetProperties())
        {
            var target = typeof(RespireOptionsBuilder).GetProperty(property.Name);
            await Assert.That(target).IsNotNull();
            await Assert.That(target!.CanWrite).IsTrue();
            await Assert.That(target.PropertyType).IsEqualTo(property.PropertyType);
            if (property.PropertyType == typeof(IList<RespireEndpoint>))
            {
                await Assert.That((IList<RespireEndpoint>)target.GetValue(builder)!).IsEmpty();
                target.SetValue(builder, new List<RespireEndpoint> { new("example", 1234) });
                await Assert.That((IList<RespireEndpoint>)property.GetValue(builder.Build())!)
                    .IsEquivalentTo(new[] { new RespireEndpoint("example", 1234) });
            }
            else
            {
                await Assert.That(target.GetValue(builder)).IsEqualTo(property.GetValue(defaults));
                var value = Sample(property.PropertyType);
                target.SetValue(builder, value);
                await Assert.That(property.GetValue(builder.Build())).IsEqualTo(value);
            }
        }
    }

    // Distinct values exercise forwarding, including service interfaces and nullable properties.
    private static object Sample(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "configured";
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 17;
        if (type == typeof(TimeSpan)) return TimeSpan.FromSeconds(17);
        if (type == typeof(RespireKey)) return (RespireKey)"prefix:";
        if (type.IsEnum) return Enum.GetValues(type).GetValue(1)!;
        if (type == typeof(IRespireCredentialProvider)) return new Credentials();
        if (type == typeof(RespireConnectionHook)) return new ConnectionHook();
        if (type == typeof(IRespireSerializer)) return RespireSerializer.Default;
        if (type == typeof(ILoggerFactory)) return NullLoggerFactory.Instance;
        if (type == typeof(RespirePipelineHook)) return new PipelineHook();
        if (type == typeof(RespireCommandHook)) return new CommandHook();
        if (type == typeof(Func<RespireEndpoint, CancellationToken, ValueTask<Stream>>))
            return (Func<RespireEndpoint, CancellationToken, ValueTask<Stream>>)((_, _) => throw new InvalidOperationException("Builder must not connect."));
        if (type == typeof(IReadOnlyDictionary<RespireEndpoint, RespireEndpoint>))
            return new Dictionary<RespireEndpoint, RespireEndpoint> { ["private:6379"] = "localhost:7001" };
        if (type == typeof(Func<RespireEndpoint, RespireEndpoint>))
            return (Func<RespireEndpoint, RespireEndpoint>)(endpoint => endpoint);
        if (type == typeof(Func<RespireEndpoint, bool>))
            return (Func<RespireEndpoint, bool>)(endpoint => endpoint.Port == 6379);
        return Activator.CreateInstance(type)!;
    }

    [Test]
    public async Task Configuration_MapsEverySerializableOption_AndNestedPolicy()
    {
        // A new property requires either a configuration sample or an explicit runtime-service exception.
        var values = new Dictionary<string, string?>
        {
            ["Endpoints:0"] = "localhost:6380", ["ReplicaEndpoints:0"] = "replica:6381",
            ["NodeAddressMap"] = "private:6379=localhost:7001", ["UseMappedHostForTls"] = "true",
            ["KeyPrefix"] = "keys:", ["PubSubPrefix"] = "channels:", ["ReadFrom"] = "Primary",
            ["HedgedReads:Delay"] = "00:00:00.025", ["ClientAvailabilityZone"] = "zone-1",
            ["ReplicaRefreshInterval"] = "00:00:02", ["UseCluster"] = "false",
            ["ReplicaLatencyTolerance"] = "00:00:00.005",
            ["IncludeCrossSlotKeyContext"] = "true", ["EnableReplicaRedirects"] = "true",
            ["SplitCrossSlotCommands"] = "true",
            ["SentinelPrimaryName"] = "primary", ["Username"] = "user", ["Password"] = "password",
            ["CredentialRefreshBeforeExpiry"] = "00:02:00", ["CredentialRefreshRetryDelay"] = "00:00:03",
            ["SentinelUsername"] = "sentinel-user", ["SentinelPassword"] = "sentinel-password",
            ["SentinelUseTls"] = "true", ["SentinelTlsOptions:TargetHost"] = "sentinel",
            ["ClientName"] = "test", ["LibraryName"] = "configured-library", ["Database"] = "2", ["AllowAdmin"] = "true", ["Protocol"] = "Resp3",
            ["MaintenanceNotifications"] = "Auto", ["MaintenanceRelaxedTimeout"] = "00:00:45",
            ["MaintenanceWindowTimeout"] = "00:01:30", ["ConnectTimeout"] = "00:00:04",
            ["ReconnectPolicy:MaxAttempts"] = "4", ["ClusterTopologyRefreshInterval"] = "00:00:50",
            ["BacklogPolicy"] = "FailFast", ["BacklogCapacity"] = "123",
            ["UseTls"] = "true", ["TlsOptions:TargetHost"] = "redis", ["EnableMultipathTcp"] = "true",
            ["ConnectionIdleReadTimeout"] = "00:00:12", ["GatheredWriteStallTimeout"] = "00:00:13",
            ["ConnectionMaxLifetime"] = "00:05:00", ["ConnectionMaxLifetimeJitter"] = "00:00:30",
            ["CommandTimeout"] = "00:00:05", ["ThreadPoolMonitoring"] = "false",
            ["RunContinuationsAsynchronously"] = "true", ["StalledDeliveryThreshold"] = "00:00:00.025",
            ["ThreadPoolWarningThreshold"] = "00:00:00.200", ["Connections"] = "3",
            ["DedicatedPool:MinimumSize"] = "2", ["DedicatedPool:MaximumSize"] = "12",
            ["DedicatedPool:MaximumIdle"] = "5", ["DedicatedPool:IdleTimeout"] = "00:00:30",
            ["ClientSideCache:MaxEntries"] = "222", ["ClientSideCache:KeyPrefixes:0"] = "tracked:",
            ["RetryPolicy:MaxAttempts"] = "7", ["RetryPolicy:MaxCommandRetryCategory"] = "ReadOnly",
            ["RetryPolicy:RetryableErrors"] = "Loading", ["RetryPolicy:InitialBackoff"] = "00:00:00.020",
            ["RetryPolicy:MaxBackoff"] = "00:00:02", ["RetryPolicy:Jitter"] = "0.5",
            ["StreamProducerId"] = "producer-1", ["TcpKeepAliveTime"] = "00:00:20",
            ["TcpKeepAliveInterval"] = "00:00:04", ["TcpKeepAliveRetryCount"] = "5",
            ["SubscriptionBufferSize"] = "333", ["SubscriptionOverflow"] = "DropNewest",
            ["ReceiveBufferSize"] = "32768", ["WriteBufferSize"] = "16384", ["MaxInflightCommands"] = "444",
            ["CircuitBreaker:FailureRateThreshold"] = "0.7", ["CircuitBreaker:MinimumFailureCount"] = "8",
            ["CircuitBreaker:SamplingWindow"] = "00:00:40", ["CircuitBreaker:MaximumSampleCount"] = "100",
            ["CircuitBreaker:OpenDuration"] = "00:00:09", ["CircuitBreaker:HalfOpenProbeCount"] = "2",
        };
        var section = Section(values);
        // Mapping is tested separately from validation: this exercises all fields, including
        // settings that cannot be enabled together on one client (replicas and circuit breaker).
        var options = RespireConfiguration.Apply(section, new RespireOptions());
        string[] runtimeServices = ["CredentialProvider", "SentinelCredentialProvider", "ConnectionFactory", "ConnectionHook", "Serializer", "LoggerFactory", "PipelineHook", "CommandHook", "NodeAddressResolver", "ReplicaRedirectValidator"];
        foreach (var property in typeof(RespireOptions).GetProperties())
        {
            if (runtimeServices.Contains(property.Name)) continue;
            await Assert.That(values.Keys.Any(key => key.Split(':')[0] == property.Name)).IsTrue();
        }
        foreach (var (path, expected) in values)
        {
            object actual = options;
            foreach (var segment in path.Split(':'))
            {
                actual = int.TryParse(segment, out var index)
                    ? ((System.Collections.IEnumerable)actual).Cast<object>().ElementAt(index)
                    : actual.GetType().GetProperty(segment)!.GetValue(actual)!;
            }
            if (actual is IReadOnlyDictionary<RespireEndpoint, RespireEndpoint> map)
                await Assert.That(map[new("private", 6379)]).IsEqualTo(new RespireEndpoint("localhost", 7001));
            else if (actual is bool boolean) await Assert.That(boolean).IsEqualTo(bool.Parse(expected!));
            else if (actual is TimeSpan time) await Assert.That(time).IsEqualTo(TimeSpan.Parse(expected!, CultureInfo.InvariantCulture));
            else await Assert.That(Convert.ToString(actual, CultureInfo.InvariantCulture)).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(false, null, "Respire", "Respire")]
    [Arguments(false, null, "wrapper", "wrapper")]
    [Arguments(false, null, null, null)]
    [Arguments(true, "wrapper", "Respire", "wrapper")]
    [Arguments(true, "", "Respire", null)]
    [Arguments(true, null, "Respire", null)]
    public async Task Configuration_LibraryName_PreservesAbsentValuesAndSupportsOptOut(
        bool configured, string? value, string? initialValue, string? expectedValue)
    {
        var values = new Dictionary<string, string?>();
        if (configured) values["LibraryName"] = value;

        var options = RespireConfiguration.Apply(Section(values), new RespireOptions { LibraryName = initialValue });

        await Assert.That(options.LibraryName).IsEqualTo(expectedValue);
    }

    [Test]
    public async Task Configuration_DedicatedPool_PreservesUnspecifiedSettingsAndClearsOptionalBounds()
    {
        var original = new RespireOptions
        {
            DedicatedPool = new()
            {
                MinimumSize = 2, MaximumIdle = 5, MaximumSize = 12, IdleTimeout = TimeSpan.FromSeconds(30),
            },
        };
        var unchanged = RespireConfiguration.Apply(Section(new()), original);
        await Assert.That(unchanged.DedicatedPool).IsSameReferenceAs(original.DedicatedPool);
        var configured = RespireConfiguration.Apply(Section(new()
        {
            ["DedicatedPool:MaximumSize"] = "", ["DedicatedPool:IdleTimeout"] = "",
        }), original);
        await Assert.That(configured.DedicatedPool.MinimumSize).IsEqualTo(2);
        await Assert.That(configured.DedicatedPool.MaximumIdle).IsEqualTo(5);
        await Assert.That(configured.DedicatedPool.MaximumSize).IsNull();
        await Assert.That(configured.DedicatedPool.IdleTimeout).IsNull();
    }

    [Test]
    [Arguments(null, false, false)]
    [Arguments(null, true, true)]
    [Arguments("false", false, false)]
    [Arguments("false", true, false)]
    [Arguments("true", false, true)]
    [Arguments("true", true, true)]
    public async Task Configuration_CrossSlotKeyContext_RequiresExplicitOptIn(
        string? configuredValue, bool initialValue, bool expectedValue)
    {
        var values = new Dictionary<string, string?>();
        if (configuredValue is not null)
        {
            values["IncludeCrossSlotKeyContext"] = configuredValue;
        }

        var options = RespireConfiguration.Apply(Section(values),
            new RespireOptions { IncludeCrossSlotKeyContext = initialValue });

        await Assert.That(options.IncludeCrossSlotKeyContext).IsEqualTo(expectedValue);
    }

    [Test]
    [Arguments(null, false, false)]
    [Arguments(null, true, true)]
    [Arguments("false", false, false)]
    [Arguments("false", true, false)]
    [Arguments("true", false, true)]
    [Arguments("true", true, true)]
    public async Task Configuration_SplitCrossSlotCommands_RequiresExplicitOptIn(
        string? configuredValue, bool initialValue, bool expectedValue)
    {
        var values = new Dictionary<string, string?>();
        if (configuredValue is not null)
        {
            values["SplitCrossSlotCommands"] = configuredValue;
        }

        var options = RespireConfiguration.Apply(Section(values),
            new RespireOptions { SplitCrossSlotCommands = initialValue });

        await Assert.That(options.SplitCrossSlotCommands).IsEqualTo(expectedValue);
    }

    [Test]
    public async Task Host_ValidatesDefaultAndNamedOptions_WithoutConnecting_AndUsesCallbackServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IRespireCredentialProvider, Credentials>();
        builder.Services.AddSingleton<RespireConnectionHook, ConnectionHook>();
        builder.Services.AddRespire(Section(new() { ["Endpoints:0"] = "127.0.0.1:1", ["KeyPrefix"] = "default:" }),
            (provider, options) => options with
            {
                CredentialProvider = provider.GetRequiredService<IRespireCredentialProvider>(),
                ConnectionHook = provider.GetRequiredService<RespireConnectionHook>(),
            });
        builder.Services.AddKeyedRespire("jobs", Section(new() { ["Endpoints:0"] = "127.0.0.1:2", ["KeyPrefix"] = "jobs:" }));
        using var host = builder.Build();
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<RespireOptions>>().Value;
        var named = host.Services.GetRequiredService<IOptionsMonitor<RespireOptions>>().Get("jobs");
        await Assert.That(options.KeyPrefix.ToString()).IsEqualTo("default:");
        await Assert.That(named.KeyPrefix.ToString()).IsEqualTo("jobs:");
        await Assert.That(options.CredentialProvider).IsSameReferenceAs(host.Services.GetRequiredService<IRespireCredentialProvider>());
        await Assert.That(options.ConnectionHook).IsSameReferenceAs(host.Services.GetRequiredService<RespireConnectionHook>());
        await Assert.That(options.LoggerFactory).IsSameReferenceAs(host.Services.GetRequiredService<ILoggerFactory>());
        var client = host.Services.GetRequiredService<RespireClient>();
        var jobs = host.Services.GetRequiredKeyedService<RespireClient>("jobs");
        await Assert.That(client).IsNotSameReferenceAs(jobs);
        await Assert.That(jobs.Core.Options.Endpoints[0].Port).IsEqualTo(2);
        await Assert.That(client.Core.Options.KeyPrefix.ToString()).IsEqualTo("default:");
        await host.StopAsync();
    }

    [Test]
    [Arguments("Connections", "0", "Connections")]
    [Arguments("CommandTimeout", "invalid", "Redis:CommandTimeout")]
    [Arguments("RetryPolicy:MaxAttempts", "0", "MaxAttempts")]
    [Arguments("CircuitBreaker:FailureRateThreshold", "2", "FailureRateThreshold")]
    [Arguments("Endpoints:0", "host:99999", "Redis:Endpoints:0")]
    public async Task Host_RejectsInvalidNamedConfigurationAtStartup(string key, string value, string message)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddKeyedRespire("invalid", Section(new() { ["Endpoints:0"] = "127.0.0.1:1", [key] = value }));
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        await Assert.That(error!.OptionsName).IsEqualTo("invalid");
        await Assert.That(error.Message).Contains(message);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Host_PreservesCallbackExceptionAtStartup(bool keyed)
    {
        var builder = Host.CreateApplicationBuilder();
        var expected = new InvalidOperationException("Callback service failed.", new Exception("Original cause."));
        var section = Section(new() { ["Endpoints:0"] = "127.0.0.1:1" });
        RespireOptions Configure(IServiceProvider provider, RespireOptions options) => throw expected;
        if (keyed) builder.Services.AddKeyedRespire("jobs", section, Configure);
        else builder.Services.AddRespire(section, Configure);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(error!.StackTrace).Contains("Configure");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Configuration_PreservesLoggerResolutionException(bool keyed)
    {
        var services = new ServiceCollection();
        var expected = new InvalidOperationException("Logger service failed.");
        services.AddSingleton<ILoggerFactory>(_ => throw expected);
        var section = Section(new() { ["Endpoints:0"] = "127.0.0.1:1" });
        if (keyed) services.AddKeyedRespire("jobs", section);
        else services.AddRespire(section);
        await using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<RespireOptions>>().Get(keyed ? "jobs" : Options.DefaultName));

        await Assert.That(error).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task Host_RejectsMissingEndpointsForDefaultClient()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRespire(Section(new()));
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        await Assert.That(error!.Message).Contains("endpoint");
    }

    [Test]
    public async Task EmptyNullableSettings_ClearTimeouts_AndDuplicateKeysAreRejected()
    {
        var services = new ServiceCollection();
        var section = Section(new() { ["Endpoints:0"] = "localhost", ["CommandTimeout"] = "", ["ClusterTopologyRefreshInterval"] = "" });
        services.AddKeyedRespire("cache", section);
        await Assert.That(() => services.AddKeyedRespire("cache", section)).Throws<InvalidOperationException>();
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<RespireOptions>>().Get("cache");
        await Assert.That(options.CommandTimeout).IsNull();
        await Assert.That(options.ClusterTopologyRefreshInterval).IsNull();
    }

    [Test]
    public async Task InvalidAddressMappingIncludesConfigurationPath()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RespireConfiguration.Apply(
            Section(new() { ["NodeAddressMap"] = "invalid" }), new RespireOptions()));
        await Assert.That(error!.Message).Contains("Redis:NodeAddressMap");
        await Assert.That(error.InnerException).IsTypeOf<RespireConfigurationException>();
    }

    private static IConfigurationSection Section(Dictionary<string, string?> values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(pair => "Redis:" + pair.Key, pair => pair.Value))
        .Build().GetSection("Redis");

    [Test]
    public async Task JsonConfiguration_BindsEndpointObjectsAndPolicies()
    {
        const string json = """
            { "Redis": {
              "Endpoints": [{ "Host": "localhost", "Port": 6389 }],
              "RetryPolicy": { "MaxAttempts": 8 },
              "CircuitBreaker": { "OpenDuration": "00:00:20" },
              "StreamProducerId": "worker-1",
              "LibraryName": null,
              "NodeAddressMap": "redis.internal:6379=localhost:7001",
              "UseMappedHostForTls": true
            } }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var services = new ServiceCollection();
        services.AddRespire(configuration.GetSection("Redis"));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RespireOptions>>().Value;
        await Assert.That(options.Endpoints[0]).IsEqualTo(new RespireEndpoint("localhost", 6389));
        await Assert.That(options.RetryPolicy!.MaxAttempts).IsEqualTo(8);
        await Assert.That(options.CircuitBreaker!.OpenDuration).IsEqualTo(TimeSpan.FromSeconds(20));
        await Assert.That(options.StreamProducerId).IsEqualTo("worker-1");
        await Assert.That(options.LibraryName).IsNull();
        await Assert.That(options.NodeAddressMap![new("REDIS.INTERNAL", 6379)]).IsEqualTo(new RespireEndpoint("localhost", 7001));
        await Assert.That(options.UseMappedHostForTls).IsTrue();
    }

    private sealed class CommandHook : RespireCommandHook
    {
        public override object? OnExecuting(RespireCommandContext context) => null;

        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) { }
    }

    private sealed class PipelineHook : RespirePipelineHook
    {
        public override object? OnExecuting(RespirePipelineContext context) => null;

        public override void OnCompleted(RespirePipelineContext context, object? state,
            RespirePipelineOutcome outcome, Exception? error) { }
    }

    private sealed class ConnectionHook : RespireConnectionHook
    {
        public override ValueTask OnConnectingAsync(RespireConnectionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Startup must not invoke connection hooks.");
    }

    private sealed class Credentials : IRespireCredentialProvider
    {
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Startup must not acquire credentials or connect.");
    }
}
