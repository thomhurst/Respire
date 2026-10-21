using Microsoft.Extensions.Caching.Distributed;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class TrackedReplicaRedirectTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CacheSetGetAndRefreshReleaseRedirectTargets(bool sentinel, bool clientCache)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel, clientCache);
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        await using var cache = new RespireDistributedCache(client);
        var pools = client.Core.OwnedPools.Count;
        await cache.SetAsync("key", [1], new DistributedCacheEntryOptions());
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        var normal = fixture.Target.ReplyOverride!;
        fixture.Target.ReplyOverride = (id, command) => command.StartsWith("EVAL ")
            ? "*2\r\n:0\r\n$1\r\nx\r\n"u8.ToArray() : normal(id, command);
        await Assert.That(await cache.GetAsync("key")).IsEquivalentTo("x"u8.ToArray());
        await cache.RefreshAsync("key");
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(fixture.Target.ReceivedCommands.Count(command => command == "CLIENT ID")).IsEqualTo(3);
        await Assert.That(fixture.Source.ReceivedCommands.Count(command => command.StartsWith("EVAL "))).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledCacheRefreshCorrectsActualTarget(bool clientCache)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel: false, clientCache);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Target.SuppressReply = command =>
        {
            if (!command.StartsWith("EVAL ") || command.Contains("redis.call('TIME')")) return false;
            submitted.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        await using var cache = new RespireDistributedCache(client);
        var pools = client.Core.OwnedPools.Count;
        using var caller = new CancellationTokenSource();
        var pending = cache.RefreshAsync("key", caller.Token);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Only the original script stays unanswered; the correction gets a definitive reply.
        fixture.Target.SuppressReply = null;
        caller.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(fixture.Target.ReceivedCommands.Contains("CLIENT KILL ID 99")).IsTrue();
        await Assert.That(fixture.Source.ReceivedCommands.Contains("CLIENT KILL ID 41")).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }
}
