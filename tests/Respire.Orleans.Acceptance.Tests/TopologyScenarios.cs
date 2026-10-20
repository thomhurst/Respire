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
        // Own the probe table until migration completes. A live silo's heartbeat can invalidate
        // WATCH on the membership hash even when its table version has not changed.
        await using var table = new RedisMembershipTable(
            Microsoft.Extensions.Options.Options.Create(new RedisClusteringOptions
            {
                CreateMultiplexer = _ => connections.Create("migration-probe")
            }),
            Microsoft.Extensions.Options.Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId }));
        await table.InitializeMembershipTable(true);
        var key = (string)RedisClusteringOptions.DefaultCreateRedisKey(new() { ServiceId = serviceId, ClusterId = clusterId })!;
        var slot = (int)await redis.Command(0, "KEYSLOT", key);
        var source = Math.Min(slot / 5461, 2);
        var target = (source + 1) % 3;
        var before = await table.ReadAll();
        Assert.Empty(before.Members);
        Assert.Equal(0, before.Version.Version);
        await redis.BeginMigration(source, target, slot, key);
        try
        {
            if (exerciseAsk)
            {
                var entry = new MembershipEntry
                {
                    SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11001), 1),
                    HostName = "localhost", SiloName = "migration-probe", Status = SiloStatus.Joining,
                    StartTime = DateTime.UtcNow, IAmAliveTime = DateTime.UtcNow
                };
                var accepted = await table.InsertRow(entry, before.Version.Next());
                var inserted = await table.ReadRow(entry.SiloAddress);
                Assert.True(accepted,
                    $"ASK insert returned false: before={before.Version.Version}, observed={inserted.Version.Version}, rows={inserted.Members.Count}.");
                var row = Assert.Single(inserted.Members);
                Assert.Equal(before.Version.Next().Version, inserted.Version.Version);
                Assert.False(await table.InsertRow(entry, inserted.Version.Next())); // duplicate
                entry.Status = SiloStatus.ShuttingDown;
                Assert.False(await table.UpdateRow(entry, row.Item2, before.Version.Next())); // stale version
                var rejected = await table.ReadRow(entry.SiloAddress);
                Assert.Equal(inserted.Version.Version, rejected.Version.Version);
                Assert.Equal(SiloStatus.Joining, Assert.Single(rejected.Members).Item1.Status);
                Assert.True(await table.UpdateRow(entry, row.Item2, inserted.Version.Next()));
                var heartbeat = entry.IAmAliveTime.AddSeconds(10);
                await table.UpdateIAmAlive(new MembershipEntry { SiloAddress = entry.SiloAddress, IAmAliveTime = heartbeat });
                var updated = await table.ReadRow(entry.SiloAddress);
                Assert.Equal(inserted.Version.Next().Version, updated.Version.Version);
                Assert.Equal(SiloStatus.ShuttingDown, Assert.Single(updated.Members).Item1.Status);
                Assert.Equal(heartbeat, Assert.Single(updated.Members).Item1.IAmAliveTime);
                // The synthetic row is not a real silo and must not enter membership discovery.
                await table.DeleteMembershipTableEntries(clusterId);
            }
        }
        finally
        {
            // Restore routability even when an assertion fails.
            await redis.FinishMigration(target, slot);
        }

        // The same adapter has cached the old route (ASK must not replace it). Real silos now
        // initialize and write membership through MOVED without racing the controlled probe.
        await using var silo = BuildSilo(connections, serviceId, clusterId);
        await silo.Start();
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
        // A persisted reminder must activate the grain on the remaining silo before any client call.
        var recoveredDelivery = await Delivery(joined);
        Assert.NotEqual(activation, recoveredDelivery.Activation);
        Assert.Equal(43, recoveredDelivery.Value);
        await using var nextClient = BuildClient(connections, serviceId, clusterId);
        await nextClient.Start();
        var restored = nextClient.Host.Services.GetRequiredService<IClusterClient>().GetGrain<ILifecycleGrain>("promotion");
        Assert.Equal(recoveredDelivery.Activation, await restored.GetActivation());
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
