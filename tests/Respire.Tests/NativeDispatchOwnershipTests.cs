using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class NativeDispatchOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PhysicalSetupKeepsOriginalFactoryErrorAndCancellationToken(bool callerCancellation)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: false);
        using var cancellation = new CancellationTokenSource();
        Exception expected = callerCancellation ? new OperationCanceledException(cancellation.Token)
            : new IOException("original setup failure");
        if (callerCancellation) cancellation.Cancel();
        Exception? actual = null;
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("scripted", 6379, new()
            {
                Protocol = RespProtocol.Resp2,
                ObserveEstablishmentErrors = true,
                TestingStreamFactory = (_, _, _) => ValueTask.FromException<Stream>(expected),
            }, cancellationToken: cancellation.Token);
        }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        if (callerCancellation)
            await Assert.That(((OperationCanceledException)actual!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(callerCancellation ? 0 : 1);
        if (!callerCancellation)
            await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
    }

    [Test]
    [MatrixDataSource]
    public async Task NativeRoutesKeepPhysicalDisconnectSeparateFromCallerFailure(
        [Matrix("raw", "catalog", "interpolated", "string", "bytes", "typed")] string route,
        [Matrix("idle", "pending", "cancelled", "success")] string outcome,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(4, "$-1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command is not ("GET key" or "STRLEN key")) return false;
                received.TrySetResult();
                return outcome is "pending" or "cancelled";
            },
            ReplyOverride = (_, command) => command == "STRLEN key" ? ":1\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        if (outcome != "idle")
        {
            pending = ConsumeNativeRoute(client, route, cancellation.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome == "cancelled")
            {
                cancellation.Cancel();
                var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
                await Assert.That(pending.IsCanceled).IsTrue();
            }
            else if (outcome == "success") await pending;
        }
        // The RESP2 client sends no handshake, so ConnectAsync can finish while the socket still
        // sits in the listener backlog. On Windows, stopping the listener then neither resets nor
        // closes that socket, so an idle client never sees the disconnect. Close the socket only after the fixture owns it.
        await server.ConnectionAccepted.WaitAsync(TimeSpan.FromSeconds(5));
        await server.DisposeAsync();
        await connection.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? callerError = null;
        if (outcome == "pending")
            callerError = await Assert.That(async () => await pending!).Throws<RespireConnectionException>();
        var items = capture.Items.ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        var physical = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(physical.Length).IsEqualTo(1);
        await Assert.That(physical[0].Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(final.Length).IsEqualTo(outcome is "pending" or "cancelled" ? 1 : 0);
        foreach (var item in items)
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (outcome == "pending")
            await Assert.That(final[0].Tags["error.type"]).IsEqualTo(callerError!.GetType().FullName);
        if (outcome == "cancelled")
            await Assert.That(final[0].Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
    }

    [Test]
    [MatrixDataSource]
    public async Task CancelledNativeRoutesDrainLateErrorBeforeNextReply(
        [Matrix("raw", "catalog", "interpolated", "string", "bytes", "typed")] string route,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(4, "$-1\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command is not ("GET key" or "STRLEN key")) return false;
                received.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command == "PING" ? FakeRespServer.PongReply : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var cancellation = new CancellationTokenSource();
        var pending = ConsumeNativeRoute(client, route, cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(pending.IsCanceled).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);

        // Caller ownership is complete. The receive reference still owns the original FIFO slot.
        await server.SendRawAsync("-WRONGTYPE late private reply\r\n"u8.ToArray());
        using var next = await client.ExecuteAsync("PING").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(next.AsString()).IsEqualTo("PONG");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        var discarded = items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(discarded.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(discarded.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    private static async Task ConsumeNativeRoute(RespireClient client, string route, CancellationToken token)
    {
        switch (route)
        {
            case "raw": using (await client.ExecuteAsync("GET", ["key"], cancellationToken: token)) { } break;
            case "catalog": using (await client.ExecuteAsync(RespireCommands.String.GET, ["key"], cancellationToken: token)) { } break;
            case "interpolated": using (await client.ExecuteAsync($"GET key", cancellationToken: token)) { } break;
            case "string": _ = await client.GetStringAsync("key", token); break;
            case "bytes": _ = await client.GetBytesAsync("key", token); break;
            case "typed": _ = await client.Strings.LengthAsync("key", token); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private sealed record Item(Dictionary<string, object?> Tags);

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();

        internal Capture(bool throwOnMeasurement, bool commandMetrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, RespireTelemetry.Meter) && (instrument.Name == "redis.client.errors"
                    || commandMetrics && instrument.Name == "db.client.operation.duration"))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                Items.Enqueue(new(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
                if (throwOnMeasurement) throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            _listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
