using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Respire.StackExchangeCompat;
using Respire.Testing.Containers;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.Orleans.Acceptance.Tests;

// Real hosts use only the adapter. ProviderScenarios supplies the official-client control.
[NotInParallel]
public class SiloScenarios
{
    [Test]
    [Arguments(RespireContainerTopology.Standalone, true)]
    [Arguments(RespireContainerTopology.Standalone, false)]
    [Arguments(RespireContainerTopology.Cluster, true)]
    public async Task Activation_Reminders_Restart_AndOwnership(RespireContainerTopology topology, bool shared)
    {
        await using var redis = await StartRedis(topology);
        await using var connections = new ProviderConnections(Options(redis), shared);
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterId = Guid.NewGuid().ToString("N");
        var grainKey = Guid.NewGuid().ToString("N");
        Guid firstActivation;

        await using (var silo = BuildSilo(connections, serviceId, clusterId))
        {
            await silo.Start();
            await using var client = BuildClient(connections, serviceId, clusterId);
            await client.Start();
            var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>(grainKey);
            firstActivation = await grain.GetActivation();
            await grain.SetValue(42);
            Assert.Equal(42, await grain.GetValue());
            await grain.RegisterReminder();
            Assert.Equal((firstActivation, 42), await Delivery(silo));

            // Dispose the silo while the separate client host still borrows its gateway connection.
            await silo.DisposeAsync();
            await connections.AssertSiloDisposed();
            await connections.AssertClientUsable(client);
        }
        await connections.AssertAllProvidersDisposed();

        await using (var silo = BuildSilo(connections, serviceId, clusterId))
        {
            await silo.Start();
            // No client call or registration: Redis's reminder must activate the persisted grain.
            var delivered = await Delivery(silo);
            Assert.NotEqual(firstActivation, delivered.Activation);
            Assert.Equal(42, delivered.Value);
            await using var client = BuildClient(connections, serviceId, clusterId);
            await client.Start();
            var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>(grainKey);
            Assert.Equal(delivered.Activation, await grain.GetActivation());
            Assert.Equal(42, await grain.GetValue());
            await grain.SetValue(43);
            await grain.RemoveReminder();
        }
        await connections.AssertAllProvidersDisposed();
    }

    [Test]
    [Arguments(RespireContainerTopology.Standalone)]
    [Arguments(RespireContainerTopology.Sentinel)]
    public async Task ActiveSilo_RecoversRedisConnection_AndRetainsState(RespireContainerTopology topology)
    {
        await using var redis = await StartRedis(topology);
        await using var connections = new ProviderConnections(Options(redis), shared: true);
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterId = Guid.NewGuid().ToString("N");
        await using var silo = BuildSilo(connections, serviceId, clusterId);
        await silo.Start();
        await using var client = BuildClient(connections, serviceId, clusterId);
        await client.Start();
        var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("recovery");
        var activation = await grain.GetActivation();
        await grain.SetValue(42);
        await grain.RegisterReminder();
        Assert.Equal((activation, 42), await Delivery(silo));

        var native = connections.SharedNative;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = 0;
        native.ConnectionStateChanged += change =>
        {
            if (change.State != RespireConnectionState.Connected) Interlocked.Exchange(ref disconnected, 1);
            if (change.State == RespireConnectionState.Connected
                && (topology == RespireContainerTopology.Sentinel
                    ? change.Endpoint == redis.DataEndpoints[1]
                    : Volatile.Read(ref disconnected) != 0))
                recovered.TrySetResult();
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (topology == RespireContainerTopology.Sentinel)
        {
            // A later marker on a control connection fences the replication stream containing
            // all completed provider writes. WAIT must run on the marker's own connection.
            using var control = await ConnectionMultiplexer.ConnectAsync($"{redis.DataEndpoints[0]}");
            await control.GetDatabase().StringSetAsync(serviceId + "/replication-fence", "ready");
            Assert.Equal(1, (long)await control.GetDatabase().ExecuteAsync("WAIT", 1, 10000));
            await redis.StopDataNodeAsync(0, deadline.Token);
        }
        else
        {
            // Kill real TCP connections without destroying data or replacing the adapter.
            using var control = await ConnectionMultiplexer.ConnectAsync($"{redis.DataEndpoints[0]},allowAdmin=true");
            var killed = (long)await control.GetDatabase().ExecuteAsync("CLIENT", "KILL", "TYPE", "normal", "SKIPME", "yes");
            Assert.True(killed > 0);
        }
        try
        {
            await recovered.Task.WaitAsync(deadline.Token);
        }
        catch
        {
            // Capture the election evidence before fixture cleanup removes the server logs.
            using var diagnostics = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { Console.WriteLine(await redis.ReadServerLogsAsync(diagnostics.Token)); }
            catch (Exception error) { Console.WriteLine($"Server diagnostics unavailable: {error.Message}"); }
            throw;
        }
        Assert.Equal(activation, await grain.GetActivation());
        Assert.Equal(42, await grain.GetValue());
        await grain.SetValue(43);
        // Clear buffered pre-recovery callbacks; require a delivery which observes the new value.
        var reader = silo.Host.Services.GetRequiredService<ReminderDeliveries>().Received.Reader;
        (Guid Activation, int Value) delivery;
        do { delivery = await reader.ReadAsync(deadline.Token); } while (delivery.Value != 43);
        Assert.Equal(activation, delivery.Activation);
        // A second silo joins through the recovered Redis membership provider.
        await using var verification = BuildSilo(connections, serviceId, clusterId);
        await verification.Start();
        var table = silo.Host.Services.GetRequiredService<IMembershipTable>();
        Assert.Equal(2, (await table.ReadAll()).Members.Count(row => row.Item1.Status == SiloStatus.Active));
        if (topology == RespireContainerTopology.Sentinel)
        {
            await redis.StartDataNodeAsync(0, deadline.Token);
            await redis.WaitForDataNodeReplicaAsync(0, deadline.Token);
            await grain.SetValue(44);
            Assert.Equal(redis.DataEndpoints[1], native.Endpoint);
        }
        await client.DisposeAsync();
        await silo.DisposeAsync();
        await using var nextClient = BuildClient(connections, serviceId, clusterId);
        await nextClient.Start();
        var reactivated = nextClient.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("recovery");
        Assert.NotEqual(activation, await reactivated.GetActivation());
        Assert.Equal(topology == RespireContainerTopology.Sentinel ? 44 : 43, await reactivated.GetValue());
        await reactivated.RemoveReminder();
    }

    private static Task<RespireContainerFixture> StartRedis(RespireContainerTopology topology) =>
        RespireContainerFixture.StartAsync(new() { Topology = topology, Image = "redis:7.0.15" });

    private static RespireOptions Options(RespireContainerFixture redis) => redis.CreateOptions() with
    {
        Connections = 1,
        Protocol = Environment.GetEnvironmentVariable("RESPIRE_TEST_PROTOCOL") switch
        {
            "2" => RespireProtocol.Resp2,
            "3" => RespireProtocol.Resp3,
            null or "" => RespireProtocol.Auto,
            var value => throw new InvalidOperationException($"Invalid RESPIRE_TEST_PROTOCOL: {value}")
        },
        ConnectTimeout = TimeSpan.FromSeconds(3),
        CommandTimeout = TimeSpan.FromSeconds(10)
    };

    private static HostedScenario BuildSilo(ProviderConnections connections, string serviceId, string clusterId)
    {
        var (siloPort, gatewayPort) = FreePorts();
        var builder = new HostBuilder().ConfigureLogging(log => log.AddConsole().SetMinimumLevel(LogLevel.Warning));
        builder.UseOrleans(silo =>
        {
            silo.Configure<ClusterOptions>(options => { options.ServiceId = serviceId; options.ClusterId = clusterId; });
            silo.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = IPAddress.Loopback;
                options.SiloPort = siloPort;
                options.GatewayPort = gatewayPort;
            });
            silo.Configure<ReminderOptions>(options =>
            {
                options.MinimumReminderPeriod = TimeSpan.FromSeconds(1);
                options.RefreshReminderListPeriod = TimeSpan.FromSeconds(1);
            });
            silo.UseRedisClustering(options =>
            {
                options.ConfigurationOptions = connections.Configuration;
                options.CreateMultiplexer = _ => connections.Create("membership");
            });
            silo.AddRedisGrainStorageAsDefault(options =>
            {
                options.ConfigurationOptions = connections.Configuration;
                options.CreateMultiplexer = _ => connections.Create("storage");
            });
            silo.UseRedisReminderService(options =>
            {
                options.ConfigurationOptions = connections.Configuration;
                options.CreateMultiplexer = _ => connections.Create("reminders");
            });
            silo.Services.AddSingleton<ReminderDeliveries>();
        });
        return new(builder.Build());
    }

    private static HostedScenario BuildClient(ProviderConnections connections, string serviceId, string clusterId)
    {
        var builder = new HostBuilder().ConfigureLogging(log => log.AddConsole().SetMinimumLevel(LogLevel.Warning));
        builder.UseOrleansClient(client =>
        {
            client.Configure<ClusterOptions>(options => { options.ServiceId = serviceId; options.ClusterId = clusterId; });
            client.UseRedisClustering(options =>
            {
                options.ConfigurationOptions = connections.Configuration;
                options.CreateMultiplexer = _ => connections.Create("gateway");
            });
        });
        return new(builder.Build());
    }

    private static async Task<(Guid Activation, int Value)> Delivery(HostedScenario silo)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        return await silo.Host.Services.GetRequiredService<ReminderDeliveries>().Received.Reader.ReadAsync(deadline.Token);
    }

    private static (int Silo, int Gateway) FreePorts()
    {
        // Keep both reservations open until selected so the OS cannot return the same port twice.
        using var silo = new TcpListener(IPAddress.Loopback, 0);
        using var gateway = new TcpListener(IPAddress.Loopback, 0);
        silo.Start();
        gateway.Start();
        return (((IPEndPoint)silo.LocalEndpoint).Port, ((IPEndPoint)gateway.LocalEndpoint).Port);
    }

    private sealed class HostedScenario(IHost host) : IAsyncDisposable
    {
        public IHost Host => host;
        private bool _disposed;
        public async Task Start()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await host.StartAsync(deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await host.StopAsync(deadline.Token); }
            finally
            {
                if (host is IAsyncDisposable disposable) await disposable.DisposeAsync();
                else host.Dispose();
            }
        }
    }

    private sealed class ProviderConnections(RespireOptions options, bool shared) : IAsyncDisposable
    {
        private readonly ConcurrentBag<(string Role, RespireConnectionMultiplexer Mux, RespireClient Native)> _created = [];
        private readonly object _gate = new();
        private readonly RespireClient _sharedNative = RespireClient.Create(options);
        private RespireConnectionMultiplexer? _shared;
        public RespireClient SharedNative => _sharedNative;
        public ConfigurationOptions Configuration => new()
        {
            EndPoints = { { options.Endpoints[0].Host, options.Endpoints[0].Port } },
            ServiceName = options.SentinelPrimaryName,
            DefaultDatabase = options.Database
        };

        public Task<(IConnectionMultiplexer, bool)> Create(string role)
        {
            lock (_gate)
            {
                var native = shared ? _sharedNative : RespireClient.Create(options);
                var mux = shared ? _shared ??= RespireConnectionMultiplexer.Wrap(native) : RespireConnectionMultiplexer.Wrap(native, ownsClient: true);
                _created.Add((role, mux, native));
                return Task.FromResult<(IConnectionMultiplexer, bool)>((mux, shared));
            }
        }

        public async Task AssertSiloDisposed()
        {
            Assert.Contains(_created, entry => entry.Role == "membership");
            Assert.Contains(_created, entry => entry.Role == "storage");
            Assert.Contains(_created, entry => entry.Role == "reminders");
            if (!shared)
                foreach (var entry in _created.Where(entry => entry.Role != "gateway"))
                {
                    Assert.Throws<ObjectDisposedException>(() => entry.Mux.GetDatabase());
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => entry.Native.PingAsync().AsTask());
                }
        }

        public async Task AssertClientUsable(HostedScenario client)
        {
            var gateway = Assert.Single(_created.Where(entry => entry.Role == "gateway"));
            Assert.NotNull(gateway.Mux.GetDatabase());
            Assert.Empty(await client.Host.Services.GetRequiredService<IGatewayListProvider>().GetGateways());
        }

        public async Task AssertAllProvidersDisposed()
        {
            if (!shared)
                foreach (var entry in _created)
                {
                    Assert.Throws<ObjectDisposedException>(() => entry.Mux.GetDatabase());
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => entry.Native.PingAsync().AsTask());
                }
            else
            {
                Assert.NotNull(_shared!.GetDatabase());
                await _sharedNative.PingAsync();
            }
            _created.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            // Cleanup also runs after failed assertions; ownership assertions happen before this fallback.
            foreach (var entry in _created) await entry.Mux.DisposeAsync();
            if (_shared is not null) await _shared.DisposeAsync();
            await _sharedNative.DisposeAsync();
        }
    }
}
