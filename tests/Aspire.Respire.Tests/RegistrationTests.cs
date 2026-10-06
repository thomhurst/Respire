using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Respire;
using Azure.Core;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire.Azure;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Aspire;

public class RegistrationTests
{
    /// <summary>Verifies empty named JSON arrays replace globals while absent arrays inherit them.</summary>
    [Test]
    [Arguments("Endpoints", "empty")]
    [Arguments("Endpoints", "absent")]
    [Arguments("Endpoints", "replacement")]
    [Arguments("ReplicaEndpoints", "empty")]
    [Arguments("ReplicaEndpoints", "absent")]
    [Arguments("ReplicaEndpoints", "replacement")]
    [Arguments("ClientSideCache:KeyPrefixes", "empty")]
    [Arguments("ClientSideCache:KeyPrefixes", "absent")]
    [Arguments("ClientSideCache:KeyPrefixes", "replacement")]
    public async Task NamedJsonArraysReplaceOrInheritGlobalArrays(string path, string mode)
    {
        var json = JsonNode.Parse("""
            {
              "Aspire": {
                "Respire": {
                  "Options": {
                    "Endpoints": ["global:6379"],
                    "ReplicaEndpoints": ["global-replica:6379"],
                    "ClientSideCache": { "TrackingMode": "Broadcast", "KeyPrefixes": ["global:"] }
                  },
                  "cache": { "Options": {} }
                }
              }
            }
            """)!;
        var named = json["Aspire"]!["Respire"]!["cache"]!["Options"]!;
        var segments = path.Split(':');
        if (mode != "absent")
        {
            if (segments.Length == 2)
            {
                var nested = new JsonObject();
                named[segments[0]] = nested;
                named = nested;
            }
            named[segments[^1]] = mode == "empty" ? new JsonArray() : new JsonArray("named:6380");
        }
        var builder = Builder(new());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        builder.Configuration.AddJsonStream(stream);
        var section = builder.Configuration.GetSection("Aspire:Respire:cache:Options:" + path);
        await Assert.That(section.Exists()).IsEqualTo(mode != "absent");
        if (mode == "empty") await Assert.That(section.Value).IsEqualTo(string.Empty);

        RespireOptions? observed = null;
        builder.AddRespireClient("cache", configureOptions: (_, options) => observed = options);
        using var host = builder.Build();
        if (path == "Endpoints" && mode == "empty")
            await Assert.That(() => host.Services.GetRequiredService<IRespireClient>()).Throws<InvalidOperationException>();
        else _ = host.Services.GetRequiredService<IRespireClient>();
        var values = path switch
        {
            "Endpoints" => observed!.Endpoints.Select(endpoint => endpoint.ToString()).ToArray(),
            "ReplicaEndpoints" => observed!.ReplicaEndpoints.Select(endpoint => endpoint.ToString()).ToArray(),
            _ => observed!.ClientSideCache!.KeyPrefixes.Select(prefix => prefix.ToString()).ToArray(),
        };
        await Assert.That(values.Length).IsEqualTo(mode == "empty" ? 0 : 1);
        if (mode == "replacement") await Assert.That(values.Single()).IsEqualTo("named:6380");
        else if (mode == "absent")
            await Assert.That(values.Single()).IsEqualTo(path == "Endpoints" ? "global:6379"
                : path == "ReplicaEndpoints" ? "global-replica:6379" : "global:");
    }

    /// <summary>Ensures registration metadata cannot drift when caller-owned settings or returned snapshots change.</summary>
    [Test]
    public async Task RegistrationSettingsRemainDetachedFromCallerMutation()
    {
        var builder = Builder();
        RespireClientSettings? callerSettings = null;
        var registration = builder.AddRespireClientBuilder("cache", settings =>
        {
            callerSettings = settings;
            settings.DisableHealthChecks = settings.DisableTracing = settings.DisableMetrics = settings.DisableLogging = true;
        });
        callerSettings!.ConnectionString = "changed:6380";
        callerSettings.DisableHealthChecks = callerSettings.DisableTracing = callerSettings.DisableMetrics = callerSettings.DisableLogging = false;
        var snapshot = registration.Settings;
        await Assert.That(snapshot.ConnectionString).IsEqualTo("localhost:6379");
        await Assert.That(snapshot.DisableHealthChecks && snapshot.DisableTracing && snapshot.DisableMetrics && snapshot.DisableLogging).IsTrue();
        snapshot.DisableHealthChecks = snapshot.DisableTracing = snapshot.DisableMetrics = snapshot.DisableLogging = false;
        await Assert.That(registration.Settings.DisableHealthChecks).IsTrue();
        await Assert.That(registration.Settings).IsNotSameReferenceAs(snapshot);
        using var host = builder.Build();
        await Assert.That(registration.GetClient(host.Services).Endpoint).IsEqualTo(new RespireEndpoint("localhost", 6379));
        await Assert.That(host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations).IsEmpty();
    }

    /// <summary>Checks mixed client flags add tracing and metrics once without preventing later registrations.</summary>
    [Test]
    [NotInParallel]
    public async Task MultipleClientsShareTelemetryProvidersAndEmitOnce()
    {
        var builder = Builder(new()
        {
            ["ConnectionStrings:cache"] = "localhost:6379", ["ConnectionStrings:trace"] = "localhost:6379",
            ["ConnectionStrings:meter"] = "localhost:6379", ["ConnectionStrings:both"] = "localhost:6379",
        });
        builder.AddRespireClient("cache", settings => settings.DisableTracing = settings.DisableMetrics = true);
        builder.AddKeyedRespireClient("trace", settings => settings.DisableMetrics = true);
        builder.AddKeyedRespireClient("meter", settings => settings.DisableTracing = true);
        builder.AddKeyedRespireClient("both");
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
            .WithMetrics(meter => meter.AddInMemoryExporter(metrics));
        using var host = builder.Build();
        await host.StartAsync();
        using var source = new ActivitySource("Respire");
        using (source.StartActivity("aspire-multiple-clients")) { }
        using var meter = new Meter("Respire");
        meter.CreateCounter<int>("aspire.multiple.clients").Add(1);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        await Assert.That(activities.Count(activity => activity.OperationName == "aspire-multiple-clients")).IsEqualTo(1);
        await Assert.That(metrics.Count(metric => metric.Name == "aspire.multiple.clients")).IsEqualTo(1);
        await host.StopAsync();
    }

    /// <summary>Protects the documented last-registration rule for the unkeyed distributed-cache service.</summary>
    [Test]
    public async Task LastDistributedCacheRegistrationSelectsItsClient()
    {
        await using var firstServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var lastServer = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("EVALSHA", StringComparison.Ordinal) ? ":1\r\n"u8.ToArray() : null,
        };
        var builder = Builder(new()
        {
            ["ConnectionStrings:first"] = $"127.0.0.1:{firstServer.Port}",
            ["ConnectionStrings:last"] = $"127.0.0.1:{lastServer.Port}",
            ["Aspire:Respire:Options:Protocol"] = "Resp2",
        });
        builder.AddRespireClientBuilder("first").AddDistributedCache(options => options.InstanceName = "first:");
        builder.AddKeyedRespireClientBuilder("last").AddDistributedCache(options => options.InstanceName = "last:");
        using var host = builder.Build();
        var caches = host.Services.GetServices<IDistributedCache>().ToArray();
        await Assert.That(caches.Length).IsEqualTo(2);
        var selected = host.Services.GetRequiredService<IDistributedCache>();
        await Assert.That(selected).IsSameReferenceAs(caches[^1]);
        await selected.SetAsync("entry", [1], new DistributedCacheEntryOptions());
        await Assert.That(firstServer.ReceivedCommands).IsEmpty();
        await Assert.That(lastServer.ReceivedCommands.Any(command => command.Contains("last:entry", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>Creates a host without environment defaults so configuration tests remain isolated.</summary>
    private static HostApplicationBuilder Builder(Dictionary<string, string?>? values = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values ?? new() { ["ConnectionStrings:cache"] = "localhost:6379" });
        return builder;
    }

    /// <summary>Verifies injection precedence and lazy client creation after immutable option overrides.</summary>
    [Test]
    public async Task ConfigurationPrecedencePreservesParsedOptionsAndAppliesCallbackLast()
    {
        var builder = Builder(new()
        {
            ["Aspire:Respire:ConnectionString"] = "global:6379,password=global",
            ["Aspire:Respire:cache:ConnectionString"] = "named:6380,password=named",
            ["ConnectionStrings:cache"] = "injected:6381,password=injected,defaultDatabase=3,ssl=true",
            ["Aspire:Respire:Options:Connections"] = "2",
            ["Aspire:Respire:Options:CommandTimeout"] = "00:00:04",
            ["Aspire:Respire:cache:Options:Connections"] = "3",
            ["Aspire:Respire:cache:Options:ClientName"] = "test",
        });
        RespireOptions? observed = null;
        builder.AddRespireClient("cache", configureOptions: (_, options) =>
        {
            observed = options;
            return options with { Connections = 1 };
        });
        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IRespireClient>();
        await Assert.That(observed!.Endpoints.Single()).IsEqualTo(new RespireEndpoint("injected", 6381));
        await Assert.That(observed.Password).IsEqualTo("injected");
        await Assert.That(observed.Database).IsEqualTo(3);
        await Assert.That(observed.UseTls).IsTrue();
        await Assert.That(observed.Connections).IsEqualTo(3);
        await Assert.That(observed.CommandTimeout).IsEqualTo(TimeSpan.FromSeconds(4));
        await Assert.That(observed.ClientName).IsEqualTo("test");
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(client).IsSameReferenceAs(host.Services.GetRequiredService<RespireClient>());
    }

    /// <summary>Ensures the settings callback can replace an Aspire-injected connection string.</summary>
    [Test]
    public async Task SettingsCallbackOverridesInjectedConnectionString()
    {
        var builder = Builder();
        RespireOptions? observed = null;
        builder.AddRespireClient("cache", settings => settings.ConnectionString = "callback:6380", (_, options) => observed = options);
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<IRespireClient>();
        await Assert.That(observed!.Endpoints.Single()).IsEqualTo(new RespireEndpoint("callback", 6380));
    }

    /// <summary>Exercises nested policies, endpoint representations, nullable values, and TLS configuration.</summary>
    [Test]
    public async Task OptionsBindEndpointsNestedPoliciesAndNullableTimeout()
    {
        var builder = Builder(new()
        {
            ["Aspire:Respire:Options:Endpoints:0:Host"] = "configured",
            ["Aspire:Respire:Options:Endpoints:0:Port"] = "6382",
            ["Aspire:Respire:Options:ReplicaEndpoints:0"] = "replica:6383",
            ["Aspire:Respire:Options:ReadFrom"] = "ReplicaPreferred",
            ["Aspire:Respire:Options:HedgedReads:Delay"] = "00:00:00.020",
            ["Aspire:Respire:Options:ReconnectPolicy:MaxAttempts"] = "4",
            ["Aspire:Respire:Options:ReconnectPolicy:JitterRatio"] = "0.3",
            ["Aspire:Respire:Options:ClientSideCache:MaxEntries"] = "100",
            ["Aspire:Respire:Options:ClientSideCache:KeyPrefixes:0"] = "cache:",
            ["Aspire:Respire:Options:CommandTimeout"] = "",
            ["Aspire:Respire:Options:TlsOptions:TargetHost"] = "redis.example",
            ["Aspire:Respire:Options:TlsOptions:EnabledSslProtocols"] = "Tls12, Tls13",
        });
        RespireOptions? observed = null;
        builder.AddRespireClient("cache", configureOptions: (_, options) => observed = options);
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<IRespireClient>();
        await Assert.That(observed!.Endpoints.Single()).IsEqualTo(new RespireEndpoint("configured", 6382));
        await Assert.That(observed.ReplicaEndpoints.Single()).IsEqualTo(new RespireEndpoint("replica", 6383));
        await Assert.That(observed.ReadFrom).IsEqualTo(RespireReadFrom.ReplicaPreferred);
        await Assert.That(observed.HedgedReads!.Delay).IsEqualTo(TimeSpan.FromMilliseconds(20));
        await Assert.That(observed.ReconnectPolicy!.MaxAttempts).IsEqualTo(4);
        await Assert.That(observed.ReconnectPolicy.JitterRatio).IsEqualTo(0.3);
        await Assert.That(observed.ClientSideCache!.MaxEntries).IsEqualTo(100);
        await Assert.That(observed.ClientSideCache.KeyPrefixes.Single()).IsEqualTo((RespireKey)"cache:");
        await Assert.That(observed.CommandTimeout).IsNull();
        await Assert.That(observed.TlsOptions!.TargetHost).IsEqualTo("redis.example");
    }

    /// <summary>Protects singleton identity and duplicate-registration rejection for both registration kinds.</summary>
    [Test]
    public async Task DefaultAndKeyedClientsAreIndependentAndDuplicatesFail()
    {
        var builder = Builder(new() { ["ConnectionStrings:cache"] = "default:6379", ["ConnectionStrings:other"] = "keyed:6380" });
        builder.AddRespireClient("cache");
        builder.AddKeyedRespireClient("other");
        await Assert.That(() => builder.AddRespireClient("other")).Throws<InvalidOperationException>();
        await Assert.That(() => builder.AddKeyedRespireClient("other")).Throws<InvalidOperationException>();
        using var host = builder.Build();
        var client = host.Services.GetRequiredService<IRespireClient>();
        var keyed = host.Services.GetRequiredKeyedService<IRespireClient>("other");
        await Assert.That(ReferenceEquals(client, keyed)).IsFalse();
        await Assert.That(keyed).IsSameReferenceAs(host.Services.GetRequiredKeyedService<RespireClient>("other"));
        await Assert.That(client.IsConnected || keyed.IsConnected).IsFalse();
    }

    /// <summary>Distinguishes eager configuration errors from deferred DI callback validation.</summary>
    [Test]
    public async Task MissingEndpointsFailEagerlyUnlessOptionsCallbackCanSupplyThem()
    {
        var builder = Builder(new());
        await Assert.That(() => builder.AddRespireClient("missing")).Throws<InvalidOperationException>();
        builder.AddRespireClient("missing", configureOptions: (_, options) => options);
        using var host = builder.Build();
        await Assert.That(() => host.Services.GetRequiredService<IRespireClient>()).Throws<InvalidOperationException>();
        var supplied = Builder(new());
        supplied.AddRespireClient("missing", configureOptions: (_, options) => options with { Endpoints = [new("localhost", 6379)] });
        using var suppliedHost = supplied.Build();
        await Assert.That(suppliedHost.Services.GetRequiredService<IRespireClient>().IsConnected).IsFalse();
        var nullBuilder = Builder();
        nullBuilder.AddRespireClient("cache", configureOptions: (_, _) => null!);
        using var nullHost = nullBuilder.Build();
        await Assert.That(() => nullHost.Services.GetRequiredService<IRespireClient>()).Throws<InvalidOperationException>();
    }

    /// <summary>Verifies diagnostic paths for global and named format or overflow failures.</summary>
    [Test]
    [Arguments("Aspire:Respire:Options:Endpoints:0", "localhost:bad")]
    [Arguments("Aspire:Respire:cache:Options:Endpoints:0:Port", "999999999999999999999999")]
    [Arguments("Aspire:Respire:Options:CommandTimeout", "invalid")]
    [Arguments("Aspire:Respire:cache:Options:ReconnectPolicy:MaxAttempts", "invalid")]
    public async Task InvalidConfigurationReportsFullPath(string path, string value)
    {
        var builder = Builder(new() { [path] = value, ["Aspire:Respire:cache:Options:Endpoints:0:Host"] = "localhost" });
        var exception = await Assert.That(() => builder.AddRespireClient("cache")).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains(path);
    }

    /// <summary>Checks both clients independently when their connection-string name is identical.</summary>
    [Test]
    public async Task DefaultAndKeyedHealthChecksSharingConnectionNameRemainIndependent()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        var builder = Builder(new()
        {
            ["ConnectionStrings:cache"] = $"127.0.0.1:{server.Port}",
            ["Aspire:Respire:Options:Protocol"] = "Resp2",
        });
        builder.AddRespireClient("cache");
        builder.AddKeyedRespireClient("cache");
        using var host = builder.Build();
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        var client = host.Services.GetRequiredService<IRespireClient>();
        var keyed = host.Services.GetRequiredKeyedService<IRespireClient>("cache");
        await Assert.That(ReferenceEquals(client, keyed)).IsFalse();
        await client.PingAsync();
        var partial = await checks.CheckHealthAsync();
        await Assert.That(partial.Entries.Count).IsEqualTo(2);
        await Assert.That(partial.Entries["respire_default_cache"].Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(partial.Entries["respire_keyed_cache"].Status).IsEqualTo(HealthStatus.Unhealthy);
        await keyed.PingAsync();
        await Assert.That((await checks.CheckHealthAsync()).Status).IsEqualTo(HealthStatus.Healthy);
    }

    /// <summary>Ensures health checks never create connections and reuse the selected keyed client.</summary>
    [Test]
    public async Task HealthCheckReusesSelectedKeyedConnection()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var builder = Builder(new()
        {
            ["ConnectionStrings:cache"] = "unused:6379",
            ["ConnectionStrings:other"] = $"127.0.0.1:{server.Port}",
            ["Aspire:Respire:Options:Protocol"] = "Resp2",
            ["Aspire:Respire:cache:DisableHealthChecks"] = "true",
        });
        builder.AddRespireClient("cache");
        builder.AddKeyedRespireClient("other");
        using var host = builder.Build();
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        var before = await checks.CheckHealthAsync();
        await Assert.That(before.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        await host.Services.GetRequiredKeyedService<IRespireClient>("other").PingAsync();
        var after = await checks.CheckHealthAsync();
        await Assert.That(after.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(after.Entries.Keys.Single()).IsEqualTo("respire_keyed_other");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>Exercises all tracing, metrics, and health-check flag combinations with real providers.</summary>
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    [NotInParallel]
    public async Task TelemetryDisableSettingsControlAutomaticProviderWiring(bool disableTracing, bool disableMetrics, bool disableHealthChecks)
    {
        var builder = Builder();
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        builder.AddRespireClient("cache", settings =>
        {
            settings.DisableTracing = disableTracing;
            settings.DisableMetrics = disableMetrics;
            settings.DisableHealthChecks = disableHealthChecks;
        });
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddInMemoryExporter(activities))
            .WithMetrics(meter => meter.AddInMemoryExporter(metrics));
        using var host = builder.Build();
        await host.StartAsync();
        using var source = new ActivitySource("Respire");
        using (source.StartActivity("aspire-registration-test")) { }
        using var meter = new Meter("Respire");
        meter.CreateCounter<int>("aspire.registration.test").Add(1);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();
        await Assert.That(activities.Any(activity => activity.OperationName == "aspire-registration-test")).IsEqualTo(!disableTracing);
        await Assert.That(metrics.Any(metric => metric.Name == "aspire.registration.test")).IsEqualTo(!disableMetrics);
        var checks = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        await Assert.That(checks.Registrations.Any()).IsEqualTo(!disableHealthChecks);
        await host.StopAsync();
    }

    /// <summary>Checks logging suppression with host-provided and explicitly configured factories.</summary>
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisableLoggingOverridesHostAndExplicitLoggerFactories(bool disabled, bool explicitFactory)
    {
        var builder = Builder();
        using var loggerFactory = new CountingLoggerFactory();
        builder.Services.AddSingleton<ILoggerFactory>(loggerFactory);
        builder.AddRespireClient("cache", settings => settings.DisableLogging = disabled,
            explicitFactory ? (_, options) => options with { LoggerFactory = loggerFactory } : null);
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<IRespireClient>();
        await Assert.That(loggerFactory.RespireCategories > 0).IsEqualTo(!disabled);
    }

    /// <summary>Verifies cache writes use the keyed client and cache disposal leaves that client usable.</summary>
    [Test]
    public async Task KeyedCacheHelpersReuseClientAndCacheDisposalDoesNotOwnIt()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var integerReply = ":1\r\n"u8.ToArray();
        server.ReplyOverride = (_, command) => command.Split(' ')[0] switch
        {
            "EVALSHA" => integerReply,
            "SET" => FakeRespServer.OkReply,
            _ => null,
        };
        var builder = Builder(new()
        {
            ["ConnectionStrings:cache"] = $"127.0.0.1:{server.Port}",
            ["Aspire:Respire:Options:Protocol"] = "Resp2",
        });
        var registration = builder.AddKeyedRespireClientBuilder("cache")
            .AddHybridCache(options => options.InstanceName = "distributed:")
            .AddOutputCache(options => options.InstanceName = "output:");
        using var host = builder.Build();
        var client = registration.GetClient(host.Services);
        await Assert.That(host.Services.GetService<IRespireClient>()).IsNull();
        await Assert.That(host.Services.GetRequiredService<HybridCache>()).IsNotNull();
        var output = host.Services.GetRequiredService<IOutputCacheStore>();
        var distributed = host.Services.GetRequiredService<IDistributedCache>();
        await distributed.SetAsync("entry", [1], new DistributedCacheEntryOptions());
        await output.SetAsync("entry", [1], [], TimeSpan.FromMinutes(1), CancellationToken.None);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVALSHA", StringComparison.Ordinal)
            && command.Contains("distributed:entry", StringComparison.Ordinal))).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET output:__MSOCV_entry", StringComparison.Ordinal))).IsTrue();
        ((IDisposable)distributed).Dispose();
        await client.PingAsync();
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>Checks DI credential resolution and the Entra Redis token scope without Azure provisioning.</summary>
    [Test]
    public async Task AzureManagedRedisUsesCallerOwnedCredentialFromDependencyInjection()
    {
        var builder = Builder(new() { ["ConnectionStrings:cache"] = "managed.redis.azure.net:10000,ssl=true" });
        var credential = new TestCredential();
        builder.Services.AddSingleton<TokenCredential>(credential);
        IRespireCredentialProvider? provider = null;
        builder.AddRespireClient("cache", configureOptions: (services, options) => options with
        {
            CredentialProvider = provider = new AzureManagedRedisCredentialProvider(services.GetRequiredService<TokenCredential>(), "object-id"),
        });
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<IRespireClient>();
        var credentials = await provider!.GetCredentialsAsync();
        await Assert.That(credentials.Username).IsEqualTo("object-id");
        await Assert.That(credentials.Password).IsEqualTo("test-token");
        await Assert.That(credential.Scopes!.Single()).IsEqualTo("https://redis.azure.com/.default");
    }

    private sealed class TestCredential : TokenCredential
    {
        internal string[]? Scopes;
        /// <summary>Captures requested scopes while returning a deterministic, unexpired test token.</summary>
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        /// <summary>Shares the deterministic token behavior for asynchronous credential consumers.</summary>
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class CountingLoggerFactory : ILoggerFactory
    {
        internal int RespireCategories;
        /// <summary>Counts Respire categories while avoiding actual log output.</summary>
        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName.StartsWith("Respire", StringComparison.Ordinal)) RespireCategories++;
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
        /// <summary>Leaves provider ownership with the test; this factory only counts category requests.</summary>
        public void AddProvider(ILoggerProvider provider) { }
        /// <summary>Releases no resources because the factory owns none.</summary>
        public void Dispose() { }
    }
}
