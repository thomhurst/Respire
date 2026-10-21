using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectEvictionTests
{
    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectMetricsCanDisposeClientWithoutDeadlockingReceiveLoop(bool reconnect)
    {
        using var metrics = new MetricConfigurationScope();
        var limit = TimeSpan.FromSeconds(10);
        await using var target = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => Encoding.UTF8.GetBytes(command.StartsWith("HELLO 3", StringComparison.Ordinal)
                ? "%1\r\n$5\r\nproto\r\n:3\r\n" : command.StartsWith("GET ", StringComparison.Ordinal) ? "$1\r\nv\r\n" : "+OK\r\n"),
        };
        await using var source = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => Encoding.UTF8.GetBytes(command.StartsWith("HELLO 3", StringComparison.Ordinal)
                ? "%1\r\n$5\r\nproto\r\n:3\r\n" : command.StartsWith("GET ", StringComparison.Ordinal)
                    ? $"-REDIRECT 127.0.0.1:{target.Port}\r\n" : "+OK\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
            EnableReplicaRedirects = true, ClientSideCache = new(),
            ReconnectPolicy = reconnect ? new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 } : null,
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("v");
        await Assert.That(client.Core.ClientCache!.Count).IsEqualTo(1);
        var flushed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClientCacheContinuityFlushes))
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (Interlocked.Increment(ref callbacks) != 1) return;
            var remaining = client.Core.ClientCache!.Count;
            try
            {
                // Bound the deliberate synchronous join so the regression fails instead
                // of stranding the test process when disposal waits for this callback.
#pragma warning disable RESP009
                client.DisposeAsync().AsTask().WaitAsync(limit).GetAwaiter().GetResult();
#pragma warning restore RESP009
                flushed.TrySetResult(remaining);
            }
            catch (Exception error) { flushed.TrySetException(error); }
        });
        listener.Start();
        target.CloseConnections();
        await Assert.That(await flushed.Task.WaitAsync(limit + limit)).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task EvictionMetricsCanDisposeClientWithoutDeadlockingSelection()
    {
        using var metrics = new MetricConfigurationScope();
        var targets = Enumerable.Range(0, ReplicaRedirectTargets.MaximumTargets + 1)
            .Select(_ => new FakeRespServer(4)
            {
                ReplyOverride = (_, command) => Encoding.UTF8.GetBytes(command.StartsWith("HELLO 3", StringComparison.Ordinal)
                    ? "%1\r\n$5\r\nproto\r\n:3\r\n" : command.StartsWith("GET ", StringComparison.Ordinal) ? "$1\r\nv\r\n" : "+OK\r\n"),
            }).ToArray();
        try
        {
            await using var source = new FakeRespServer(4)
            {
                ReplyOverride = (_, command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes("%1\r\n$5\r\nproto\r\n:3\r\n") : FakeRespServer.OkReply,
            };
            await using var client = await RespireClient.ConnectAsync(new RespireOptions()
            {
                Endpoints = [new("127.0.0.1", source.Port)], Connections = 1,
                EnableReplicaRedirects = true, ClientSideCache = new(),
            });
            foreach (var target in targets.Take(ReplicaRedirectTargets.MaximumTargets))
            {
                using var lease = await client.Core.RedirectTargets!.AcquireAsync(new("127.0.0.1", target.Port), default);
            }
            var callbacks = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, observer) =>
            {
                if (ReferenceEquals(instrument, RespireTelemetry.ClientCacheContinuityFlushes))
                    observer.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            {
                if (Interlocked.Increment(ref callbacks) == 1)
                {
                    // Deliberately reproduce synchronous disposal from instrumentation.
#pragma warning disable RESP009
                    client.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore RESP009
                }
            });
            listener.Start();
            var acquisition = client.Core.RedirectTargets!.AcquireAsync(new("127.0.0.1", targets[^1].Port), default).AsTask();
            await Assert.That(async () =>
            {
                using var lease = await acquisition.WaitAsync(TimeSpan.FromSeconds(10));
            }).Throws<OperationCanceledException>();
            await Assert.That(callbacks).IsGreaterThan(0);
        }
        finally { foreach (var target in targets) await target.DisposeAsync(); }
    }
}
