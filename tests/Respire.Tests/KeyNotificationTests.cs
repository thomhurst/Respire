using System.Text;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class KeyNotificationTests
{
    private static readonly SystemTextJsonSerializer Serializer = new();

    [Test]
    [Arguments("__keyspace@12__:tenant:key", "hset", RespireKeyNotificationKind.KeySpace)]
    [Arguments("__keyevent@12__:hset", "tenant:key", RespireKeyNotificationKind.KeyEvent)]
    [Arguments("__subkeyspace@12__:tenant:key", "hset|0:,3:a,b", RespireKeyNotificationKind.SubKeySpace)]
    [Arguments("__subkeyevent@12__:hset", "10:tenant:key|0:,3:a,b", RespireKeyNotificationKind.SubKeyEvent)]
    [Arguments("__subkeyspaceitem@12__:tenant:key\n", "hset", RespireKeyNotificationKind.SubKeySpaceItem)]
    [Arguments("__subkeyspaceevent@12__:hset|tenant:key", "0:,3:a,b", RespireKeyNotificationKind.SubKeySpaceEvent)]
    public async Task ParsesEveryLayoutAndStripsOnlyExplicitPrefix(string channel, string payload, RespireKeyNotificationKind kind)
    {
        var message = Message(channel, payload);
        await Assert.That(message.TryParseKeyNotification(out var physical)).IsTrue();
        await Assert.That(physical.Kind).IsEqualTo(kind);
        await Assert.That(physical.Database).IsEqualTo(12);
        await Assert.That(physical.Key).IsEqualTo(new RespireKey("tenant:key"));
        await Assert.That(physical.Type).IsEqualTo(RespireKeyNotificationType.HSet);
        await Assert.That(message.TryParseKeyNotification("tenant:"u8, out var logical)).IsTrue();
        await Assert.That(logical.Key).IsEqualTo(new RespireKey("key"));
        await Assert.That(logical.Channel).IsEqualTo(message.Channel);
        await Assert.That(logical.RawValue.Equals(message.Payload)).IsTrue();
        await Assert.That(message.TryParseKeyNotification("wrong:"u8, out var mismatch)).IsFalse();
        await Assert.That(mismatch.Kind).IsEqualTo(RespireKeyNotificationKind.Unknown);
        await Assert.That(message.TryParseKeyNotification("tenant:key"u8, out var empty)).IsTrue();
        await Assert.That(empty.Key.IsEmpty).IsTrue();
        var expected = kind is RespireKeyNotificationKind.KeyEvent or RespireKeyNotificationKind.KeySpace ? 0
            : kind == RespireKeyNotificationKind.SubKeySpaceItem ? 1 : 2;
        await Assert.That(physical.GetSubKeys().Count).IsEqualTo(expected);
        await Assert.That(physical.HasSubKey).IsEqualTo(expected != 0);
        if (expected != 0) await Assert.That(physical.GetSubKeys().FirstOrDefault().IsEmpty).IsTrue();
        if (expected == 2) await Assert.That(Encoding.UTF8.GetString(physical.GetSubKeys().ToArray()[1].Span)).IsEqualTo("a,b");
        var destination = new byte[3];
        await Assert.That(logical.TryCopyKey(destination, out var written)).IsTrue();
        await Assert.That(written).IsEqualTo(3);
        await Assert.That(destination.AsSpan().SequenceEqual("key"u8)).IsTrue();
        await Assert.That(logical.TryCopyKey(new byte[2], out written)).IsFalse();
        await Assert.That(written).IsEqualTo(0);
    }

    [Test]
    [Arguments("__keyspace@0__:", "future.module-event")]
    [Arguments("__keyevent@0__:future.module-event", "")]
    [Arguments("__subkeyspace@0__:", "future.module-event|0:")]
    [Arguments("__subkeyevent@0__:future.module-event", "0:|0:")]
    [Arguments("__subkeyspaceitem@0__:\n", "future.module-event")]
    [Arguments("__subkeyspaceevent@0__:future.module-event|", "0:")]
    public async Task UnknownEventsAndEmptyKeysRemainLossless(string channel, string payload)
    {
        var message = Message(channel, payload);
        await Assert.That(message.TryParseKeyNotification(out var notification)).IsTrue();
        await Assert.That(notification.Type).IsEqualTo(RespireKeyNotificationType.Unknown);
        await Assert.That(notification.RawType.Span.SequenceEqual("future.module-event"u8)).IsTrue();
        await Assert.That(notification.Key.IsEmpty).IsTrue();
    }

    [Test]
    public async Task KnownEventsRoundTripThroughFactories()
    {
        foreach (var type in Enum.GetValues<RespireKeyNotificationType>())
        {
            if (type == RespireKeyNotificationType.Unknown) continue;
            var channel = RespireChannel.KeyEvent(type, 0);
            var message = new RespireMessage(channel, null, "key"u8.ToArray(), Serializer);
            await Assert.That(message.TryParseKeyNotification(out var notification)).IsTrue();
            await Assert.That(notification.Type).IsEqualTo(type);
            await Assert.That(notification.RawType.Span.SequenceEqual(KeyNotificationTypes.Format(type))).IsTrue();
        }
    }

    [Test]
    [Arguments("ordinary", "set")]
    [Arguments("__keyspace@-1__:key", "set")]
    [Arguments("__keyspace@+1__:key", "set")]
    [Arguments("__keyspace@01__:key", "set")]
    [Arguments("__keyspace@2147483648__:key", "set")]
    [Arguments("__keyspace@99999999999999999999__:key", "set")]
    [Arguments("__keyspace@*__:key", "set")]
    [Arguments("__keyspace@__:key", "set")]
    [Arguments("__keyspace@0:key", "set")]
    [Arguments("__keyspace@0__:key", "")]
    [Arguments("__keyevent@0__:", "key")]
    [Arguments("__subkeyspace@0__:key", "hset")]
    [Arguments("__subkeyspace@0__:key", "hset|")]
    [Arguments("__subkeyspace@0__:key", "|1:a")]
    [Arguments("__subkeyspace@0__:key", "hset|-1:a")]
    [Arguments("__subkeyspace@0__:key", "hset|+1:a")]
    [Arguments("__subkeyspace@0__:key", "hset|01:a")]
    [Arguments("__subkeyspace@0__:key", "hset|1x:a")]
    [Arguments("__subkeyspace@0__:key", "hset|2147483648:a")]
    [Arguments("__subkeyspace@0__:key", "hset|1:")]
    [Arguments("__subkeyspace@0__:key", "hset|1:a,")]
    [Arguments("__subkeyspace@0__:key", "hset|1:a,1:")]
    [Arguments("__subkeyspace@0__:key", "hset|1:ab")]
    [Arguments("__subkeyspace@0__:key", "hset|0:garbage")]
    [Arguments("__subkeyevent@0__:hset", "4:key|1:a")]
    [Arguments("__subkeyevent@0__:hset", "3:key;1:a")]
    [Arguments("__subkeyevent@0__:hset", "3:key|")]
    [Arguments("__subkeyspaceitem@0__:key", "hset")]
    [Arguments("__subkeyspaceevent@0__:hset", "1:a")]
    [Arguments("__subkeyspaceevent@0__:hset|key", "")]
    public async Task MalformedFramesFailWithoutThrowing(string channel, string payload)
    {
        await Assert.That(Message(channel, payload).TryParseKeyNotification(out var notification)).IsFalse();
        await Assert.That(notification.Kind).IsEqualTo(RespireKeyNotificationKind.Unknown);
    }

    [Test]
    public async Task BinaryKeysAndSubKeysKeepEveryDelimiterAndStorage()
    {
        byte[] key = [0, 255, 128, (byte)':', (byte)',', (byte)'|', (byte)'\n'];
        byte[] field = [255, 0, (byte)',', (byte)':', (byte)'|', (byte)'\n'];
        byte[] payload = [.. "7:"u8, .. key, (byte)'|', .. "6:"u8, .. field, (byte)',', .. "0:"u8];
        var message = new RespireMessage("__subkeyevent@0__:hset", null, payload, Serializer);
        await Assert.That(message.TryParseKeyNotification(out var result)).IsTrue();
        await Assert.That(result.KeyBytes.Span.SequenceEqual(key)).IsTrue();
        var slices = result.GetSubKeys().ToArray();
        await Assert.That(slices.Length).IsEqualTo(2);
        await Assert.That(slices[0].Span.SequenceEqual(field)).IsTrue();
        await Assert.That(slices[1].IsEmpty).IsTrue();
        // Memory identity proves these views retain the original owned message buffer.
        await Assert.That(result.KeyBytes.Equals(message.Payload.Slice(2, key.Length))).IsTrue();
        await Assert.That(slices[0].Equals(message.Payload.Slice(12, field.Length))).IsTrue();
    }

    [Test]
    public async Task AllBinaryLayoutsAndFactoryStorageRemainOwned()
    {
        byte[] key = [255, 0, (byte)':', (byte)',', (byte)'|'];
        byte[] field = [128, (byte)'\n', (byte)',', (byte)':', (byte)'|', 0];
        var expectedKey = key.ToArray();
        var expectedField = field.ToArray();
        var exact = RespireChannel.SubKeySpaceSingleKey(key, 0);
        var item = RespireChannel.SubKeySpaceItem(key, field, 0);
        var eventKey = RespireChannel.SubKeySpaceEvent(RespireKeyNotificationType.HSet, key, 0);
        Array.Fill(key, (byte)'x');
        Array.Fill(field, (byte)'x');
        foreach (var descriptor in new[] { exact, item, eventKey })
        {
            byte[] payload = descriptor == item ? "hset"u8.ToArray()
                : descriptor == exact ? [.. "hset|6:"u8, .. expectedField] : [.. "6:"u8, .. expectedField];
            var message = new RespireMessage(descriptor, null, payload, Serializer);
            await Assert.That(message.TryParseKeyNotification(out var notification)).IsTrue();
            await Assert.That(notification.KeyBytes.Span.SequenceEqual(expectedKey)).IsTrue();
            await Assert.That(notification.GetSubKeys().FirstOrDefault().Span.SequenceEqual(expectedField)).IsTrue();
            await Assert.That(notification.Type).IsEqualTo(RespireKeyNotificationType.HSet);
        }
    }

    [Test]
    public async Task FactoriesRetainKindDatabaseAndPhysicalSlot()
    {
        var exact = RespireChannel.KeySpaceSingleKey("tenant:{tag}:key", 12);
        await Assert.That(exact.ToString()).IsEqualTo("__keyspace@12__:tenant:{tag}:key");
        await Assert.That(exact.Kind).IsEqualTo(SubscriptionKind.Channel);
        await Assert.That(exact.RoutingScope).IsEqualTo(RespireChannelRoutingScope.KeyOwner);
        await Assert.That(exact.RoutingSlot).IsEqualTo(new RespireKey("tenant:{tag}:key").ClusterSlot);
        await Assert.That(exact.NotificationDatabase).IsEqualTo(12);
        var prefix = RespireChannel.KeySpacePrefix("a*?[]\\");
        await Assert.That(prefix.ToString()).IsEqualTo("__keyspace@*__:a\\*\\?\\[\\]\\\\*");
        await Assert.That(prefix.Kind).IsEqualTo(SubscriptionKind.Pattern);
        await Assert.That(prefix.RoutingScope).IsEqualTo(RespireChannelRoutingScope.AllPrimaries);
        var eventKey = RespireChannel.SubKeySpaceEvent("new*"u8, "a?key");
        await Assert.That(eventKey.ToString()).IsEqualTo("__subkeyspaceevent@*__:new\\*|a\\?key");
        await Assert.That(eventKey.RoutingSlot).IsEqualTo(new RespireKey("a?key").ClusterSlot);
        var ordinary = new RespireChannel(exact.Bytes);
        await Assert.That(ordinary.IsNotification).IsFalse();
        await Assert.That(ordinary).IsEqualTo(exact);
        await Assert.That(ordinary.GetHashCode()).IsEqualTo(exact.GetHashCode());
    }

    [Test]
    public async Task InvalidFactoriesAndKindOverridesAreRejected()
    {
        await Assert.That(() => RespireChannel.KeySpaceSingleKey("key", -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireChannel.KeyEvent(RespireKeyNotificationType.Unknown)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => RespireChannel.KeyEvent(Array.Empty<byte>())).Throws<ArgumentException>();
        await Assert.That(() => RespireChannel.SubKeySpaceItem("key\n", "field", 0)).Throws<ArgumentException>();
        await Assert.That(() => RespireChannel.SubKeySpaceEvent("bad|event"u8, "key")).Throws<ArgumentException>();
        await Assert.That(() => RespireChannel.Sharded(RespireChannel.KeySpaceSingleKey("key", 0))).Throws<ArgumentException>();
        await Assert.That(() => RespireChannel.Literal(RespireChannel.KeySpacePattern("*"))).Throws<ArgumentException>();
    }

    [Test]
    public async Task PublishingDescriptorsFailsBeforeNetworkWork()
    {
        await using var client = RespireClient.Create("localhost:1");
        var descriptor = RespireChannel.KeySpaceSingleKey("key", 0);
        await Assert.That(async () => await client.PublishAsync(descriptor, "set")).Throws<ArgumentException>();
        await Assert.That(async () => await client.PublishShardedAsync(descriptor, "set")).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(0)]
    public async Task ClusterDescriptorsNeverSilentlyUseOneArbitraryPrimary(int database)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("localhost", 1)],
        });
        var descriptor = RespireChannel.KeySpaceSingleKey("key", database);
        if (database == 0)
            await Assert.That(async () => await client.SubscribeAsync(descriptor)).Throws<NotSupportedException>();
        else
            await Assert.That(async () => await client.SubscribeAsync(descriptor)).Throws<ArgumentException>();
    }

    [Test]
    public async Task RepeatedParsingAndStructEnumerationAllocateNothing()
    {
        var message = Message("__subkeyevent@0__:hset", "3:key|1:a,0:,3:x,y");
        for (var index = 0; index < 100; index++) ParseAndCount(message);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var index = 0; index < 1000; index++) count += ParseAndCount(message);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(count).IsEqualTo(4000);
        await Assert.That(allocated).IsEqualTo(0L);
    }

    private static int ParseAndCount(RespireMessage message)
    {
        if (!message.TryParseKeyNotification("key"u8, out var notification)) return -1;
        var count = 0;
        foreach (var key in notification.GetSubKeys()) count += key.Length;
        return count;
    }
    private static RespireMessage Message(string channel, string payload)
        => new(channel, null, Encoding.UTF8.GetBytes(payload), Serializer);
}
