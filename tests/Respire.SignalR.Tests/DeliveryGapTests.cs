using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Respire.SignalR.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel("signalr-integration")]
public class DeliveryGapTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ReconnectLogsGapBeforeSubsequentDelivery(RespProtocol protocol)
    {
        var name = Guid.NewGuid().ToString("N");
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString)
            with { ClientName = name, Protocol = protocol });
        await using var admin = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var logger = new GapLogger();
        await using var bus = new RespirePubSub(client, new() { ChannelPrefix = name }, logger, name);
        var received = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        (await bus.SubscribeAsync("messages")).OnMessage(message => received.TrySetResult(message.Message.Span[0]));
        var subscriber = (await admin.Server.ClientsAsync()).Single(row => row.Name == name && row.Flags.Contains('P'));
        await Assert.That(await admin.Server.KillClientAsync(subscriber.Id)).IsTrue();
        await Assert.That(await logger.Gap.Task.WaitAsync(TimeSpan.FromSeconds(10))).Contains("Reconnect");
        await bus.PublishAsync("messages", [42]);
        await Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo((byte)42);
        await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task BoundedOverflowReportsLossAndContinuesDelivery()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var hub = Guid.NewGuid().ToString("N");
        var logger = new GapLogger();
        var measurements = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Respire.SignalR") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "signalr.hub" && Equals(tag.Value, hub))
                    measurements.AddOrUpdate(instrument.Name, value, (_, previous) => previous + value);
        });
        listener.Start();
        await using var bus = new RespirePubSub(client,
            new() { ChannelPrefix = hub, SubscriptionBufferSize = 1 }, logger, hub);
        var receiver = await bus.SubscribeAsync("messages");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.OnMessage(async message =>
        {
            if (message.Message.Span[0] == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(message.Stopping);
            }
            else delivered.TrySetResult(message.Message.Span[0]);
        });
        await bus.PublishAsync("messages", [1]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await bus.PublishAsync("messages", [2]);
        await bus.PublishAsync("messages", [3]);
        // This SUBSCRIBE acknowledgement follows the queued publishes on the same subscriber socket.
        await bus.SubscribeAsync("barrier");
        release.SetResult();
        await Assert.That(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo((byte)3);
        await Assert.That(await logger.Gap.Task.WaitAsync(TimeSpan.FromSeconds(10))).Contains("BufferOverflow");
        await Assert.That(measurements["respire.signalr.delivery.gaps"]).IsEqualTo(1);
        await Assert.That(measurements["respire.signalr.messages.dropped"]).IsEqualTo(1);
        await bus.DisposeAsync();
        await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    private sealed class GapLogger : ILogger
    {
        internal TaskCompletionSource<string> Gap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (id.Id == 16) Gap.TrySetResult(format(state, error));
        }
    }
}
