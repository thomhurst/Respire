using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.PubSub;

public class RespireChannelTests
{
    [Test]
    [Arguments("")]
    [Arguments("café:雪\0{channel}:\U0001F512")]
    [Arguments("__keyspace@0__:key")]
    public async Task TextAndBytesShareIdentity(string text)
    {
        RespireChannel channel = text;
        RespireChannel bytes = Encoding.UTF8.GetBytes(text);
        await Assert.That(channel).IsEqualTo(bytes);
        await Assert.That(channel.GetHashCode()).IsEqualTo(bytes.GetHashCode());
        await Assert.That(channel.ClusterSlot).IsEqualTo(new RespireKey(text).ClusterSlot);
        await Assert.That(channel.Kind).IsEqualTo(SubscriptionKind.Channel);
        await Assert.That(channel.ToString()).IsEqualTo(text);
        await Assert.That(RespireChannel.Pattern(channel)).IsEqualTo(bytes);
    }

    [Test]
    public async Task BinaryStorageIsOwnedAndDisplayIsNotIdentity()
    {
        byte[] input = [1, 0xff, 0, 0xfe, 2];
        var channel = new RespireChannel(input.AsMemory(1, 3));
        var hash = channel.GetHashCode();
        input[1] = 0;
        await Assert.That(channel.Bytes.ToArray()).IsEquivalentTo(new byte[] { 0xff, 0, 0xfe });
        await Assert.That(channel.GetHashCode()).IsEqualTo(hash);
        await Assert.That(channel.ClusterSlot).IsEqualTo(ClusterHash.GetSlot(channel.Bytes.Span));
        RespireChannel different = new byte[] { 0xfe, 0, 0xff };
        await Assert.That(channel == different).IsFalse();
        await Assert.That(channel.ToString()).IsEqualTo(different.ToString());
        await Assert.That(default(RespireChannel)).IsEqualTo((RespireChannel)"");
        await Assert.That(RespireChannel.Sharded(channel).Kind).IsEqualTo(SubscriptionKind.Sharded);
        await Assert.That(RespireChannel.Literal(RespireChannel.Pattern(channel)).Kind).IsEqualTo(SubscriptionKind.Channel);
    }

    [Test]
    public async Task InvalidTextAndNullAreRejected()
    {
        await Assert.That(() => new RespireChannel("\uD800")).Throws<ArgumentException>();
        await Assert.That(() => new RespireChannel("\uDC00")).Throws<ArgumentException>();
        await Assert.That(() => new RespireChannel((string)null!)).Throws<ArgumentNullException>();
        await Assert.That(() => (RespireChannel)(byte[])null!).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task RawBytesDoNotAllocate()
    {
        RespireChannel channel = "notifications";
        var before = GC.GetAllocatedBytesForCurrentThread();
        var length = 0;
        for (var i = 0; i < 1000; i++) length += channel.Bytes.Length;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(length).IsEqualTo(13000);
        await Assert.That(allocated).IsEqualTo(0);
    }
}
