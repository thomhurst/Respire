using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class MessageMetricsTests
{
    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 3)]
    [Arguments(true, 0)]
    [Arguments(true, 3)]
    public async Task ConfirmedPublicationCountsOneMessageNotSubscribers(bool sharded, int subscribers)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.PubSub });
        await using var server = new FakeRespServer(System.Text.Encoding.ASCII.GetBytes($":{subscribers}\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        var result = sharded
            ? await client.PublishShardedAsync("private-channel", "secret-payload")
            : await client.PublishAsync("private-channel", "secret-payload");
        await Assert.That(result).IsEqualTo(subscribers);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That(items[0].Value).IsEqualTo(1d);
        await Assert.That(items[0].Unit).IsEqualTo("{message}");
        await Assert.That(items[0].Tags["redis.client.pubsub.message.direction"]).IsEqualTo("out");
        await Assert.That(items[0].Tags["redis.client.pubsub.sharded"]).IsEqualTo(sharded);
        await Assert.That(items[0].Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(items[0].Tags.ContainsKey("redis.client.library")).IsTrue();
        await Assert.That(items[0].Tags.Count).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RawAndDiscardedRepliesCountOnlyConfirmedPublications(bool fireAndForget)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.PubSub });
        await using var server = new FakeRespServer(":5\r\n"u8.ToArray(), "-ERR rejected\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        if (fireAndForget)
        {
            await client.ExecuteFireAndForgetAsync("publish", "channel", "first");
            await client.ExecuteFireAndForgetAsync("SPUBLISH", "channel", "rejected");
            // A following successful command proves both discarded replies were drained.
            using var drained = await client.ExecuteAsync("PING");
        }
        else
        {
            using var published = await client.ExecuteAsync("publish", "channel", "first");
            await Assert.That(async () => { using var rejected = await client.ExecuteAsync("SPUBLISH", "channel", "rejected"); })
                .Throws<RespireServerException>();
        }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.pubsub.sharded"]!).IsFalse();
    }

    [Test]
    public async Task CanceledPublicationCanStillBeConfirmedByItsLaterReply()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.PubSub });
        await using var server = new FakeRespServer { SuppressReply = command => command.StartsWith("PUBLISH ", StringComparison.Ordinal) };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var publication = client.PublishAsync("channel", "payload", cancellation.Token).AsTask();
        while (server.CommandsSeen == 0) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await publication).Throws<OperationCanceledException>();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        await server.SendRawAsync(":0\r\n"u8.ToArray());
        await capture.WaitForCountAsync(1);
        await Assert.That(capture.Items.Single().Value).IsEqualTo(1d);
    }

    [Test]
    [Arguments(SubscriptionKind.Channel, SubscriptionOverflow.DropOldest)]
    [Arguments(SubscriptionKind.Pattern, SubscriptionOverflow.DropOldest)]
    [Arguments(SubscriptionKind.Sharded, SubscriptionOverflow.DropOldest)]
    [Arguments(SubscriptionKind.Channel, SubscriptionOverflow.DropNewest)]
    [Arguments(SubscriptionKind.Pattern, SubscriptionOverflow.DropNewest)]
    [Arguments(SubscriptionKind.Sharded, SubscriptionOverflow.DropNewest)]
    public async Task ReceivedFramesCountOnceAcrossFanOutAndDrops(SubscriptionKind kind, SubscriptionOverflow overflow)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.PubSub });
        var (confirmation, message) = Frames(kind);
        await using var server = new FakeRespServer(confirmation);
        await using var client = LazyClient(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var channel = ((RespireChannel)"ch").WithKind(kind);
        var options = new RespireSubscriptionOptions(BufferSize: 1, Overflow: overflow);
        await using var first = await client.SubscribeAsync(channel, options, CancellationToken.None);
        await using var second = await client.SubscribeAsync(channel, options, CancellationToken.None);
        await server.SendRawAsync([.. message, .. message, .. message]);
        await capture.WaitForCountAsync(3);
        await Assert.That(capture.Items.Count).IsEqualTo(3);
        await Assert.That(capture.Items.All(item => item.Value == 1 && (string)item.Tags["redis.client.pubsub.message.direction"]! == "in"
            && (bool)item.Tags["redis.client.pubsub.sharded"]! == (kind == SubscriptionKind.Sharded) && item.Tags.Count == 4)).IsTrue();
        // Drop callbacks follow the message metric; wait for their deterministic count boundary.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (first.DroppedMessages != 2 || second.DroppedMessages != 2) await Task.Delay(1, deadline.Token);
        await using var reader = first.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        // Both local subscriptions send a control command, but neither acknowledgement counts.
        await Assert.That(server.ReceivedCommands.Count(command => command.EndsWith("SUBSCRIBE ch", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    [Arguments(SubscriptionKind.Channel)]
    [Arguments(SubscriptionKind.Pattern)]
    [Arguments(SubscriptionKind.Sharded)]
    public async Task ReconnectMarkersDoNotCountAsMessages(SubscriptionKind kind)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.PubSub });
        var (confirmation, message) = Frames(kind);
        await using var server = new FakeRespServer(2, [.. confirmation, .. message]);
        server.SuppressReply = command => command == "SUBSCRIBE stalled";
        await using var client = LazyClient(server.Port);
        using var capture = new Capture();
        await using var subscription = await client.SubscribeAsync(((RespireChannel)"ch").WithKind(kind));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        using var cancellation = new CancellationTokenSource();
        var stalled = client.SubscribeAsync("stalled", cancellation.Token).AsTask();
        while (server.CommandsSeen < 2) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        await Assert.That(async () => await stalled).Throws<OperationCanceledException>();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Message);
        await capture.WaitForCountAsync(2);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments(RespireMetricGroups.None, 0, 0)]
    [Arguments(RespireMetricGroups.Default, 0, 0)]
    [Arguments(RespireMetricGroups.PubSub, 1, 0)]
    [Arguments(RespireMetricGroups.Streaming, 0, 1)]
    [Arguments(RespireMetricGroups.PubSub | RespireMetricGroups.Streaming, 1, 1)]
    public async Task GroupsAreIndependentOfCommandFilters(RespireMetricGroups groups, int publications, int lags)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = groups, CommandBlockList = ["PUBLISH"] });
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        await client.PublishAsync("channel", "payload");
        new RespireStreamEntry("1000-0", []).RecordProcessingStart();
        await Assert.That(capture.Items.Count(item => item.Name == "redis.client.pubsub.messages")).IsEqualTo(publications);
        await Assert.That(capture.Items.Count(item => item.Name == "redis.client.stream.lag")).IsEqualTo(lags);
    }

    [Test]
    [Arguments("1000-0", 2000L, 1d)]
    [Arguments("1000-18446744073709551615", 1000L, 0d)]
    [Arguments("1000-0", 999L, -1d)]
    [Arguments("18446744073709551615-0", 2000L, -1d)]
    [Arguments("1000-18446744073709551616", 2000L, -1d)]
    [Arguments("1000", 2000L, -1d)]
    [Arguments("$", 2000L, -1d)]
    [Arguments("-1-0", 2000L, -1d)]
    [Arguments("1000-", 2000L, -1d)]
    [Arguments("+1000-0", 2000L, -1d)]
    public async Task StreamLagUsesTimestampAtExplicitProcessingStart(string id, long now, double expected)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Streaming });
        using var capture = new Capture();
        RespireTelemetry.RecordStreamProcessingStart(new(id), new FixedClock(now));
        await Assert.That(capture.Items.Count).IsEqualTo(expected < 0 ? 0 : 1);
        if (expected < 0) return;
        var item = capture.Items.Single();
        await Assert.That(item.Value).IsEqualTo(expected);
        await Assert.That(item.Unit).IsEqualTo("s");
        await Assert.That(item.Tags.Count).IsEqualTo(2);
        await Assert.That(item.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(item.Tags.ContainsKey("redis.client.library")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisabledGroupsOrListenersAvoidClockAndAllocations(bool listenerEnabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = listenerEnabled ? RespireMetricGroups.None : RespireMetricGroups.PubSub | RespireMetricGroups.Streaming });
        using var capture = listenerEnabled ? new Capture() : null;
        var clock = new FixedClock(2000);
        for (var i = 0; i < 5; i++) { Measure(clock, false); Measure(clock, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (Bytes: Measure(clock, false), Control: Measure(clock, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(clock.Reads).IsEqualTo(0);
    }

    [Test]
    public async Task ReadingAndAcknowledgingDoNotInventProcessingStarts()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Streaming });
        await using var server = new FakeRespServer(
            "*1\r\n*2\r\n$6\r\nevents\r\n*1\r\n*2\r\n$6\r\n1000-0\r\n*2\r\n$4\r\ntype\r\n$3\r\njob\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        var entries = await client.Streams.ReadGroupOnceAsync("events", "workers", "consumer");
        await Assert.That(entries.Length).IsEqualTo(1);
        await Assert.That(await entries[0].AckAsync()).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
        entries[0].RecordProcessingStart();
        entries[0].RecordProcessingStart();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.All(item => item.Name == "redis.client.stream.lag" && item.Value >= 0)).IsTrue();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(FixedClock clock, bool control)
    {
        var response = RespValue.Integer(3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            RespireTelemetry.RecordPublication("PUBLISH", in response);
            RespireTelemetry.RecordReceivedMessage(false);
            RespireTelemetry.RecordStreamProcessingStart(new("1000-0"), clock);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed class FixedClock(long milliseconds) : TimeProvider
    {
        internal int Reads;
        public override DateTimeOffset GetUtcNow() { Reads++; return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
    }

    private static RespireClient LazyClient(int port) => RespireClient.Create(new RespireOptions
        { Protocol = RespProtocol.Resp2, Endpoints = { new RespireEndpoint("127.0.0.1", port) } });

    private static (byte[] Confirmation, byte[] Message) Frames(SubscriptionKind kind)
    {
        var verb = kind switch { SubscriptionKind.Pattern => "psubscribe", SubscriptionKind.Sharded => "ssubscribe", _ => "subscribe" };
        var message = kind switch { SubscriptionKind.Pattern => "pmessage", SubscriptionKind.Sharded => "smessage", _ => "message" };
        var pattern = kind == SubscriptionKind.Pattern ? "$2\r\nch\r\n" : "";
        return (Encoding.ASCII.GetBytes($"*3\r\n${verb.Length}\r\n{verb}\r\n$2\r\nch\r\n:1\r\n"),
            Encoding.ASCII.GetBytes($"*{(kind == SubscriptionKind.Pattern ? 4 : 3)}\r\n${message.Length}\r\n{message}\r\n{pattern}$2\r\nch\r\n$5\r\nhello\r\n"));
    }

    internal sealed record Item(string Name, string? Unit, double Value, Dictionary<string, object?> Tags);

    internal sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();
        private readonly Channel<bool> _events = Channel.CreateUnbounded<bool>();
        private readonly bool _throwOnMeasurement;

        internal Capture(bool throwOnMeasurement = false)
        {
            _throwOnMeasurement = throwOnMeasurement;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name is "redis.client.pubsub.messages" or "redis.client.stream.lag")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Items.Enqueue(new(instrument.Name, instrument.Unit, value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value)));
            _events.Writer.TryWrite(true);
            if (_throwOnMeasurement) throw new InvalidOperationException("Test listener failure.");
        }

        internal async Task WaitForCountAsync(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Items.Count < count) await _events.Reader.ReadAsync(deadline.Token);
        }

        public void Dispose() => _listener.Dispose();
    }
}
