using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscriptionFinalFailureObservesLateSelection(bool attachLate)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = attachLate ? RespireMetricGroups.Resiliency : RespireMetricGroups.None,
        });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "SUBSCRIBE one",
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.SubscribeAsync("one", deadline.Token);
        while (!server.ReceivedCommands.Contains("SUBSCRIBE one")) await Task.Delay(1, deadline.Token);
        using var capture = new Capture(throwOnMeasurement: true);
        if (!attachLate) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var index = server.ReceivedCommands.ToList().IndexOf("SUBSCRIBE one");
        await server.SendRawAsync("-NOPERM late rejection\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        await Assert.That(async () => await pending).Throws<RespireServerException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(1, true)]
    public async Task ShardedRecoveryOwnerRetainsAttemptsAndExcludesShutdown(int attempts, bool shutdown)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        var recovering = false;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (!shutdown || !Volatile.Read(ref recovering) || command != "SSUBSCRIBE one") return false;
            waiting.TrySetResult();
            return true;
        };
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "SSUBSCRIBE one" => Volatile.Read(ref recovering) ? "-NOPERM recovery rejected\r\n"u8.ToArray()
                : "*3\r\n$10\r\nssubscribe\r\n$3\r\none\r\n:1\r\n"u8.ToArray(),
            "SUNSUBSCRIBE one" => "*3\r\n$12\r\nsunsubscribe\r\n$3\r\none\r\n:0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = attempts },
        });
        await using var subscription = await client.SubscribeShardedAsync("one").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var index = server.ReceivedCommands.ToList().LastIndexOf("SSUBSCRIBE one");
        using var capture = new Capture(throwOnMeasurement: true);
        Volatile.Write(ref recovering, true);
        await server.SendRawAsync("*3\r\n$12\r\nsunsubscribe\r\n$3\r\none\r\n:0\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        if (shutdown)
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.DisposeAsync();
            await Assert.That(capture.Items).IsEmpty();
        }
        else
        {
            await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
                .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
            var items = capture.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(attempts);
            for (var attempt = 0; attempt < items.Length; attempt++)
            {
                await Assert.That(items[attempt].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
                await Assert.That(items[attempt].Tags["redis.client.errors.internal"]).IsEqualTo(true);
                await Assert.That(items[attempt].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(attempt);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShardedRecoveryHandlesNonCancellationFailureRacingDisposal(bool disposeHub)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "SSUBSCRIBE one" => "*3\r\n$10\r\nssubscribe\r\n$3\r\none\r\n:1\r\n"u8.ToArray(),
            "SUNSUBSCRIBE one" => "*3\r\n$12\r\nsunsubscribe\r\n$3\r\none\r\n:0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
        });
        await using var hub = new SubscriptionHub(client.Core);
        await using var subscription = await hub.SubscribeAsync(
            SubscriptionKind.Sharded, ["one"], new(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = typeof(SubscriptionHub).GetField("_shardedOwners", flags)!.GetValue(hub)!;
        owners.GetType().GetMethod("Clear")!.Invoke(owners, null);
        var disposed = typeof(SubscriptionHub).GetField("_disposed", flags)!;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "SSUBSCRIBE one") return false;
            waiting.TrySetResult();
            return true;
        };
        using var capture = new Capture(throwOnMeasurement: true);
        // Await the worker itself: its drained signal completes even when the worker faults.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = (Task)typeof(SubscriptionHub).GetMethod("RecoverShardedAsync", flags)!.Invoke(hub, [drained])!;
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Reproduce the disposal window before hub lifetime cancellation reaches the command.
            if (disposeHub) disposed.SetValue(hub, true);
            else client.Core.Disposed = true;
            var index = server.ReceivedCommands.ToList().LastIndexOf("SSUBSCRIBE one");
            await server.SendRawAsync("-NOPERM disposal race\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            await recovery.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(recovery.IsCompletedSuccessfully).IsTrue();
            await Assert.That(drained.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(capture.Items).IsEmpty();
        }
        finally
        {
            disposed.SetValue(hub, false);
            client.Core.Disposed = false;
            await hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task NotificationCleanupDeadlineReportsInternalFailure()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply);
        var channel = RespireChannel.KeySpacePrefix("one:", 0);
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ when command == $"PSUBSCRIBE {channel}" => Encoding.ASCII.GetBytes(
                $"*3\r\n$10\r\npsubscribe\r\n${channel.ToString().Length}\r\n{channel}\r\n:1\r\n"),
            _ => null,
        };
        server.SuppressReply = command => command == $"PUNSUBSCRIBE {channel}";
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
            CommandTimeout = null, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        await using var subscription = await client.SubscribeAsync(channel).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        using var capture = new Capture(throwOnMeasurement: true);
        await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(server.ReceivedCommands).Contains($"PUNSUBSCRIBE {channel}");
        await Assert.That(client.Core.Disposed).IsFalse();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(true);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task InternalPubSubOwnerRejectsLateBorrowerAfterCompletion()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var completed = DispatchResponseSource<bool>.Start();
        var borrowed = completed.Observation;
        completed.CompleteInternal();
        var next = DispatchResponseSource<bool>.Start();
        try
        {
            borrowed.Handled(new IOException("late recovery callback"));
            await Assert.That(next.Observation.Attempts).IsEqualTo(0);
            await Assert.That(capture.Items).IsEmpty();
        }
        finally { next.CompleteInternal(); }
    }

    [Test]
    public async Task WarmInternalPubSubOwnersAllocateNothing()
    {
        for (var index = 0; index < 4; index++) _ = MeasureInternalOwners(false);
        _ = MeasureInternalOwners(true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureInternalOwners(false), MeasureInternalOwners(true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThan(0L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureInternalOwners(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var owner = DispatchResponseSource<bool>.Start();
            if (owner.Observation.Attempts != 0) throw new InvalidOperationException("Unexpected retry count.");
            owner.CompleteInternal();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
