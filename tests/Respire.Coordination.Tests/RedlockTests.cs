using System.Diagnostics;
using Testcontainers.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.Coordination.Tests;

[ClassDataSource<RedlockRedisCluster>(Shared = SharedType.PerTestSession)]
public class RedlockTests(RedlockRedisCluster fixture)
{
    [Test]
    public async Task QuorumContentionRenewalAndReleaseWorkAcrossIndependentNodes()
    {
        var clients = fixture.CreateClients();
        try
        {
            var group = new RespireRedlockGroup(clients);
            var key = (RespireKey)$"redlock:{Guid.NewGuid():N}";
            await using var first = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(10));
            await Assert.That(first.Acquired).IsTrue();

            await using var contender = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(10));
            await Assert.That(contender.Acquired).IsFalse();

            await Assert.That(await first.Lock.ResetExpiryAsync(TimeSpan.FromSeconds(10))).IsTrue();
            await Assert.That(first.Lock.RemainingEstimate > TimeSpan.Zero).IsTrue();
            await Assert.That(await first.Lock.ReleaseAsync()).IsTrue();
            await Assert.That(first.Lock.IsReleased).IsTrue();

            await using var replacement = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(10));
            await Assert.That(replacement.Acquired).IsTrue();
        }
        finally
        {
            await fixture.DisposeClientsAsync(clients);
        }
    }

    [Test]
    public async Task FailedQuorumCleansPartialAcquisition()
    {
        var clients = fixture.CreateClients();
        var key = (RespireKey)$"redlock:{Guid.NewGuid():N}";
        var blocker = new RespireLockToken(new byte[] { 1, 2, 3 });
        try
        {
            var group = new RespireRedlockGroup(clients);
            await Assert.That(await clients[0].Locks.TryTakeAsync(key, blocker, TimeSpan.FromSeconds(10))).IsTrue();
            await Assert.That(await clients[1].Locks.TryTakeAsync(key, blocker, TimeSpan.FromSeconds(10))).IsTrue();
            await using var failed = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(10));
            await Assert.That(failed.Acquired).IsFalse();
            await Assert.That(await clients[2].Locks.GetOwnerTokenAsync(key)).IsNull();
        }
        finally
        {
            await clients[0].Locks.ReleaseAsync(key, blocker);
            await clients[1].Locks.ReleaseAsync(key, blocker);
            await fixture.DisposeClientsAsync(clients);
        }
    }

    [Test]
    public async Task GroupRejectsEvenOrRepeatedClientSets()
    {
        var clients = fixture.CreateClients();
        try
        {
            await Assert.That(() => new RespireRedlockGroup(clients.Take(2))).Throws<ArgumentException>();
            await Assert.That(() => new RespireRedlockGroup([clients[0], clients[1], clients[0]])).Throws<ArgumentException>();
        }
        finally
        {
            foreach (var client in clients) await client.DisposeAsync();
        }
    }

    [Test]
    public async Task PartitionBoundsNodeWaitAndRejectsWhenQuorumIsUnavailable()
    {
        var containers = Enumerable.Range(0, 3).Select(_ => new RedisBuilder("redis:7.0.15").Build()).ToArray();
        var clients = Array.Empty<IRespireClient>();
        try
        {
            foreach (var container in containers) await container.StartAsync();
            clients = containers.Select(container => (IRespireClient)RespireClient.Create(new RespireOptions
            {
                Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
                Connections = 1,
            })).ToArray();
            var group = new RespireRedlockGroup(clients, new RespireRedlockOptions { NodeTimeout = TimeSpan.FromMilliseconds(100) });
            await containers[2].StopAsync();

            var key = (RespireKey)$"redlock:{Guid.NewGuid():N}";
            await using (var quorum = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(5)))
                await Assert.That(quorum.Acquired).IsTrue();

            await containers[1].StopAsync();
            var started = Stopwatch.GetTimestamp();
            await using var unavailable = await group.TryAcquireAsync(key, TimeSpan.FromSeconds(5));
            await Assert.That(unavailable.Acquired).IsFalse();
            await Assert.That(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3)).IsTrue();
        }
        finally
        {
            await fixture.DisposeClientsAsync(clients);
            foreach (var container in containers) await container.DisposeAsync();
        }
    }
}

public sealed class RedlockRedisCluster : IAsyncInitializer, IAsyncDisposable
{
    private readonly List<RedisContainer> _containers = [];

    public async Task InitializeAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            var container = new RedisBuilder("redis:7.0.15").Build();
            await container.StartAsync();
            _containers.Add(container);
        }
    }

    public IRespireClient[] CreateClients() => _containers.Select(container =>
        (IRespireClient)RespireClient.Create(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Connections = 1,
        })).ToArray();

    public async ValueTask DisposeClientsAsync(IEnumerable<IRespireClient> clients)
    {
        foreach (var client in clients) await client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers) await container.DisposeAsync();
        _containers.Clear();
    }
}
