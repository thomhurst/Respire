using Respire.Protocol;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = "HybridCacheCoherence")]
public class PayloadLimitedSubscriptionTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2, SubscriptionKind.Channel)]
    [Arguments(3, SubscriptionKind.Channel)]
    [Arguments(2, SubscriptionKind.Pattern)]
    [Arguments(3, SubscriptionKind.Pattern)]
    [Arguments(2, SubscriptionKind.Sharded)]
    [Arguments(3, SubscriptionKind.Sharded)]
    public async Task PayloadLimitsArePerSubscriptionAndRetainOrderedGaps(int protocol, SubscriptionKind kind)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { Protocol = (RespProtocol)protocol });
        var name = "payload:" + Guid.NewGuid().ToString("N");
        var channel = kind == SubscriptionKind.Sharded ? RespireChannel.Sharded(name) : new RespireChannel(name);
        var target = kind == SubscriptionKind.Pattern ? RespireChannel.Pattern(name + "*") : channel;
        await using var bounded = await client.SubscribeAsync(target, new RespireSubscriptionOptions(2)
        { MaxPayloadBytes = 3 }, CancellationToken.None);
        await using var unrestricted = await client.SubscribeAsync(target);
        var gap = new TaskCompletionSource<RespireSubscriptionGap>(TaskCreationOptions.RunContinuationsAsynchronously);
        bounded.DeliveryGap += value => gap.TrySetResult(value);
        await client.PublishAsync(channel, "ok");
        await client.PublishAsync(channel, "oversized");
        await client.PublishAsync(channel, "end");
        await Assert.That((await gap.Task.WaitAsync(TimeSpan.FromSeconds(10))).Reason)
            .IsEqualTo(RespireSubscriptionGapReason.PayloadTooLarge);
        await Assert.That(bounded.DroppedMessages).IsEqualTo(1);
        await using var reader = bounded.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("ok");
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("end");
        await using var other = unrestricted.GetAsyncEnumerator();
        foreach (var expected in new[] { "ok", "oversized", "end" })
        {
            await Assert.That(await other.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
            await Assert.That(other.Current.Text).IsEqualTo(expected);
        }
    }
}
