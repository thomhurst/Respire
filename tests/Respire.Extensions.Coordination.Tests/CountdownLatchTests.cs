using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Respire.Testing.Containers;

namespace Respire.Extensions.Coordination.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class CountdownLatchTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ConcurrentSignalsReleaseWaitersExactlyAtZero()
    {
        await using var client = await ConnectAsync();
        var key = Key();
        var coordination = new RespireCoordination(client);
        var latch = await coordination.CreateCountdownLatchAsync(key, 32);
        var wait = latch.WaitAsync().AsTask();
        var remaining = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => latch.CountDownAsync().AsTask()));

        await Assert.That(remaining.Count(value => value == 0)).IsEqualTo(1);
        await Assert.That(remaining.Min()).IsEqualTo(0L);
        await Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(async () => await latch.CountDownAsync()).Throws<RespireServerException>();
    }

    [Test]
    public async Task ResetStartsNewGenerationAndReleasesOldWaiters()
    {
        await using var client = await ConnectAsync();
        var coordination = new RespireCoordination(client);
        var key = Key();
        var old = await coordination.CreateCountdownLatchAsync(key, 1);
        var waiting = old.WaitAsync().AsTask();
        var current = await coordination.ResetCountdownLatchAsync(key, 2);

        await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(await old.CountDownAsync()).IsEqualTo(-1L);
        await Assert.That(await current.CountDownAsync()).IsEqualTo(1L);
        await Assert.That(await current.CountDownAsync()).IsEqualTo(0L);
        await Assert.That(await current.WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }

    [Test]
    public async Task BinaryPrefixedKeyIsSnapshottedAndCancellationDoesNotSignal()
    {
        await using var client = await ConnectAsync();
        var view = client.WithKeyPrefix($"latch:{Guid.NewGuid():N}:");
        byte[] bytes = [0xff, 0, 1];
        var latch = await new RespireCoordination(view).CreateCountdownLatchAsync(bytes, 1);
        bytes[2] = 2;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await latch.CountDownAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(await latch.CountDownAsync()).IsEqualTo(0L);
        await Assert.That(await latch.WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }

    [Test]
    public async Task ValidationAndSingleUseCreationAreEnforced()
    {
        await using var client = await ConnectAsync();
        var coordination = new RespireCoordination(client);
        var key = Key();
        await Assert.That(async () => await coordination.CreateCountdownLatchAsync(key, -1))
            .Throws<ArgumentOutOfRangeException>();
        await coordination.CreateCountdownLatchAsync(key, 0);
        await Assert.That(async () => await coordination.CreateCountdownLatchAsync(key, 1))
            .Throws<RespireServerException>();
    }

    [Test]
    public async Task JoinedClientSharesGenerationAndPreservesInt64Precision()
    {
        await using var owner = await ConnectAsync();
        await using var participant = await ConnectAsync();
        var key = Key();
        var ownerCoordination = new RespireCoordination(owner);
        var participantCoordination = new RespireCoordination(participant);
        var initialCount = 9_007_199_254_740_993L;
        var latch = await ownerCoordination.CreateCountdownLatchAsync(key, initialCount);

        var joined = await participantCoordination.JoinCountdownLatchAsync(key);
        await Assert.That(joined).IsNotNull();
        await Assert.That(await joined!.CountDownAsync()).IsEqualTo(initialCount - 1);
        await Assert.That(await latch.CountDownAsync()).IsEqualTo(initialCount - 2);
        await Assert.That(await new RespireCoordination(participant).JoinCountdownLatchAsync(Key())).IsNull();
    }

    [Test]
    public async Task JoinedClientWaitsForSignalFromAnotherClient()
    {
        await using var owner = await ConnectAsync();
        await using var participant = await ConnectAsync();
        var key = Key();
        var latch = await new RespireCoordination(owner).CreateCountdownLatchAsync(key, 1);
        var joined = await new RespireCoordination(participant).JoinCountdownLatchAsync(key);
        var waiting = joined!.WaitAsync().AsTask();
        await latch.CountDownAsync();

        await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ClusterRunsLatchScriptsOnTaggedKey(int protocol)
    {
        await using var cluster = await RespireContainerFixture.StartAsync(new() { Topology = RespireContainerTopology.Cluster });
        await using var client = await RespireClient.ConnectAsync(cluster.CreateOptions() with
        {
            Protocol = (RespProtocol)protocol,
            Connections = 1,
        });
        var coordination = new RespireCoordination(client.WithKeyPrefix("coord:"));
        var latch = await coordination.CreateCountdownLatchAsync("{batch}:latch", 2);
        var waiter = latch.WaitAsync().AsTask();
        await Assert.That(await latch.CountDownAsync()).IsEqualTo(1L);
        await Assert.That(await latch.CountDownAsync()).IsEqualTo(0L);
        await Assert.That(await waiter.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }

    private ValueTask<RespireClient> ConnectAsync() => RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
        Connections = 2, Protocol = RespProtocol.Resp3,
    });

    private static RespireKey Key() => $"{{{Guid.NewGuid():N}}}:latch";
}
