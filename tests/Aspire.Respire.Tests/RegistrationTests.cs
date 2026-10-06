using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private static HostApplicationBuilder Builder(Dictionary<string, string?>? values = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values ?? new() { ["ConnectionStrings:cache"] = "localhost:6379" });
        return builder;
    }

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

    [Test]
    public async Task MissingEndpointsAndNullCallbackFailWhenResolving()
    {
        var builder = Builder(new());
        builder.AddRespireClient("missing");
        using var host = builder.Build();
        await Assert.That(() => host.Services.GetRequiredService<IRespireClient>()).Throws<InvalidOperationException>();
        var nullBuilder = Builder();
        nullBuilder.AddRespireClient("cache", configureOptions: (_, _) => null!);
        using var nullHost = nullBuilder.Build();
        await Assert.That(() => nullHost.Services.GetRequiredService<IRespireClient>()).Throws<InvalidOperationException>();
    }

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
        await Assert.That(after.Entries.Keys.Single()).IsEqualTo("respire_other");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

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
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class CountingLoggerFactory : ILoggerFactory
    {
        internal int RespireCategories;
        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName.StartsWith("Respire", StringComparison.Ordinal)) RespireCategories++;
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
