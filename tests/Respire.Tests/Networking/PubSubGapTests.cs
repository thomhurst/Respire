using System.Text;
using Respire.Internal;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PubSubGapTests
{
    [Test]
    [Arguments(false, SubscriptionKind.Channel)]
    [Arguments(true, SubscriptionKind.Channel)]
    [Arguments(false, SubscriptionKind.Pattern)]
    [Arguments(true, SubscriptionKind.Pattern)]
    [Arguments(false, SubscriptionKind.Sharded)]
    [Arguments(true, SubscriptionKind.Sharded)]
    public async Task ReconnectAcknowledgementAndMessageInOneRead_YieldsGapFirst(bool push, SubscriptionKind kind)
    {
        var verb = kind switch { SubscriptionKind.Pattern => "psubscribe", SubscriptionKind.Sharded => "ssubscribe", _ => "subscribe" };
        var messageVerb = kind switch { SubscriptionKind.Pattern => "pmessage", SubscriptionKind.Sharded => "smessage", _ => "message" };
        var prefix = push ? ">" : "*";
        var confirmation = $"{prefix}3\r\n${verb.Length}\r\n{verb}\r\n$2\r\nch\r\n:1\r\n";
        var pattern = kind == SubscriptionKind.Pattern ? "$2\r\nch\r\n" : "";
        var data = $"{prefix}{(kind == SubscriptionKind.Pattern ? 4 : 3)}\r\n${messageVerb.Length}\r\n{messageVerb}\r\n{pattern}$2\r\nch\r\n$5\r\nhello\r\n";
        await using var server = new FakeRespServer(2, Encoding.ASCII.GetBytes(confirmation + data));
        server.SuppressReply = command => command == "SUBSCRIBE stalled";
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        await using var subscription = await client.SubscribeAsync(((RespireChannel)"ch").WithKind(kind));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Message);
        var notification = new TaskCompletionSource<RespireSubscriptionGap>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += _ => throw new InvalidOperationException("Observer failure must not break acknowledgement delivery.");
        subscription.DeliveryGap += gap => notification.TrySetResult(gap);

        using var cancel = new CancellationTokenSource();
        var stalled = client.SubscribeAsync("stalled", cancel.Token).AsTask();
        while (server.CommandsSeen < 2) await Task.Delay(10, deadline.Token);
        await cancel.CancelAsync();
        await Assert.That(async () => await stalled).Throws<OperationCanceledException>();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        var marker = reader.Current;
        await Assert.That(marker.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(marker.Gap!.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
        await Assert.That(marker.Gap.Duration >= TimeSpan.Zero).IsTrue();
        await Assert.That(marker.Gap.DroppedMessages).IsEqualTo(0);
        await Assert.That(await notification.Task.WaitAsync(deadline.Token)).IsEqualTo(marker.Gap);
        _ = Assert.Throws<InvalidOperationException>(() => marker.As<string>());
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("hello");
    }

    [Test]
    [Arguments(SubscriptionOverflow.DropOldest)]
    [Arguments(SubscriptionOverflow.DropNewest)]
    public async Task OverflowPreservesGapAndDataOrder(SubscriptionOverflow overflow)
    {
        var buffer = new SubscriptionBuffer(2, overflow);
        buffer.Write(Message("a"));
        buffer.Write(Message("b"));
        buffer.Write(Message("c"));
        buffer.Write(Message("d"));
        buffer.Complete();
        var items = new List<RespireMessage>();
        await foreach (var item in buffer.ReadAllAsync()) items.Add(item);
        await Assert.That(items.Count).IsEqualTo(3);
        var gapIndex = overflow == SubscriptionOverflow.DropOldest ? 0 : 2;
        await Assert.That(items[gapIndex].Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(items[gapIndex].Gap!.DroppedMessages).IsEqualTo(2);
        await Assert.That(items.Where(item => item.Kind == RespireMessageKind.Message).Select(item => item.Text))
            .IsEquivalentTo(overflow == SubscriptionOverflow.DropOldest ? ["c", "d"] : ["a", "b"]);
    }

    [Test]
    [Arguments(SubscriptionOverflow.DropOldest)]
    [Arguments(SubscriptionOverflow.DropNewest)]
    public async Task FullBufferRetainsReconnectMarkerAcrossRepeatedOverflow(SubscriptionOverflow overflow)
    {
        var buffer = new SubscriptionBuffer(1, overflow);
        buffer.Write(Message("before"));
        var start = DateTimeOffset.UtcNow;
        buffer.WriteGap(new(RespireSubscriptionGapReason.Reconnect, start, start.AddSeconds(1)));
        for (var i = 0; i < 100; i++) buffer.Write(Message("after"));
        buffer.Complete();
        var items = new List<RespireMessage>();
        await foreach (var item in buffer.ReadAllAsync()) items.Add(item);
        await Assert.That(items.Count).IsEqualTo(2);
        var marker = items.Single(item => item.Kind == RespireMessageKind.Gap);
        await Assert.That(marker.Gap!.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect | RespireSubscriptionGapReason.BufferOverflow);
        await Assert.That(marker.Gap.DroppedMessages).IsEqualTo(100);
        await Assert.That(marker.Gap.EndedAt).IsEqualTo(start.AddSeconds(1));
    }

    [Test]
    public async Task GapWakesEmptyReaderAndSurvivesEnumeratorReplacement()
    {
        var buffer = new SubscriptionBuffer(2, SubscriptionOverflow.DropOldest);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = buffer.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        var now = DateTimeOffset.UtcNow;
        buffer.WriteGap(new(RespireSubscriptionGapReason.Reconnect, now, now));
        await Assert.That(await pending).IsTrue();
        buffer.Write(Message("a"));
        buffer.Write(Message("b"));
        await first.DisposeAsync();
        await using var next = buffer.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        await Assert.That(await next.MoveNextAsync()).IsTrue();
        await Assert.That(next.Current.Text).IsEqualTo("a");
        await Assert.That(await next.MoveNextAsync()).IsTrue();
        await Assert.That(next.Current.Text).IsEqualTo("b");
        buffer.Complete();
        await Assert.That(await next.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task CancellationDoesNotDiscardBufferedGapOrData()
    {
        var buffer = new SubscriptionBuffer(1, SubscriptionOverflow.DropOldest);
        var now = DateTimeOffset.UtcNow;
        buffer.WriteGap(new(RespireSubscriptionGapReason.Reconnect, now, now));
        buffer.Write(Message("retained"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using (var first = buffer.ReadAllAsync(cancelled.Token).GetAsyncEnumerator())
        {
            await Assert.That(async () => await first.MoveNextAsync()).Throws<OperationCanceledException>();
        }
        buffer.Complete();
        await using var next = buffer.ReadAllAsync().GetAsyncEnumerator();
        await Assert.That(await next.MoveNextAsync()).IsTrue();
        await Assert.That(next.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await next.MoveNextAsync()).IsTrue();
        await Assert.That(next.Current.Text).IsEqualTo("retained");
        await Assert.That(await next.MoveNextAsync()).IsFalse();
    }

    private static RespireMessage Message(string text)
        => new("ch", null, Encoding.UTF8.GetBytes(text), new SystemTextJsonSerializer());
}
