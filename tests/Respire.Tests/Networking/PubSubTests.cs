using Respire.Commands;
using Respire.FastClient;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// Pub/sub wire tests against the fake server: subscribe confirmations complete commands,
/// message frames route to handlers, RESP3 push frames route on any connection with a handler.
/// </summary>
public class PubSubTests
{
    private static readonly byte[] SubscribeConfirmation =
        "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray();
    private static readonly byte[] MessageFrame =
        "*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$5\r\nhello\r\n"u8.ToArray();

    [Test]
    public async Task Subscribe_ConfirmationCompletes_MessagesRouteToHandler()
    {
        await using var server = new FakeRespServer(SubscribeConfirmation);
        await using var subscriber = await RespireSubscriber.CreateAsync("127.0.0.1", server.Port);

        var received = new TaskCompletionSource<(string Channel, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync("ch", (string channel, in RespireValue message) =>
            received.TrySetResult((channel, message.AsString())));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SUBSCRIBE ch");

        await server.SendRawAsync(MessageFrame);
        var (gotChannel, gotPayload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(gotChannel).IsEqualTo("ch");
        await Assert.That(gotPayload).IsEqualTo("hello");
    }

    [Test]
    public async Task MultipleMessages_AllDelivered_InOrder()
    {
        await using var server = new FakeRespServer(SubscribeConfirmation);
        await using var subscriber = await RespireSubscriber.CreateAsync("127.0.0.1", server.Port);

        var received = new List<string>();
        var countdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.SubscribeAsync("ch", (string _, in RespireValue message) =>
        {
            lock (received)
            {
                received.Add(message.AsString());
                if (received.Count == 3)
                {
                    countdown.TrySetResult();
                }
            }
        });

        await server.SendRawAsync(
            "*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\na\r\n*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\nb\r\n*3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$1\r\nc\r\n"u8.ToArray());
        await countdown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(received).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task PSubscribe_PatternMessagesRouteWithConcreteChannel()
    {
        await using var server = new FakeRespServer(
            "*3\r\n$10\r\npsubscribe\r\n$3\r\nch*\r\n:1\r\n"u8.ToArray());
        await using var subscriber = await RespireSubscriber.CreateAsync("127.0.0.1", server.Port);

        var received = new TaskCompletionSource<(string Channel, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await subscriber.PSubscribeAsync("ch*", (string channel, in RespireValue message) =>
            received.TrySetResult((channel, message.AsString())));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("PSUBSCRIBE ch*");

        await server.SendRawAsync("*4\r\n$8\r\npmessage\r\n$3\r\nch*\r\n$3\r\nch1\r\n$4\r\ndata\r\n"u8.ToArray());
        var (gotChannel, gotPayload) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(gotChannel).IsEqualTo("ch1");
        await Assert.That(gotPayload).IsEqualTo("data");
    }

    [Test]
    public async Task Unsubscribe_ConfirmationCompletes_HandlerRemoved()
    {
        await using var server = new FakeRespServer(
            SubscribeConfirmation,
            "*3\r\n$11\r\nunsubscribe\r\n$2\r\nch\r\n:0\r\n"u8.ToArray(),
            "*3\r\n$9\r\nsubscribe\r\n$3\r\nch2\r\n:1\r\n"u8.ToArray());
        await using var subscriber = await RespireSubscriber.CreateAsync("127.0.0.1", server.Port);

        var deliveries = 0;
        await subscriber.SubscribeAsync("ch", (string _, in RespireValue _) => Interlocked.Increment(ref deliveries));
        await subscriber.UnsubscribeAsync("ch");

        // A late message for the now-unregistered channel must be ignored without breaking
        // the connection. The follow-up subscribe's confirmation arrives after the injected
        // message on the same stream, so its completion proves the message was processed.
        await server.SendRawAsync(MessageFrame);
        await subscriber.SubscribeAsync("ch2", (string _, in RespireValue _) => { });

        await Assert.That(deliveries).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("UNSUBSCRIBE ch");
    }

    [Test]
    public async Task Resp3PushFrame_RoutesToHandler_RepliesUnaffected()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);

        var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions
            {
                PushHandler = (in RespireValue value) => pushed.TrySetResult(value.AsArray()[2].AsString()),
            });

        // Push frame injected between a command and its reply must not consume the FIFO slot.
        await server.SendRawAsync(">3\r\n$7\r\nmessage\r\n$2\r\nch\r\n$6\r\nurgent\r\n"u8.ToArray());
        await Assert.That(await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("urgent");

        var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        pong.Dispose();
    }

    [Test]
    public async Task HandlerThrowing_DoesNotKillConnection()
    {
        await using var server = new FakeRespServer(
            SubscribeConfirmation,
            "*3\r\n$9\r\nsubscribe\r\n$3\r\nch2\r\n:1\r\n"u8.ToArray());
        await using var subscriber = await RespireSubscriber.CreateAsync("127.0.0.1", server.Port);

        await subscriber.SubscribeAsync("ch", (string _, in RespireValue _) => throw new InvalidOperationException("boom"));
        await server.SendRawAsync(MessageFrame);

        // A follow-up round trip on the same stream proves the throwing handler was invoked
        // (its message precedes the confirmation) without killing the receive loop.
        await subscriber.SubscribeAsync("ch2", (string _, in RespireValue _) => { });

        await Assert.That(subscriber.IsConnected).IsTrue();
    }
}
