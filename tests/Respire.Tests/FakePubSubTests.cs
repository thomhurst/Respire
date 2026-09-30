using System.Buffers;
using System.Text;
using Respire.Protocol;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakePubSubTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinarySubscriptionsCountConnectionsAndOwnPublicationBytes(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = (RespProtocol)protocol, Connections = 1 };
        await using var first = await RespireClient.ConnectAsync(options);
        await using var second = await RespireClient.ConnectAsync(options);
        byte[] channel = [0, 255, 128];
        byte[] payload = [254, 0, 127];
        await using var one = await first.SubscribeAsync((RespireChannel)channel);
        await using var duplicate = await first.SubscribeAsync((RespireChannel)channel);
        await using var two = await second.SubscribeAsync((RespireChannel)channel);
        await Assert.That(await first.PublishAsync((RespireChannel)channel, payload)).IsEqualTo(2);
        payload[0] = 0;
        channel[0] = 1;
        foreach (var subscription in new[] { one, duplicate, two })
        {
            await using var reader = subscription.GetAsyncEnumerator();
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
            await Assert.That(reader.Current.Payload.Span.SequenceEqual(new byte[] { 254, 0, 127 })).IsTrue();
            await Assert.That(reader.Current.Channel).IsEqualTo((RespireChannel)new byte[] { 0, 255, 128 });
        }
        await one.DisposeAsync();
        await Assert.That(await first.PublishAsync((RespireChannel)new byte[] { 0, 255, 128 }, "still subscribed")).IsEqualTo(2);
        await duplicate.DisposeAsync();
        await Assert.That(await first.PublishAsync((RespireChannel)new byte[] { 0, 255, 128 }, "one connection")).IsEqualTo(1);
        await two.DisposeAsync();
        await Assert.That(await first.PublishAsync((RespireChannel)new byte[] { 0, 255, 128 }, "none")).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WireConfirmationsAndSubscribedModeMatchTheProtocol(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var wire = await WireClient.ConnectAsync(server, protocol);
        await wire.SendAsync("SUBSCRIBE", "first", "second", "first");
        await Confirmation(wire, "subscribe", "first", 1, protocol);
        await Confirmation(wire, "subscribe", "second", 2, protocol);
        await Confirmation(wire, "subscribe", "first", 2, protocol);
        await wire.SendAsync("PING");
        using (var pong = await wire.ReadAsync())
        {
            if (protocol == 2)
            {
                await Assert.That(pong.AsArray()[0].AsString()).IsEqualTo("pong");
                await Assert.That(pong.AsArray()[1].AsString()).IsEqualTo("");
            }
            else await Assert.That(pong.AsString()).IsEqualTo("PONG");
        }
        await wire.SendAsync("GET", "missing");
        using (var get = await wire.ReadAsync())
        {
            if (protocol == 2)
            {
                await Assert.That(get.IsError).IsTrue();
                await Assert.That(get.GetErrorMessage()).IsEqualTo("ERR Can't execute 'get': only SUBSCRIBE / UNSUBSCRIBE / PING are supported in this context");
            }
            else await Assert.That(get.IsNull).IsTrue();
        }
        await wire.SendAsync("PUBLISH", "first", "self");
        using (var reply = await wire.ReadAsync())
        {
            if (protocol == 2) await Assert.That(reply.IsError).IsTrue();
            else
            {
                await Assert.That(reply.Type).IsEqualTo(RespDataType.Push);
                await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo("message");
                await Assert.That(reply.AsArray()[2].AsString()).IsEqualTo("self");
            }
        }
        if (protocol == 3)
        {
            using var count = await wire.ReadAsync();
            await Assert.That(count.AsInteger()).IsEqualTo(1);
        }
        await wire.SendAsync("UNSUBSCRIBE", "missing", "first", "first");
        await Confirmation(wire, "unsubscribe", "missing", 2, protocol);
        await Confirmation(wire, "unsubscribe", "first", 1, protocol);
        await Confirmation(wire, "unsubscribe", "first", 1, protocol);
        await wire.SendAsync("UNSUBSCRIBE");
        await Confirmation(wire, "unsubscribe", "second", 0, protocol);
        await wire.SendAsync("UNSUBSCRIBE");
        await Confirmation(wire, "unsubscribe", null, 0, protocol);
        await wire.SendAsync("PING");
        using var ordinaryPong = await wire.ReadAsync();
        await Assert.That(ordinaryPong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    [Arguments(2, "SUBSCRIBE")]
    [Arguments(3, "SUBSCRIBE")]
    [Arguments(2, "PING")]
    [Arguments(3, "PING")]
    public async Task FaultHeldReplyPrecedesLaterPublications(int protocol, string command)
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var wire = await WireClient.ConnectAsync(server, protocol);
        if (command == "PING")
        {
            await wire.SendAsync("SUBSCRIBE", "channel");
            await Confirmation(wire, "subscribe", "channel", 1, protocol);
        }
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault(command, RespireFakeFault.Pause(gate, afterExecution: true));
        await wire.SendAsync(command == "SUBSCRIBE" ? [command, "channel"] : [command]);
        await fault.Matched.WaitAsync(Limit);
        for (var index = 0; index < 3; index++)
            await Assert.That(await publisher.PublishAsync("channel", index)).IsEqualTo(1);
        gate.Release();
        using (var reply = await wire.ReadAsync())
        {
            if (command == "SUBSCRIBE") await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo("subscribe");
            else if (protocol == 2) await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo("pong");
            else await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        for (var index = 0; index < 3; index++)
        {
            using var message = await wire.ReadAsync();
            await Assert.That(message.AsArray()[0].AsString()).IsEqualTo("message");
            await Assert.That(message.AsArray()[2].AsString()).IsEqualTo(index.ToString());
        }
        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CancelledActivationAndDisposalRemoveConnectionRoutes(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var subscriber = RespireClient.Create(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("SUBSCRIBE", RespireFakeFault.Pause(gate, afterExecution: true));
        using var caller = new CancellationTokenSource();
        var subscribing = subscriber.SubscribeAsync("channel", caller.Token).AsTask();
        await fault.Matched.WaitAsync(Limit);
        await Assert.That(await publisher.PublishAsync("channel", "before cancellation")).IsEqualTo(1);
        caller.Cancel();
        var error = await Assert.That(async () => await subscribing.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await subscriber.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(await publisher.PublishAsync("channel", "after disposal")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublicationDisconnectRespectsAcceptanceWithoutReplay(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var subscriber = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var subscription = await subscriber.SubscribeAsync("channel");
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("PUBLISH", RespireFakeFault.Disconnect(afterExecution));
        await Assert.That(async () => await publisher.PublishAsync("channel", "accepted").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
        await subscriber.PublishAsync("channel", "marker");
        await using var reader = subscription.GetAsyncEnumerator();
        if (afterExecution)
        {
            await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
            await Assert.That(reader.Current.Text).IsEqualTo("accepted");
        }
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("marker");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ReusedRequestBuffersCannotChangeRoutesOrHeldMessages(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var subscriber = await WireClient.ConnectAsync(server, protocol);
        await using var publisher = await WireClient.ConnectAsync(server, protocol);
        byte[] channel = [0, 255, 128];
        byte[] payload = [254, 0, 127];
        await subscriber.SendBytesAsync("SUBSCRIBE"u8.ToArray(), channel);
        using (var acknowledgement = await subscriber.ReadAsync())
            await Assert.That(acknowledgement.AsArray()[1].AsSpan().SequenceEqual(channel)).IsTrue();
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("PING", RespireFakeFault.Pause(gate, afterExecution: true));
        // Overwrite the subscription request buffer, then hold its reply ahead of the push.
        await subscriber.SendAsync("PING", new string('s', 8192));
        await fault.Matched.WaitAsync(Limit);
        await publisher.SendBytesAsync("PUBLISH"u8.ToArray(), channel, payload);
        using (var receivers = await publisher.ReadAsync())
            await Assert.That(receivers.AsInteger()).IsEqualTo(1);
        channel[0] = 1;
        payload[0] = 1;
        await publisher.SendAsync("ECHO", new string('p', 8192));
        using (var overwritten = await publisher.ReadAsync())
            await Assert.That(overwritten.AsString().Length).IsEqualTo(8192);
        gate.Release();
        using var pong = await subscriber.ReadAsync();
        using var message = await subscriber.ReadAsync();
        await Assert.That(message.AsArray()[1].AsSpan().SequenceEqual(new byte[] { 0, 255, 128 })).IsTrue();
        await Assert.That(message.AsArray()[2].AsSpan().SequenceEqual(new byte[] { 254, 0, 127 })).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SubscriberDisconnectResubscribesWithoutReplayingTheGap(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var subscriber = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await using var subscription = await subscriber.SubscribeAsync("original");
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Connected) connected.TrySetResult();
        };
        var gate = new RespireFakeGate();
        using var disconnect = server.InjectFault("SUBSCRIBE", RespireFakeFault.Disconnect(), firstArgument: "trigger"u8.ToArray());
        using var restore = server.InjectFault("SUBSCRIBE", RespireFakeFault.Pause(gate), firstArgument: "original"u8.ToArray());
        await Assert.That(async () => await subscriber.SubscribeAsync("trigger").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await restore.Matched.WaitAsync(Limit);
        await Assert.That(await publisher.PublishAsync("original", "lost during gap")).IsEqualTo(0);
        gate.Release();
        await connected.Task.WaitAsync(Limit);
        await Assert.That(await publisher.PublishAsync("original", "after reconnect")).IsEqualTo(1);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(reader.Current.Gap!.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("after reconnect");
    }

    [Test]
    public async Task ServerDisposalJoinsFaultHeldRepliesAndQueuedPublications()
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var subscriber = await WireClient.ConnectAsync(server, 3);
        using var fault = server.InjectFault("SUBSCRIBE", RespireFakeFault.Pause(new RespireFakeGate(), afterExecution: true));
        await subscriber.SendAsync("SUBSCRIBE", "held");
        await fault.Matched.WaitAsync(Limit);
        await Assert.That(await publisher.PublishAsync("held", "queued")).IsEqualTo(1);
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask()).WaitAsync(Limit);
        await Assert.That(async () => { using var ignored = await subscriber.ReadAsync(); }).Throws<EndOfStreamException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConcurrentPublishersKeepEachPublishersMessagesOrdered(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var subscriber = await WireClient.ConnectAsync(server, protocol);
        await subscriber.SendAsync("SUBSCRIBE", "shared");
        await Confirmation(subscriber, "subscribe", "shared", 1, protocol);
        var publishers = Enumerable.Range(0, 4).Select(async publisher =>
        {
            await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
            for (var index = 0; index < 32; index++)
                await Assert.That(await client.PublishAsync("shared", $"{publisher}:{index}")).IsEqualTo(1);
        }).ToArray();
        var received = new int[4];
        for (var index = 0; index < 128; index++)
        {
            using var message = await subscriber.ReadAsync();
            await Assert.That(message.Type).IsEqualTo(protocol == 3 ? RespDataType.Push : RespDataType.Array);
            await Assert.That(message.AsArray()[0].AsString()).IsEqualTo("message");
            await Assert.That(message.AsArray()[1].AsString()).IsEqualTo("shared");
            var parts = message.AsArray()[2].AsString().Split(':');
            var publisher = int.Parse(parts[0]);
            await Assert.That(int.Parse(parts[1])).IsEqualTo(received[publisher]++);
        }
        await Task.WhenAll(publishers).WaitAsync(Limit);
        await Assert.That(received.All(count => count == 32)).IsTrue();
    }

    [Test]
    public async Task SlowSubscriberHasBoundedOutputAndDoesNotBlockOtherClients()
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var slow = await WireClient.ConnectAsync(server, 2);
        await slow.SendAsync("SUBSCRIBE", "slow");
        await Confirmation(slow, "subscribe", "slow", 1, 2);
        var payload = new byte[256 * 1024];
        // 63 encoded messages fit; the 64th exceeds 16 MiB once frame bytes are included.
        // Even the publication that disconnects this receiver counts its active route.
        for (var index = 0; index < 64; index++)
            await Assert.That(await publisher.PublishAsync("slow", payload).AsTask().WaitAsync(Limit)).IsEqualTo(1);
        await Assert.That(await publisher.PublishAsync("slow", payload).AsTask().WaitAsync(Limit)).IsEqualTo(0);
        await Assert.That(await publisher.SetAsync("still responsive", "yes")).IsTrue();
        await server.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Test]
    public async Task DrainedPublicationsReleaseOutputCapacity()
    {
        await using var server = new RespireFakeServer();
        await using var publisher = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var subscriber = await WireClient.ConnectAsync(server, 3);
        await subscriber.SendAsync("SUBSCRIBE", "drained");
        await Confirmation(subscriber, "subscribe", "drained", 1, 3);
        var payload = new byte[256 * 1024];
        // Total traffic exceeds the output limit, but only one publication is awaited at a time.
        for (var index = 0; index < 80; index++)
        {
            await Assert.That(await publisher.PublishAsync("drained", payload).AsTask().WaitAsync(Limit)).IsEqualTo(1);
            using var message = await subscriber.ReadAsync();
            await Assert.That(message.Type).IsEqualTo(RespDataType.Push);
            await Assert.That(message.AsArray()[2].AsSpan().Length).IsEqualTo(payload.Length);
        }
    }

    [Test]
    public async Task UnsupportedPubSubVariantsLeaveFollowingRepliesInOrder()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
        foreach (var command in new[] { "PSUBSCRIBE", "PUNSUBSCRIBE", "SSUBSCRIBE", "SUNSUBSCRIBE", "SPUBLISH" })
        {
            var error = await Assert.That(async () => { using var ignored = await client.ExecuteAsync(command, "channel", "value"); })
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains(command);
            await Assert.That(await client.PublishAsync("channel", "no routes")).IsEqualTo(0);
        }
    }

    private static async Task Confirmation(WireClient wire, string kind, string? channel, long count, int protocol)
    {
        using var reply = await wire.ReadAsync();
        await Assert.That(reply.Type).IsEqualTo(protocol == 3 ? RespDataType.Push : RespDataType.Array);
        await Assert.That(reply.AsArray()[0].AsString()).IsEqualTo(kind);
        if (channel is null) await Assert.That(reply.AsArray()[1].IsNull).IsTrue();
        else await Assert.That(reply.AsArray()[1].AsString()).IsEqualTo(channel);
        await Assert.That(reply.AsArray()[2].AsInteger()).IsEqualTo(count);
    }

    private sealed class WireClient(Stream stream) : IAsyncDisposable
    {
        private byte[] _buffer = new byte[4096];
        private int _length;

        internal static async Task<WireClient> ConnectAsync(RespireFakeServer server, int protocol)
        {
            var options = server.CreateOptions();
            var wire = new WireClient(await options.TestingStreamFactory!(options.Endpoints[0].Host, 6379, default));
            if (protocol == 3)
            {
                await wire.SendAsync("HELLO", "3");
                using var hello = await wire.ReadAsync();
                await Assert.That(hello.Type).IsEqualTo(RespDataType.Map);
            }
            return wire;
        }

        internal Task SendAsync(params string[] arguments)
            => SendBytesAsync(arguments.Select(Encoding.UTF8.GetBytes).ToArray());

        internal async Task SendBytesAsync(params byte[][] arguments)
        {
            var writer = new ArrayBufferWriter<byte>();
            writer.Write(Encoding.ASCII.GetBytes($"*{arguments.Length}\r\n"));
            foreach (var bytes in arguments)
            {
                writer.Write(Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"));
                writer.Write(bytes);
                writer.Write("\r\n"u8);
            }
            await stream.WriteAsync(writer.WrittenMemory).AsTask().WaitAsync(Limit);
        }

        internal async Task<RespValue> ReadAsync()
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (true)
            {
                var consumed = 0;
                var status = RespParser.TryParseValue(_buffer.AsSpan(0, _length), ref consumed, out var reply);
                if (status == RespParseStatus.Done)
                {
                    // Keep parsed payload storage intact while the caller owns the reply.
                    var remaining = new byte[Math.Max(4096, _length - consumed)];
                    _buffer.AsSpan(consumed, _length - consumed).CopyTo(remaining);
                    _length -= consumed;
                    _buffer = remaining;
                    return reply;
                }
                if (status != RespParseStatus.NeedMoreData) throw new IOException("Invalid fake pub/sub reply.");
                if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
                var read = await stream.ReadAsync(_buffer.AsMemory(_length), deadline.Token);
                if (read == 0) throw new EndOfStreamException();
                _length += read;
            }
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
