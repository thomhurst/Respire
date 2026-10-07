using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.FusionCache.Tests;

public class DistributedLockerClusterTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [NotInParallel]
    public async Task GeneratedKeysAcquireRenewAndRetainCountersOnRealCluster(int protocol)
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var first = new RespireFusionCacheDistributedLocker(client.WithKeyPrefix("cluster-test:"),
            new() { LeaseDuration = TimeSpan.FromSeconds(2) });
        await using var second = new RespireFusionCacheDistributedLocker(client.WithKeyPrefix("cluster-test:"));
        var owners = new Dictionary<int, (string Name, RespireFusionCacheLock Handle)>();
        for (var candidate = 0; owners.Count < 3 && candidate < 64; candidate++)
        {
            var name = "cache-" + candidate;
            var owner = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(first, name, TimeSpan.Zero))!;
            var leaseSlot = await client.Server.ClusterKeySlotAsync("cluster-test:" + owner.LeaseKey.ToString());
            var counterSlot = await client.Server.ClusterKeySlotAsync("cluster-test:" + owner.FencingCounterKey.ToString());
            await Assert.That(leaseSlot).IsEqualTo(counterSlot);
            var primary = leaseSlot < 5461 ? 0 : leaseSlot < 10922 ? 1 : 2;
            if (!owners.TryAdd(primary, (name, owner))) await DistributedLockerTests.ReleaseAsync(first, name, owner);
        }
        await Assert.That(owners.Count).IsEqualTo(3);
        await Task.Delay(TimeSpan.FromSeconds(3));
        foreach (var (primary, (name, owner)) in owners)
        {
            if (owner.OwnershipLost) throw new InvalidOperationException($"Cluster lease renewal lost primary {primary}.", owner.RenewalFailure);
            await Assert.That(await DistributedLockerTests.AcquireAsync(second, name, TimeSpan.Zero)).IsNull();
            await DistributedLockerTests.ReleaseAsync(first, name, owner);
            var next = (RespireFusionCacheLock)(await DistributedLockerTests.AcquireAsync(second, name, TimeSpan.Zero))!;
            await Assert.That(next.FencingToken).IsEqualTo(owner.FencingToken + 1);
        }
        // The same core renewal operation used by keep-alive is owner checked on the slot owner.
        await using var lease = await new Coordination.RespireCoordination(client).TryAcquireFencedLockAsync(
            "{renew}:lease", "{renew}:counter", TimeSpan.FromSeconds(20));
        await using var renewal = await lease.Lock.KeepAliveAsync();
        await Assert.That(await lease.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(20))).IsTrue();
        var counterTtl = await client.Keys.ExpiryAsync("{renew}:counter");
        await Assert.That(counterTtl.Exists && !counterTtl.HasExpiry).IsTrue();
    }
}
