using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BinaryPubSubTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdenticalLiteralAndPatternBytesKeepIndependentSubscriptions(bool push)
    {
        byte[] bytes = [0xff, 0, (byte)'x'];
        await using var server = new FakeRespServer(
            Confirmation("subscribe", bytes), Confirmation("psubscribe", bytes),
            Confirmation("unsubscribe", bytes), Confirmation("punsubscribe", bytes));
        await using var client = CreateClient(server.Port);
        await using var literal = await client.SubscribeAsync(RespireChannel.Literal(bytes));
        await using var pattern = await client.SubscribeAsync(RespireChannel.Pattern(bytes));
        await using var literalReader = literal.GetAsyncEnumerator();
        await using var patternReader = pattern.GetAsyncEnumerator();

        await server.SendRawAsync(Frame(push, "message"u8.ToArray(), bytes, "literal"u8.ToArray()));
        await server.SendRawAsync(Frame(push, "pmessage"u8.ToArray(), bytes, bytes, "pattern"u8.ToArray()));
        await Assert.That(await literalReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(await patternReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(literalReader.Current.Text).IsEqualTo("literal");
        await Assert.That(literalReader.Current.Pattern).IsNull();
        await Assert.That(patternReader.Current.Text).IsEqualTo("pattern");
        await Assert.That(patternReader.Current.Pattern!.Value.Bytes.Span.SequenceEqual(bytes)).IsTrue();

        await literal.DisposeAsync();
        await server.SendRawAsync(Frame(push, "pmessage"u8.ToArray(), bytes, bytes, "retained"u8.ToArray()));
        await Assert.That(await patternReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(patternReader.Current.Text).IsEqualTo("retained");
        await pattern.DisposeAsync();
        await Assert.That(server.ReceivedArguments.Select(arguments => Encoding.ASCII.GetString(arguments[0])))
            .IsEquivalentTo(["SUBSCRIBE", "PSUBSCRIBE", "UNSUBSCRIBE", "PUNSUBSCRIBE"]);
    }

    [Test]
    public async Task CancelledActivationReconnectsExistingBinaryRoutes()
    {
        byte[] bytes = [0xff, 0, (byte)'x'];
        var expected = bytes.ToArray();
        await using var server = new FakeRespServer(2, Confirmation("subscribe", expected));
        server.SuppressReply = command => command == "SUBSCRIBE stalled";
        await using var client = CreateClient(server.Port);
        await using var subscription = await client.SubscribeAsync((RespireChannel)bytes);
        Array.Fill(bytes, (byte)'z');
        using var cancellation = new CancellationTokenSource();
        var pending = client.SubscribeAsync("stalled", cancellation.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < 2) await Task.Delay(10, deadline.Token);
        await cancellation.CancelAsync();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        while (server.CommandsSeen < 3) await Task.Delay(10, deadline.Token);
        await Assert.That(server.ReceivedArguments[2][1].SequenceEqual(expected)).IsTrue();
        await Assert.That(server.ReceivedConnectionIds[2]).IsNotEqualTo(server.ReceivedConnectionIds[0]);
        await subscription.DisposeAsync();
        await Assert.That(server.ReceivedArguments[^1][1].SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(false, SubscriptionKind.Channel)]
    [Arguments(true, SubscriptionKind.Channel)]
    [Arguments(false, SubscriptionKind.Pattern)]
    [Arguments(true, SubscriptionKind.Pattern)]
    [Arguments(false, SubscriptionKind.Sharded)]
    [Arguments(true, SubscriptionKind.Sharded)]
    public async Task RawTargetsAndMessagesSurviveFrameLifetime(bool push, SubscriptionKind kind)
    {
        byte[] input = [0xff, 0, (byte)':', (byte)'{', (byte)'x', (byte)'}', (byte)'*'];
        var expected = input.ToArray();
        RespireChannel channel = new RespireChannel(input).WithKind(kind);
        var verb = kind switch { SubscriptionKind.Pattern => "PSUBSCRIBE", SubscriptionKind.Sharded => "SSUBSCRIBE", _ => "SUBSCRIBE" };
        await using var server = new FakeRespServer(Confirmation(verb.ToLowerInvariant(), expected));
        await using var client = CreateClient(server.Port);
        await using var subscription = await client.SubscribeAsync(channel);
        Array.Fill(input, (byte)'z');
        await Assert.That(server.ReceivedArguments[0][1].SequenceEqual(expected)).IsTrue();
        await Assert.That(subscription.Targets[0].Bytes.Span.SequenceEqual(expected)).IsTrue();
        await Assert.That(subscription.Kind).IsEqualTo(kind);

        byte[] concrete = kind == SubscriptionKind.Pattern ? [0xff, 0, (byte)':', (byte)'x'] : expected;
        byte[] payload = [0xfe, 0, 0xff];
        var frame = kind == SubscriptionKind.Pattern
            ? Frame(push, "pmessage"u8.ToArray(), expected, concrete, payload)
            : Frame(push, Encoding.ASCII.GetBytes(kind == SubscriptionKind.Sharded ? "smessage" : "message"), concrete, payload);
        await using var reader = subscription.GetAsyncEnumerator();
        await server.SendRawAsync(frame);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        var retained = reader.Current;
        await server.SendRawAsync(frame);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(retained.Channel.Bytes.Span.SequenceEqual(concrete)).IsTrue();
        await Assert.That(retained.Payload.Span.SequenceEqual(payload)).IsTrue();
        if (kind == SubscriptionKind.Pattern)
        {
            await Assert.That(retained.Pattern!.Value.Bytes.Span.SequenceEqual(expected)).IsTrue();
        }
        else
        {
            await Assert.That(retained.Pattern).IsNull();
            await Assert.That(retained.Channel.Bytes.Equals(subscription.Targets[0].Bytes)).IsTrue();
        }
        await subscription.DisposeAsync();
        await Assert.That(server.ReceivedArguments[^1][1].SequenceEqual(expected)).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(server.ReceivedArguments[^1][0])).IsEqualTo(verb.Replace("SUBSCRIBE", "UNSUBSCRIBE"));
    }

    [Test]
    [Arguments("")]
    [Arguments("café\0:__keyspace@0__:{snow}")]
    public async Task ByteEquivalentTargetsDeduplicateAndUnsubscribeAfterLastConsumer(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var server = new FakeRespServer(Confirmation("subscribe", bytes));
        await using var client = CreateClient(server.Port);
        RespireChannel[] channels = [text, bytes];
        var first = await client.SubscribeAsync(channels, CancellationToken.None);
        var second = await client.SubscribeAsync((RespireChannel)bytes);
        await Assert.That(first.Targets.Count).IsEqualTo(1);
        await first.DisposeAsync();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        await second.DisposeAsync();
        await Assert.That(server.ReceivedArguments[^1][0].SequenceEqual("UNSUBSCRIBE"u8.ToArray())).IsTrue();
        await Assert.That(server.ReceivedArguments[^1][1].SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublishWritesExactBytesAndSnapshotsCallerInput(bool sharded)
    {
        byte[] input = [0xff, 0, (byte)'{', (byte)'x', (byte)'}'];
        var expected = input.ToArray();
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = CreateClient(server.Port);
        var publication = sharded
            ? client.PublishAsync(RespireChannel.Sharded(input), "payload")
            : client.PublishAsync(input, "payload");
        Array.Fill(input, (byte)'z');
        await Assert.That(await publication).IsEqualTo(1);
        await Assert.That(server.ReceivedArguments[0][1].SequenceEqual(expected)).IsTrue();
        await Assert.That(Encoding.ASCII.GetString(server.ReceivedArguments[0][0])).IsEqualTo(sharded ? "SPUBLISH" : "PUBLISH");
    }

    [Test]
    public async Task MixedKindsAndPatternsForPublicationFailBeforeConnecting()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = CreateClient(server.Port);
        RespireChannel[] channels = ["same", RespireChannel.Pattern("same")];
        await Assert.That(async () => await client.SubscribeAsync(channels, CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(async () => await client.PublishAsync(channels[1], "payload")).Throws<ArgumentException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    private static RespireClient CreateClient(int port) => RespireClient.Create(new RespireOptions
    {
        Protocol = RespProtocol.Resp2,
        Endpoints = { new RespireEndpoint("127.0.0.1", port) },
        Connections = 1,
    });

    private static byte[] Confirmation(string verb, byte[] channel)
    {
        var frame = Frame(false, Encoding.ASCII.GetBytes(verb), channel);
        // Add the subscription count as the third array element.
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
