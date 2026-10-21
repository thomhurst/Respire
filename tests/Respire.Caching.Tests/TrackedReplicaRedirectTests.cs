using Microsoft.Extensions.Caching.Distributed;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class TrackedReplicaRedirectTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DetachedCorrectionWithoutClientPermissionsRetainsRedirectTarget(bool write)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel: false, cache: false);
        var sourceReply = fixture.Source.ReplyOverride!;
        fixture.Source.ReplyOverride = (id, command) => command == "CLIENT ID"
            ? "-NOPERM identity denied\r\n"u8.ToArray() : sourceReply(id, command);
        var getPrefix = "EVAL " + (write ? RespireDistributedCache.SetScript : RespireDistributedCache.GetAndRefreshScript).Source;
        var correctionPrefix = "EVAL " + (write ? RespireDistributedCache.CapTtlScript : RespireDistributedCache.CapRefreshedTtlScript).Source;
        var targetReply = fixture.Target.ReplyOverride!;
        fixture.Target.ReplyOverride = (id, command) => command.StartsWith(getPrefix, StringComparison.Ordinal)
            ? (write ? ":1\r\n"u8.ToArray() : "*2\r\n:1\r\n$1\r\nx\r\n"u8.ToArray()) : targetReply(id, command);
        fixture.Target.DelayCommand(getPrefix, 1100);
        var correcting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Target.GateCommand(correctionPrefix, release.Task, () => correcting.TrySetResult());
        // Identity is optional only for operations without cancellation or command deadlines.
        await using var client = await RespireClient.ConnectAsync(fixture.Options with { CommandTimeout = null });
        await using var cache = new RespireDistributedCache(client)
        {
            CorrectionWaitBound = TimeSpan.FromMilliseconds(50),
        };
        var pools = client.Core.OwnedPools.Count;
        var pending = write
            ? cache.SetAsync("key", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) })
            : cache.GetAsync("key");
        try
        {
            await correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools + 1);
        }
        finally { release.TrySetResult(); }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.OwnedPools.Count != pools) await Task.Delay(10, deadline.Token);
        await Assert.That(fixture.Source.ReceivedCommands.Any(command =>
            command.StartsWith(correctionPrefix, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DelayedSuccessfulReadCorrectsOnlyTheRedirectTarget(bool refresh, bool sentinel)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel, cache: false);
        var getPrefix = "EVAL " + RespireDistributedCache.GetAndRefreshScript.Source;
        var correctionPrefix = "EVAL " + RespireDistributedCache.CapRefreshedTtlScript.Source;
        var normal = fixture.Target.ReplyOverride!;
        fixture.Target.ReplyOverride = (id, command) => command.StartsWith(getPrefix, StringComparison.Ordinal)
            ? "*2\r\n:1\r\n$1\r\nx\r\n"u8.ToArray() : normal(id, command);
        // Both the read and the first correction exceed the tolerance. The next correction
        // must still use the physical target after the first pass establishes ordering.
        fixture.Target.DelayCommand(getPrefix, 1100);
        fixture.Target.DelayCommand(correctionPrefix, 1100);
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        await using var cache = new RespireDistributedCache(client);
        var pools = client.Core.OwnedPools.Count;

        if (refresh) await cache.RefreshAsync("key");
        else await Assert.That(await cache.GetAsync("key")).IsEquivalentTo("x"u8.ToArray());

        await Assert.That(fixture.Target.ReceivedCommands.Count(command =>
            command.StartsWith(correctionPrefix, StringComparison.Ordinal))).IsGreaterThanOrEqualTo(2);
        await Assert.That(fixture.Source.ReceivedCommands.Any(command =>
            command.StartsWith(correctionPrefix, StringComparison.Ordinal)
            || command.StartsWith($"EVALSHA {RespireDistributedCache.CapRefreshedTtlScript.Sha1} ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DelayedSuccessfulWriteCorrectsOnlyTheRedirectTarget(bool sentinel)
    {
        await using var fixture = new ReplicaRedirectTestFixture(sentinel, cache: false);
        var correctionPrefix = "EVAL " + RespireDistributedCache.CapTtlScript.Source;
        fixture.Target.DelayCommand("EVAL " + RespireDistributedCache.SetScript.Source, 1100);
        fixture.Target.DelayCommand(correctionPrefix, 1100);
        await using var client = await RespireClient.ConnectAsync(fixture.Options);
        await using var cache = new RespireDistributedCache(client);
        var pools = client.Core.OwnedPools.Count;

        await cache.SetAsync("key", [1], new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });

        await Assert.That(fixture.Target.ReceivedCommands.Count(command =>
            command.StartsWith(correctionPrefix, StringComparison.Ordinal))).IsGreaterThanOrEqualTo(2);
        await Assert.That(fixture.Source.ReceivedCommands.Any(command =>
            command.StartsWith(correctionPrefix, StringComparison.Ordinal)
            || command.StartsWith($"EVALSHA {RespireDistributedCache.CapTtlScript.Sha1} ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

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
