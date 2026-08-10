using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// Pub/sub wire tests against the fake server: subscriptions are async streams, subscribe
/// confirmations complete activation, message frames route to the right subscription, and
/// RESP3 push frames route on any connection with a push handler.
/// </summary>
/// <remarks>
/// The fake server accepts one connection, so these tests use a lazy client
/// (<see cref="RespireClient.Create(RespireOptions)"/>): only the pub/sub hub connects.
/// </remarks>
public class PubSubTests
{
    private static readonly byte[] SubscribeConfirmation =
        "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray();
    private static readonly byte[] MessageFrame =
        "*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$5\r\nhello\r\n"u8.ToArray();

    private static RespireClient CreateLazyClient(int port)
        => RespireClient.Create(new RespireOptions { Endpoints = { new RespireEndpoint("127.0.0.1", port) } });

    [Test]
    public async Task MalformedLaterName_DoesNotRegisterEarlierRoute()
    {
        await using var server = new FakeRespServer(SubscribeConfirmation);
        await using var client = CreateLazyClient(server.Port);

        // Names are validated before any route is registered or any byte reaches the wire.
        await Assert.That(async () => await client.SubscribeAsync(["valid", "\uD800"]))
            .Throws<ArgumentException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeAsync_ReturnsAfterConfirmation_ThenMessagesFlowToEnumerator()
    {
        await using var server = new FakeRespServer(SubscribeConfirmation);
        await using var client = CreateLazyClient(server.Port);

        var channel = new string("ch".AsSpan());
        await using var subscription = await client.SubscribeAsync(channel);

        // Returning already implies the confirmation arrived — no waiting for the command to show up.
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SUBSCRIBE ch"]);

        var enumerator = subscription.GetAsyncEnumerator();
        var moveTask = enumerator.MoveNextAsync();
        await server.SendRawAsync(MessageFrame);

        await Assert.That(await moveTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(enumerator.Current.Channel).IsEqualTo("ch");
        await Assert.That(ReferenceEquals(enumerator.Current.Channel, channel)).IsTrue();
        await Assert.That(enumerator.Current.Text).IsEqualTo("hello");

        // Enumeration streams the buffer; it never resubscribes.
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await enumerator.DisposeAsync();
    }

    [Test]
    public async Task SubscribeAsync_Cancelled_UnsubscribesInsteadOfLeaking()
    {
        await using var server = new FakeRespServer(
            "*3\r\n$9\r\nsubscribe\r\n$4\r\nwarm\r\n:1\r\n"u8.ToArray(),
            SubscribeConfirmation,
            "*3\r\n$11\r\nunsubscribe\r\n$2\r\nch\r\n:0\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server.Port);

        // A live subscription first, so cancellation races the SUBSCRIBE rather than the connect.
        await using var warm = await client.SubscribeAsync("warm");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await client.SubscribeAsync("ch", cts.Token))
            .Throws<OperationCanceledException>();

        // Cleanup is awaited before the cancellation surfaces, so the channel cannot be left
        // subscribed server-side even though the SUBSCRIBE had already gone out.
        await Assert.That(server.ReceivedCommands).Contains("UNSUBSCRIBE ch");
    }

    [Test]
    public async Task MultipleMessages_AllDelivered_InOrder()
    {
        await using var server = new FakeRespServer(SubscribeConfirmation);
        await using var client = CreateLazyClient(server.Port);

        await using var subscription = await client.SubscribeAsync("ch");
        var received = new List<string>();
        var collector = Task.Run(async () =>
        {
            await foreach (var message in subscription)
            {
                lock (received)
                {
                    received.Add(message.Text);
                }

                if (received.Count == 3)
                {
                    break;
                }
            }
        });

        await server.SendRawAsync(
            "*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\na\r\n*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\nb\r\n*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\nc\r\n"u8.ToArray());
        await collector.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(received).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task SubscribePattern_MessagesCarryConcreteChannelAndPattern()
    {
        await using var server = new FakeRespServer(
            "*3\r\n$10\r\npsubscribe\r\n$3\r\nch*\r\n:1\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server.Port);

        await using var subscription = await client.SubscribePatternAsync("ch*");
        var enumerator = subscription.GetAsyncEnumerator();
        var moveTask = enumerator.MoveNextAsync();

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("PSUBSCRIBE ch*");

        await server.SendRawAsync("*4\r\n$8\r\npmessage\r\n$3\r\nch*\r\n$3\r\nch1\r\n$4\r\ndata\r\n"u8.ToArray());

        await Assert.That(await moveTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(enumerator.Current.Channel).IsEqualTo("ch1");
        await Assert.That(enumerator.Current.Pattern).IsEqualTo("ch*");
        await Assert.That(enumerator.Current.Text).IsEqualTo("data");
        await enumerator.DisposeAsync();
    }

    [Test]
    public async Task DisposingSubscription_Unsubscribes_AndEndsEnumeration()
    {
        await using var server = new FakeRespServer(
            SubscribeConfirmation,
            "*3\r\n$11\r\nunsubscribe\r\n$2\r\nch\r\n:0\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server.Port);

        var subscription = await client.SubscribeAsync("ch");
        var enumerator = subscription.GetAsyncEnumerator();
        var moveTask = enumerator.MoveNextAsync();

        await subscription.DisposeAsync();

        // The stream ends (no message ever arrived) and UNSUBSCRIBE went to the server.
        await Assert.That(await moveTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("UNSUBSCRIBE ch");
        await enumerator.DisposeAsync();
    }

    [Test]
    public async Task MessageForUnsubscribedChannel_IsIgnoredWithoutBreakingConnection()
    {
        await using var server = new FakeRespServer(
            SubscribeConfirmation,
            "*3\r\n$9\r\nsubscribe\r\n$3\r\nch2\r\n:1\r\n"u8.ToArray());
        await using var client = CreateLazyClient(server.Port);

        var first = await client.SubscribeAsync("ch");
        var firstEnumerator = first.GetAsyncEnumerator();
        var firstMove = firstEnumerator.MoveNextAsync();

        // A message for a channel nobody routes must be dropped silently.
        await server.SendRawAsync("*3\r\n$7\r\nmessage\r\n$5\r\nother\r\n$1\r\nx\r\n"u8.ToArray());

        // A follow-up subscription round trip on the same stream proves the frame was consumed
        // without killing the receive loop.
        var second = await client.SubscribeAsync("ch2");
        var secondEnumerator = second.GetAsyncEnumerator();
        var secondMove = secondEnumerator.MoveNextAsync();
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("SUBSCRIBE ch2");

        // Disposing the subscriptions completes their buffers, which lets the pending
        // MoveNextAsync calls finish false — only then may the enumerators be disposed.
        await second.DisposeAsync();
        await Assert.That(await secondMove.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await secondEnumerator.DisposeAsync();

        await first.DisposeAsync();
        await Assert.That(await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).IsFalse();
        await firstEnumerator.DisposeAsync();
    }

    [Test]
    public async Task Resp3PushFrame_RoutesToHandler_RepliesUnaffected()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);

        var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions
            {
                PushHandler = (in RespValue value) => pushed.TrySetResult(value.AsArray()[2].AsString()),
            });

        // Push frame injected between a command and its reply must not consume the FIFO slot.
        await server.SendRawAsync(">3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$6\r\nurgent\r\n"u8.ToArray());
        await Assert.That(await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("urgent");

        var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        pong.Dispose();
    }
}
