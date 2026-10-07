using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class PubSubPrefixTests
{
    [Test]
    [MatrixDataSource]
    public async Task TypedRoutesRetainPhysicalIdentity(
        [Matrix(false, true)] bool push,
        [Matrix(SubscriptionKind.Channel, SubscriptionKind.Pattern, SubscriptionKind.Sharded)] SubscriptionKind kind)
    {
        RespireChannel physicalTarget = default;
        var verb = kind switch { SubscriptionKind.Pattern => "PSUBSCRIBE", SubscriptionKind.Sharded => "SSUBSCRIBE", _ => "SUBSCRIBE" };
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith(verb + " ", StringComparison.Ordinal)
                || command.StartsWith(verb.Replace("SUBSCRIBE", "UNSUBSCRIBE") + " ", StringComparison.Ordinal)
                ? Confirmation(command[..command.IndexOf(' ')].ToLowerInvariant(), physicalTarget.Bytes.ToArray()) : null,
        };
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
        });
        byte[] prefix = [255, 0, (byte)'*', (byte)'?', (byte)'[', (byte)']', (byte)'\\', (byte)':'];
        var view = root.WithKeyPrefix("keys:").WithPubSubPrefix((RespireKey)prefix);
        var logical = new RespireChannel("item");
        var target = kind switch
        {
            SubscriptionKind.Pattern => RespireChannel.Pattern("i*"),
            SubscriptionKind.Sharded => RespireChannel.Sharded(logical),
            _ => logical,
        };
        var physical = view.ResolveChannel(logical);
        physicalTarget = view.ResolveChannel(target);
        prefix[0] = 1;
        await using var first = await view.SubscribeAsync(target);
        await using var duplicate = await root.SubscribeAsync(physicalTarget);
        await using var firstReader = first.GetAsyncEnumerator();
        await using var duplicateReader = duplicate.GetAsyncEnumerator();
        var firstRead = firstReader.MoveNextAsync().AsTask();
        var duplicateRead = duplicateReader.MoveNextAsync().AsTask();
        var receivers = kind == SubscriptionKind.Sharded
            ? await view.PublishAsync(RespireChannel.Sharded(logical), "before") : await view.PublishAsync(logical, "before");
        await Assert.That(receivers).IsEqualTo(1L);
        var subscriptionConnection = server.ReceivedConnectionIds[0];
        await server.SendRawAsync(Message("before"), subscriptionConnection);
        await Assert.That(await firstRead.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(await duplicateRead.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(firstReader.Current.Channel).IsEqualTo(physical);
        await Assert.That(duplicateReader.Current.Channel).IsEqualTo(physical);
        await Assert.That(first.Targets[0]).IsEqualTo(physicalTarget);
        await Assert.That(firstReader.Current.Pattern).IsEqualTo(kind == SubscriptionKind.Pattern ? physicalTarget : (RespireChannel?)null);
        await first.DisposeAsync();
        await view.DisposeAsync();
        var retainedRead = duplicateReader.MoveNextAsync().AsTask();
        if (kind == SubscriptionKind.Sharded) await root.PublishShardedAsync(physical, "retained");
        else await root.PublishAsync(physical, "retained");
        await server.SendRawAsync(Message("retained"), subscriptionConnection);
        await Assert.That(await retainedRead.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(duplicateReader.Current.Text).IsEqualTo("retained");
        await Assert.That(root.IsConnected).IsTrue();
        await Assert.That(view.ResolveKey("item")).IsEqualTo((RespireKey)"keys:item");
        await duplicate.DisposeAsync();
        var commands = server.ReceivedArguments;
        await Assert.That(commands.Select(arguments => Encoding.ASCII.GetString(arguments[0])))
            .IsEquivalentTo([verb, verb, kind == SubscriptionKind.Sharded ? "SPUBLISH" : "PUBLISH",
                kind == SubscriptionKind.Sharded ? "SPUBLISH" : "PUBLISH", verb.Replace("SUBSCRIBE", "UNSUBSCRIBE")]);
        await Assert.That(commands[0][1].SequenceEqual(physicalTarget.Bytes.ToArray())).IsTrue();
        await Assert.That(commands[1][1].SequenceEqual(physicalTarget.Bytes.ToArray())).IsTrue();
        await Assert.That(commands[2][1].SequenceEqual(physical.Bytes.ToArray())).IsTrue();
        await Assert.That(commands[^1][1].SequenceEqual(physicalTarget.Bytes.ToArray())).IsTrue();

        byte[] Message(string payload) => kind == SubscriptionKind.Pattern
            ? Frame(push, "pmessage"u8.ToArray(), physicalTarget.Bytes.ToArray(), physical.Bytes.ToArray(), Encoding.UTF8.GetBytes(payload))
            : Frame(push, Encoding.ASCII.GetBytes(kind == SubscriptionKind.Sharded ? "smessage" : "message"),
                physical.Bytes.ToArray(), Encoding.UTF8.GetBytes(payload));
    }

    [Test]
    public async Task RootAndDerivedOptionsOwnIndependentBinaryNamespaces()
    {
        byte[] configured = [255, 0, (byte)':'];
        await using var root = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 6379)], KeyPrefix = "keys:", PubSubPrefix = configured,
            ReplicaEndpoints = [new("localhost", 6380)], ClientSideCache = new(),
        });
        configured[0] = 1;
        var view = (RespireClient)root.WithPubSubPrefix("inner:").WithKeyPrefix("nested:")
            .WithReadFrom(RespireReadFrom.Replica).WithoutClientCache();
        byte[] expected = [255, 0, (byte)':', .. "inner:item"u8];
        await Assert.That(view.ResolveChannel("item").Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(view.PrimaryReadView.ResolveChannel("item").Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(view.ForDeferredBatch().ResolveChannel("item").Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(view.ResolveKey("item")).IsEqualTo((RespireKey)"keys:nested:item");
        byte[] rootExpected = [255, 0, (byte)':', .. "item"u8];
        await Assert.That(root.ResolveChannel("item").Bytes.Span.SequenceEqual(rootExpected)).IsTrue();
    }

    [Test]
    [Arguments("redis://localhost?keyPrefix=keys%3A&pubSubPrefix=events%3A", "events:")]
    [Arguments("localhost,keyPrefix=keys%3A,pubSubPrefix=events%3A", "events:")]
    [Arguments("redis://localhost?pubSubPrefix=%252C", "%2C")]
    [Arguments("localhost,pubSubPrefix=%252C", "%2C")]
    [Arguments("redis://localhost?pubSubPrefix=first&pubSubPrefix=", "")]
    [Arguments("localhost,pubSubPrefix=first,pubSubPrefix=", "")]
    public async Task ConnectionStringsDecodePubSubPrefixOnce(string connectionString, string expected)
    {
        await using var root = RespireClient.Create(RespireOptions.Parse(connectionString));
        await Assert.That(root.ResolveChannel("item").Bytes.Span.SequenceEqual(Encoding.UTF8.GetBytes(expected + "item"))).IsTrue();
    }

    [Test]
    public async Task NotificationDescriptorsStayPhysicalAndPrefixesRemainExplicit()
    {
        await using var root = RespireClient.Create("localhost,keyPrefix=keys%3A");
        var view = root.WithPubSubPrefix("events:").WithKeyPrefix("nested:");
        var key = view.ResolveKey("item");
        var descriptor = RespireChannel.KeySpaceSingleKey(key, 2);
        await Assert.That(view.ResolveChannel(descriptor)).IsEqualTo(descriptor);
        await Assert.That(view.ResolveChannel(descriptor).RoutingSlot).IsEqualTo(descriptor.RoutingSlot);
        await Assert.That(view.ResolveChannel(descriptor).NotificationDatabase).IsEqualTo(2);
        var message = new RespireMessage(descriptor, null, "set"u8.ToArray(), root.Core.Options.Serializer);
        await Assert.That(message.TryParseKeyNotification(view.ResolveKey(RespireKey.Empty).ToBytes(), out var notification)).IsTrue();
        await Assert.That(notification.KeyBytes.Span.SequenceEqual("item"u8)).IsTrue();
        await Assert.That(message.Channel).IsEqualTo(descriptor);
        await Assert.That(view.ResolveChannel(new RespireChannel(descriptor.Bytes)).ToString())
            .IsEqualTo("events:__keyspace@2__:keys:nested:item");
    }

    [Test]
    public async Task RawPublishAndAdministrativeQueriesRemainPhysical()
    {
        await using var server = new FakeRespServer(4, ":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("PUBSUB CHANNELS", StringComparison.Ordinal)
                ? "*0\r\n"u8.ToArray() : null,
        };
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = root.WithPubSubPrefix("events:");
        using (await view.ExecuteAsync("PUBLISH", "item", "raw")) { }
        using (await view.ExecuteAsync($"PUBLISH item raw")) { }
        _ = await view.Server.PubSubChannelsAsync((RespireChannel)"item*");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PUBLISH item raw", "PUBLISH item raw", "PUBSUB CHANNELS item*"]);
    }

    [Test]
    public async Task EmptyAndInvalidTextPrefixesAreRejected()
    {
        await using var root = RespireClient.Create("localhost");
        await Assert.That(() => root.WithPubSubPrefix(RespireKey.Empty)).Throws<ArgumentException>();
        await Assert.That(() => root.WithPubSubPrefix("")).Throws<ArgumentException>();
        await Assert.That(() => root.WithPubSubPrefix("\uD800")).Throws<ArgumentException>();
        await Assert.That(() => RespireClient.Create(new RespireOptions
            { Endpoints = [new("localhost", 6379)], PubSubPrefix = "\uDC00" })).Throws<ArgumentException>();
        await Assert.That(root.WithKeyPrefix("keys:").ResolveChannel("item")).IsEqualTo((RespireChannel)"item");
    }

    private static byte[] Confirmation(string verb, byte[] target)
    {
        var frame = Frame(false, Encoding.ASCII.GetBytes(verb), target);
        frame[1] = (byte)'3';
        return [.. frame, .. ":1\r\n"u8];
    }

    private static byte[] Frame(bool push, params byte[][] values)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"{(push ? '>' : '*')}{values.Length}\r\n"));
        foreach (var value in values)
        {
            stream.Write(Encoding.ASCII.GetBytes($"${value.Length}\r\n"));
            stream.Write(value);
            stream.Write("\r\n"u8);
        }
        return stream.ToArray();
    }
}
