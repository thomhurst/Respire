using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Runtime;
using Respire.Testing.Containers;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.Orleans.Acceptance.Tests;

public partial class SiloScenarios
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cluster_MembershipWrites_AfterSlotMove(bool exerciseAsk)
    {
        await using var redis = await ClusterDeployment.Start();
        await using var connections = new ProviderConnections(ClusterOptions(redis), shared: true);
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterId = Guid.NewGuid().ToString("N");
        await using var silo = BuildSilo(connections, serviceId, clusterId);
        await silo.Start();
        var table = silo.Host.Services.GetRequiredService<IMembershipTable>();
        var key = (string)RedisClusteringOptions.DefaultCreateRedisKey(new() { ServiceId = serviceId, ClusterId = clusterId })!;
        var slot = (int)await redis.Command(0, "KEYSLOT", key);
        var source = Math.Min(slot / 5461, 2);
        var target = (source + 1) % 3;
        var before = await table.ReadAll();
        await redis.BeginMigration(source, target, slot, key);
        if (exerciseAsk)
        {
            var entry = new MembershipEntry
            {
                SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11001), 1),
                HostName = "localhost", SiloName = "migration-probe", Status = SiloStatus.Joining,
                StartTime = DateTime.UtcNow, IAmAliveTime = DateTime.UtcNow
            };
            var error = await Assert.ThrowsAsync<RespireTransactionRetryException>(() => table.InsertRow(entry, before.Version.Next()));
            Assert.Equal("ASK", error.ServerError.Code);
            var rejected = await table.ReadAll();
            Assert.Equal(before.Version.Version, rejected.Version.Version);
            Assert.DoesNotContain(rejected.Members, row => row.Item1.SiloAddress.Equals(entry.SiloAddress));
        }
        await redis.FinishMigration(target, slot);

        // The existing adapter has cached the old route. A new silo writes membership through it.
        await using var joined = BuildSilo(connections, serviceId, clusterId);
        await joined.Start();
        Assert.Equal(2, (await table.ReadAll()).Members.Count(row => row.Item1.Status == SiloStatus.Active));
        await using var client = BuildClient(connections, serviceId, clusterId);
        await client.Start();
        var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("moved");
        await grain.SetValue(42);
        Assert.Equal(42, await grain.GetValue());
    }

    [Test]
    public async Task Cluster_ActiveSilos_SurvivePrimaryPromotion()
    {
        await using var redis = await ClusterDeployment.Start();
        await using var connections = new ProviderConnections(ClusterOptions(redis), shared: true);
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterId = Guid.NewGuid().ToString("N");
        await using var silo = BuildSilo(connections, serviceId, clusterId);
        await silo.Start();
        await using var client = BuildClient(connections, serviceId, clusterId);
        await client.Start();
        var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("promotion");
        var activation = await grain.GetActivation();
        await grain.SetValue(42);
        await grain.RegisterReminder();
        Assert.Equal((activation, 42), await Delivery(silo));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // Promote every primary's replica so membership, grain state, and reminders all change owners.
        // Coordinated FAILOVER fences replication; this does not claim lossless crash failover.
        for (var replica = 3; replica < 6; replica++)
        {
            await redis.Command(replica, "FAILOVER");
            var node = replica;
            await ClusterDeployment.Until(async () => ((string)(await redis.Control(node).ExecuteAsync("ROLE"))[0]!) == "master", deadline.Token);
        }
        await grain.SetValue(43);
        Assert.Equal(activation, await grain.GetActivation());
        var reader = silo.Host.Services.GetRequiredService<ReminderDeliveries>().Received.Reader;
        (Guid Activation, int Value) delivered;
        do { delivered = await reader.ReadAsync(deadline.Token); } while (delivered.Value != 43);
        Assert.Equal(activation, delivered.Activation);
        await using var joined = BuildSilo(connections, serviceId, clusterId);
        await joined.Start();
        Assert.Equal(2, (await silo.Host.Services.GetRequiredService<IMembershipTable>().ReadAll())
            .Members.Count(row => row.Item1.Status == SiloStatus.Active));
        await client.DisposeAsync();
        await silo.DisposeAsync();
        await using var nextClient = BuildClient(connections, serviceId, clusterId);
        await nextClient.Start();
        var restored = nextClient.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("promotion");
        Assert.NotEqual(activation, await restored.GetActivation());
        Assert.Equal(43, await restored.GetValue());
        await restored.RemoveReminder();
    }

    [Test]
    public async Task AbruptSiloDeath_IsEvictedWithinDeadline()
    {
        await using var redis = await StartRedis(RespireContainerTopology.Standalone);
        await using var connections = new ProviderConnections(Options(redis), shared: true);
        var serviceId = Guid.NewGuid().ToString("N");
        var clusterId = Guid.NewGuid().ToString("N");
        await using var survivor = BuildSilo(connections, serviceId, clusterId, fastDetection: true);
        await survivor.Start();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SiloScenarios).Assembly.Location);
        foreach (var argument in new[] { "--child-silo", redis.DataEndpoints[0].ToString(), serviceId, clusterId })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var table = survivor.Host.Services.GetRequiredService<IMembershipTable>();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await ClusterDeployment.Until(async () =>
            {
                Assert.False(child.HasExited, "Child silo exited before joining.");
                return (await table.ReadAll()).Members.Count(row => row.Item1.Status == SiloStatus.Active) == 2;
            }, startup.Token);
            var local = survivor.Host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;
            var victim = Assert.Single((await table.ReadAll()).Members.Where(row => !row.Item1.SiloAddress.Equals(local))).Item1.SiloAddress;
            var elapsed = Stopwatch.StartNew();
            child.Kill(entireProcessTree: true); // No StopAsync: membership must be updated by the survivor.
            using var eviction = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await child.WaitForExitAsync(eviction.Token);
            await ClusterDeployment.Until(async () => (await table.ReadRow(victim)).Members
                .Any(row => row.Item1.Status == SiloStatus.Dead), eviction.Token);
            Console.WriteLine($"Abrupt silo death observed as Dead after {elapsed.Elapsed.TotalSeconds:F2}s (60s budget).");
            Assert.Equal(SiloStatus.Active, Assert.Single((await table.ReadRow(local)).Members).Item1.Status);
            await using var client = BuildClient(connections, serviceId, clusterId);
            await client.Start();
            var grain = client.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("survivor");
            await grain.SetValue(42);
            Assert.Equal(42, await grain.GetValue());
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Console.WriteLine(await stdout);
            Console.WriteLine(await stderr);
        }
    }

    internal static async Task RunChildSilo(string endpoint, string serviceId, string clusterId)
    {
        await using var connections = new ProviderConnections(RespireOptions.Parse($"redis://{endpoint}/0") with
        {
            Protocol = Environment.GetEnvironmentVariable("RESPIRE_TEST_PROTOCOL") == "2" ? RespireProtocol.Resp2 : RespireProtocol.Resp3
        }, shared: true);
        await using var silo = BuildSilo(connections, serviceId, clusterId, fastDetection: true);
        await silo.Start();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static RespireOptions ClusterOptions(ClusterDeployment redis) => new()
    {
        Endpoints = [new("127.0.0.1", redis.Port(0))], UseCluster = true, Connections = 1,
        Protocol = Environment.GetEnvironmentVariable("RESPIRE_TEST_PROTOCOL") == "2" ? RespireProtocol.Resp2 : RespireProtocol.Resp3,
        ConnectTimeout = TimeSpan.FromSeconds(3), CommandTimeout = TimeSpan.FromSeconds(10)
    };
}
