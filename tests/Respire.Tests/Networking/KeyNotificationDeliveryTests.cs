using System.Diagnostics.Metrics;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class KeyNotificationDeliveryTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DescriptorRecoveryPreservesKindAndReportsGapBeforeNotification(bool resp3, bool pattern)
    {
        var descriptor = pattern ? RespireChannel.KeySpacePrefix("tenant:", 0)
            : RespireChannel.KeySpaceSingleKey("tenant:key", 0);
        var frame = Confirmation(descriptor, resp3, false).Concat(Data(descriptor, resp3, "set")).ToArray();
        byte[][] replies = resp3 ? ["%1\r\n+proto\r\n:3\r\n"u8.ToArray(), frame] : [frame];
        await using var server = new FakeRespServer(2, replies);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "SUBSCRIBE stalled") return false;
            stalled.TrySetResult();
            return true;
        };
        server.ReplyOverride = (_, command) => command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal)
            || command.StartsWith("PUNSUBSCRIBE ", StringComparison.Ordinal) ? Confirmation(descriptor, resp3, true) : null;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var subscription = await client.SubscribeAsync(descriptor);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.TryParseKeyNotification(out var first)).IsTrue();
        await Assert.That(first.Key).IsEqualTo(new RespireKey("tenant:key"));
        using var cancellation = new CancellationTokenSource();
        var pending = client.SubscribeAsync("stalled", cancellation.Token).AsTask();
        await stalled.Task.WaitAsync(deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(reader.Current.TryParseKeyNotification(out _)).IsFalse();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.TryParseKeyNotification(out var recovered)).IsTrue();
        await Assert.That(recovered.Type).IsEqualTo(RespireKeyNotificationType.Set);
        await Assert.That(subscription.Targets[0].RoutingScope).IsEqualTo(descriptor.RoutingScope);
        var commandName = pattern ? "PSUBSCRIBE" : "SUBSCRIBE";
        await Assert.That(server.ReceivedCommands.Count(command => command == $"{commandName} {descriptor}"))
            .IsEqualTo(2);
    }

    [Test]
    [Arguments(SubscriptionOverflow.DropNewest)]
    [Arguments(SubscriptionOverflow.DropOldest)]
    public async Task NotificationOverflowRetainsDeliveryGapAndDropTelemetry(SubscriptionOverflow overflow)
    {
        var descriptor = RespireChannel.KeySpaceSingleKey("tenant:key", 0);
        await using var server = new FakeRespServer(Confirmation(descriptor, false, false));
        server.ReplyOverride = (_, command) => command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal)
            ? Confirmation(descriptor, false, true) : null;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        long measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.pubsub.messages.dropped")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref measurements, value));
        listener.Start();
        await using var subscription = await client.SubscribeAsync(descriptor,
            new RespireSubscriptionOptions(BufferSize: 1, Overflow: overflow), default);
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += _ => { if (subscription.DroppedMessages == 2) dropped.TrySetResult(); };
        await server.SendRawAsync(Data(descriptor, false, "set").Concat(Data(descriptor, false, "del"))
            .Concat(Data(descriptor, false, "expire")).ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dropped.Task.WaitAsync(deadline.Token);
        await Assert.That(subscription.DroppedMessages).IsEqualTo(2L);
        await Assert.That(Interlocked.Read(ref measurements) >= 2).IsTrue();
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        var found = false;
        var gap = false;
        for (var index = 0; index < 2; index++)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            if (reader.Current.TryParseKeyNotification(out var notification))
            {
                found = true;
                await Assert.That(notification.Type).IsEqualTo(overflow == SubscriptionOverflow.DropOldest
                    ? RespireKeyNotificationType.Expire : RespireKeyNotificationType.Set);
            }
            else
            {
                gap = true;
                await Assert.That(reader.Current.Gap!.DroppedMessages).IsEqualTo(2L);
            }
        }
        await Assert.That(found && gap).IsTrue();
    }

    private static byte[] Confirmation(RespireChannel descriptor, bool push, bool unsubscribe)
    {
        var verb = descriptor.Kind == SubscriptionKind.Pattern ? "psubscribe" : "subscribe";
        if (unsubscribe) verb = descriptor.Kind == SubscriptionKind.Pattern ? "punsubscribe" : "unsubscribe";
        return Encoding.ASCII.GetBytes($"{(push ? '>' : '*')}3\r\n{Bulk(verb)}{Bulk(descriptor.ToString())}:{(unsubscribe ? 0 : 1)}\r\n");
    }
    private static byte[] Data(RespireChannel descriptor, bool push, string payload)
    {
        var pattern = descriptor.Kind == SubscriptionKind.Pattern;
        return Encoding.ASCII.GetBytes($"{(push ? '>' : '*')}{(pattern ? 4 : 3)}\r\n"
            + Bulk(pattern ? "pmessage" : "message") + (pattern ? Bulk(descriptor.ToString()) : "")
            + Bulk("__keyspace@0__:tenant:key") + Bulk(payload));
    }
    private static string Bulk(string value) => $"${value.Length}\r\n{value}\r\n";
}
