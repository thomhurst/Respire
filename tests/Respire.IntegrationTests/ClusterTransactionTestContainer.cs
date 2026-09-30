using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>A real single-primary Redis Cluster for same-slot transaction semantics.</summary>
public sealed class ClusterTransactionTestContainer : IAsyncInitializer, IAsyncDisposable
{
    private IContainer? _container;
    private IContainer Container => _container ?? throw new InvalidOperationException("Cluster has not started.");
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(6379);

    public async Task InitializeAsync()
    {
        var container = new ContainerBuilder("redis:7.0.15")
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--cluster-enabled", "yes", "--cluster-config-file", "nodes.conf",
                "--cluster-announce-ip", "127.0.0.1", "--appendonly", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        _container = container;
        try
        {
            await container.StartAsync();
            // Discovery must advertise the mapped client port, not the container-private port.
            await RunAsync(["redis-cli", "-e", "CONFIG", "SET", "cluster-announce-port", Port.ToString()]);
            await RunAsync(["redis-cli", "-e", "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var result = await container.ExecAsync(["redis-cli", "CLUSTER", "INFO"], ready.Token);
                if (result.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) break;
                await Task.Delay(100, ready.Token);
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task RunAsync(string[] command)
    {
        var result = await Container.ExecAsync(command);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Cluster setup failed: {result.Stdout} {result.Stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        var container = Interlocked.Exchange(ref _container, null);
        if (container is not null) await container.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
