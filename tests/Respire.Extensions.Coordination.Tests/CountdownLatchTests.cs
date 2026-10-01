using System.Diagnostics;
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
        var options = cluster.CreateOptions() with
        {
            Protocol = (RespProtocol)protocol,
            Connections = 1,
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var coordination = new RespireCoordination(client.WithKeyPrefix("coord:"));
        var latch = await coordination.CreateCountdownLatchAsync("{batch}:latch", 1);
        var subscribeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = SubscribeConfirmationListener(
            options.Endpoints.Select(static endpoint => endpoint.Port).ToHashSet(), subscribeReady);
        var waiter = latch.WaitAsync().AsTask();
        await subscribeReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(await latch.CountDownAsync()).IsEqualTo(0L);
        await Assert.That(await waiter.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
    }

    [Test]
    public async Task PublishPermissionFailureLeavesLatchStateUnchanged()
    {
        await using var admin = await ConnectAsync();
        var key = Key();
        var original = await new RespireCoordination(admin).CreateCountdownLatchAsync(key, 1);
        var username = $"latch-no-publish-{Guid.NewGuid():N}";
        const string password = "latch-test-password";
        (await admin.ExecuteAsync("ACL", "SETUSER", username, "reset", "on", $">{password}", "~*", "+@all", "-publish")).Dispose();
        RespireClient? restricted = null;
        try
        {
            restricted = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new(fixture.Host, fixture.Port)],
                Database = fixture.Database,
                Username = username,
                Password = password,
                Protocol = RespProtocol.Resp3,
            });
            var coordination = new RespireCoordination(restricted);
            RespireServerException? resetError = null;
            try { _ = await coordination.ResetCountdownLatchAsync(key, 2); }
            catch (RespireServerException error) { resetError = error; }
            await Assert.That(resetError?.Message.Contains("publish", StringComparison.OrdinalIgnoreCase)).IsTrue();

            var stillCurrent = await new RespireCoordination(admin).JoinCountdownLatchAsync(key);
            await Assert.That(stillCurrent?.Generation).IsEqualTo(original.Generation);
            var restrictedLatch = await coordination.JoinCountdownLatchAsync(key);
            RespireServerException? countDownError = null;
            try { _ = await restrictedLatch!.CountDownAsync(); }
            catch (RespireServerException error) { countDownError = error; }
            await Assert.That(countDownError?.Message.Contains("publish", StringComparison.OrdinalIgnoreCase)).IsTrue();
            using var remaining = await admin.ExecuteAsync("HGET", key, "remaining");
            await Assert.That(remaining.AsString()).IsEqualTo("1");
        }
        finally
        {
            if (restricted is not null) await restricted.DisposeAsync();
            (await admin.ExecuteAsync("ACL", "DELUSER", username)).Dispose();
        }
    }

    private static ActivityListener SubscribeConfirmationListener(
        HashSet<int> ports, TaskCompletionSource confirmed)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") is "SUBSCRIBE"
                    && activity.GetTagItem("server.port") is int port && ports.Contains(port))
                    confirmed.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private ValueTask<RespireClient> ConnectAsync() => RespireClient.ConnectAsync(new RespireOptions
    {
        Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
        Connections = 2, Protocol = RespProtocol.Resp3,
    });

    private static RespireKey Key() => $"{{{Guid.NewGuid():N}}}:latch";
}
