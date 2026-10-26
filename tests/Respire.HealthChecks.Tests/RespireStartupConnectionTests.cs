using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.DependencyInjection;
using Respire.HealthChecks;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>Covers background connection of DI-registered clients when the host starts (#2183).</summary>
public class RespireStartupConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static IServiceCollection CreateServices()
        => new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

    private static RespireStartupConnector Connector(IServiceProvider provider)
        => provider.GetServices<IHostedService>().OfType<RespireStartupConnector>().Single();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HostStartConnectsLazyClientWithoutApplicationTraffic(bool keyed)
    {
        await using var server = new RespireFakeServer();
        var services = CreateServices();
        if (keyed)
        {
            services.AddKeyedRespire("cache", _ => server.CreateOptions() with { Connections = 1 });
            services.AddHealthChecks().AddRespire(clientFactory: provider => provider.GetRequiredKeyedService<IRespireClient>("cache"));
        }
        else
        {
            services.AddRespire(_ => server.CreateOptions() with { Connections = 1 });
            services.AddHealthChecks().AddRespire();
        }

        await using var provider = services.BuildServiceProvider();
        var client = keyed ? provider.GetRequiredKeyedService<IRespireClient>("cache") : provider.GetRequiredService<IRespireClient>();
        await Assert.That(client.IsConnected).IsFalse();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);
        await connector.Connecting.WaitAsync(Timeout);

        await Assert.That(client.IsConnected).IsTrue();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
        await connector.StopAsync(CancellationToken.None);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectOnStartFalseKeepsClientLazy(bool keyed)
    {
        await using var server = new RespireFakeServer();
        var services = CreateServices();
        if (keyed) services.AddKeyedRespire("cache", _ => server.CreateOptions());
        else services.AddRespire(_ => server.CreateOptions());
        services.Configure<RespireStartupOptions>(keyed ? "cache" : Microsoft.Extensions.Options.Options.DefaultName,
            options => options.ConnectOnStart = false);
        await using var provider = services.BuildServiceProvider();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);

        await Assert.That(connector.Connecting.IsCompleted).IsTrue();
        var client = keyed ? provider.GetRequiredKeyedService<IRespireClient>("cache") : provider.GetRequiredService<IRespireClient>();
        await Assert.That(client.IsConnected).IsFalse();
        await connector.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task DefaultAndKeyedClientsAreConfiguredIndependently()
    {
        await using var server = new RespireFakeServer();
        var services = CreateServices();
        services.AddRespire(_ => server.CreateOptions() with { Connections = 1 });
        services.AddKeyedRespire("cache", _ => server.CreateOptions() with { Connections = 1 });
        services.Configure<RespireStartupOptions>(options => options.ConnectOnStart = false);
        await using var provider = services.BuildServiceProvider();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);
        await connector.Connecting.WaitAsync(Timeout);

        await Assert.That(provider.GetRequiredKeyedService<IRespireClient>("cache").IsConnected).IsTrue();
        await Assert.That(provider.GetRequiredService<IRespireClient>().IsConnected).IsFalse();
        await connector.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task FailedConnectionRetriesUntilRedisIsAvailable()
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        var factory = options.TestingStreamFactory!;
        var attempts = 0;
        var services = CreateServices();
        services.AddRespire(_ => options with
        {
            Connections = 1,
            TestingStreamFactory = (host, port, token) => Interlocked.Increment(ref attempts) <= 2
                ? ValueTask.FromException<Stream>(new IOException("Redis unavailable"))
                : factory(host, port, token),
        });
        services.Configure<RespireStartupOptions>(startup => startup.InitialRetryDelay = TimeSpan.FromMilliseconds(10));
        await using var provider = services.BuildServiceProvider();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);
        await connector.Connecting.WaitAsync(Timeout);

        await Assert.That(provider.GetRequiredService<IRespireClient>().IsConnected).IsTrue();
        await Assert.That(Volatile.Read(ref attempts)).IsGreaterThanOrEqualTo(3);
        await connector.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopEndsRetryBackoff()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RespireFakeServer();
        var services = CreateServices();
        services.AddRespire(_ => server.CreateOptions() with
        {
            Connections = 1,
            TestingStreamFactory = (_, _, _) =>
            {
                attempted.TrySetResult();
                return ValueTask.FromException<Stream>(new IOException("Redis unavailable"));
            },
        });
        services.Configure<RespireStartupOptions>(startup => startup.InitialRetryDelay = TimeSpan.FromHours(1));
        await using var provider = services.BuildServiceProvider();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);
        await attempted.Task.WaitAsync(Timeout);
        await connector.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        await Assert.That(connector.Connecting.IsCompletedSuccessfully).IsTrue();
        await Assert.That(provider.GetRequiredService<IRespireClient>().IsConnected).IsFalse();
    }

    [Test]
    public async Task ClientCreationFailureDoesNotFailStartup()
    {
        var services = CreateServices();
        services.AddRespire(_ => throw new InvalidOperationException("No Redis configured"));
        await using var provider = services.BuildServiceProvider();

        var connector = Connector(provider);
        await connector.StartAsync(CancellationToken.None);
        await connector.Connecting.WaitAsync(Timeout);

        await Assert.That(connector.Connecting.IsCompletedSuccessfully).IsTrue();
        await connector.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task ConnectorIsRegisteredOnce()
    {
        var services = CreateServices();
        services.AddRespire("localhost");
        services.AddKeyedRespire("a", "localhost");
        services.AddKeyedRespire("b", "localhost");
        await using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetServices<IHostedService>().OfType<RespireStartupConnector>().Count()).IsEqualTo(1);
    }
}
