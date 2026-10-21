using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectTargetRecoveryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedRecoveryFailuresKeepTheObservedSocketContinuityFence(bool cancelWaiter)
    {
        var rejectHandshake = false;
        await using var target = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
                && Volatile.Read(ref rejectHandshake) ? Bytes("-LOADING dataset\r\n") : TargetReply(command),
        };
        await using var source = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry: false));
        var targets = client.Core.RedirectTargets!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        // Keep the initialized owner busy, so exhaustion cannot replace it during selection.
        using var original = await targets.AcquireAsync(endpoint, default);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original!.Connection.PendingCommandsFailing += () => failed.TrySetResult();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.GateCommand("HELLO 3", release.Task, () => started.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var before = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        Volatile.Write(ref rejectHandshake, !cancelWaiter);
        target.CloseConnections();
        try
        {
            await failed.Task.WaitAsync(Limit);
            await started.Task.WaitAsync(Limit);
            var second = targets.AcquireAsync(endpoint, cancellation.Token).AsTask();
            var third = targets.AcquireAsync(endpoint, default).AsTask();
            await Assert.That(second.IsCompleted).IsFalse();
            await Assert.That(third.IsCompleted).IsFalse();
            // This value is covered by the source's uninterrupted tracking connection.
            // Recovery waiters must not repeat the target socket's completed loss fence.
            await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
            if (cancelWaiter)
            {
                cancellation.Cancel();
                var error = await Assert.That(async () => { using var lease = await second.WaitAsync(Limit); })
                    .Throws<OperationCanceledException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            }
            release.TrySetResult();
            if (cancelWaiter)
            {
                using var recovered = await third.WaitAsync(Limit);
                await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
                await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
                await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
                await Assert.That(source.ReceivedCommands.Count(command => command == "GET fresh")).IsEqualTo(1);

                // The replacement socket owns a fresh loss fence, independent of the old waiters.
                var failedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                recovered!.Connection.PendingCommandsFailing += () => failedAgain.TrySetResult();
                target.CloseConnections();
                await failedAgain.Task.WaitAsync(Limit);
                await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(2L);
                await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
            }
            else
            {
                await Assert.That(async () => { using var lease = await second.WaitAsync(Limit); })
                    .Throws<RespireReconnectLimitException>();
                await Assert.That(async () => { using var lease = await third.WaitAsync(Limit); })
                    .Throws<RespireReconnectLimitException>();
                await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
                await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
                await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
                await Assert.That(source.ReceivedCommands.Count(command => command == "GET fresh")).IsEqualTo(1);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SharedInitialAcquisitionFailuresKeepTheOriginalContinuityFence(bool cancelWaiter)
    {
        var rejectHandshake = true;
        await using var target = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
                && Volatile.Read(ref rejectHandshake) ? Bytes("-ERR handshake rejected\r\n") : TargetReply(command),
        };
        await using var source = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry: false) with
        { ReconnectPolicy = null });
        var targets = client.Core.RedirectTargets!;
        var endpoint = new RespireEndpoint("127.0.0.1", target.Port);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.GateCommand("HELLO 3", release.Task, () => started.TrySetResult());
        using var cancellation = new CancellationTokenSource();
        var before = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        var first = targets.AcquireAsync(endpoint, default).AsTask();
        try
        {
            await started.Task.WaitAsync(Limit);
            // Both calls synchronously select the retained entry before waiting for its
            // initialization gate. The server barrier keeps that first handshake pending.
            var second = targets.AcquireAsync(endpoint, cancellation.Token).AsTask();
            var third = targets.AcquireAsync(endpoint, default).AsTask();
            await Assert.That(second.IsCompleted).IsFalse();
            await Assert.That(third.IsCompleted).IsFalse();
            await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
            if (cancelWaiter) cancellation.Cancel();
            release.TrySetResult();

            await Assert.That(async () => { using var lease = await first.WaitAsync(Limit); }).Throws<RespireConnectionException>();
            if (cancelWaiter)
            {
                var error = await Assert.That(async () => { using var lease = await second.WaitAsync(Limit); })
                    .Throws<OperationCanceledException>();
                await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            }
            else
                await Assert.That(async () => { using var lease = await second.WaitAsync(Limit); }).Throws<RespireConnectionException>();
            await Assert.That(async () => { using var lease = await third.WaitAsync(Limit); }).Throws<RespireConnectionException>();

            await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
            await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
            await Assert.That(source.ReceivedCommands.Count(command => command == "GET fresh")).IsEqualTo(1);

            // A successful tracking handshake ends this initial acquisition episode.
            Volatile.Write(ref rejectHandshake, false);
            using var recovered = await targets.AcquireAsync(endpoint, default);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recovered!.Connection.PendingCommandsFailing += () => failed.TrySetResult();
            target.CloseConnections();
            await failed.Task.WaitAsync(Limit);
            await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(2L);
            await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        }
        finally { release.TrySetResult(); }
    }

    [Test, NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PlannedReplacementDoesNotRefenceWhenOldTargetFinishesDraining(bool lifetime, bool retry)
    {
        using var metrics = new MetricConfigurationScope();
        var clock = new ClusterRecoveryTestClock();
        await using var replacement = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var target = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => TargetReply(command),
            SuppressReply = command => command == "PING",
        };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        {
            ConnectionMaxLifetime = lifetime ? Limit : null,
            ConnectionLifetimeTimeProvider = clock,
        });
        await Assert.That(await client.GetStringAsync("seed")).IsEqualTo("value");
        using var lease = await client.Core.RedirectTargets!.AcquireAsync(new("127.0.0.1", target.Port), default);
        var original = lease!.Connection;
        var node = original.Multiplexer!;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        original.PendingCommandsFailing += () => failed.TrySetResult();
        var pending = original.SendAsync(new RawCommand(RespCommands.Ping)).AsTask();
        using var timeout = new CancellationTokenSource(Limit);
        while (!target.ReceivedCommands.Contains("PING")) await Task.Delay(10, timeout.Token);
        var before = client.ClientSideCache!.GetStatistics().ContinuityFlushes;
        var fenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument, RespireTelemetry.ClientCacheContinuityFlushes))
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => fenced.TrySetResult());
        listener.Start();

        if (lifetime)
        {
            // Both source and target lifetime workers must register their delays before
            // advancing: a late registration could otherwise miss this clock advance.
            while (clock.ActiveTimers < 2) await Task.Delay(10, timeout.Token);
            // Keep the source's recycle unpublished so only the target's fence is measured.
            source.SuppressReply = command => command.StartsWith("HELLO 3", StringComparison.Ordinal);
            clock.Advance(Limit);
            while (original.IsAcceptingCommands) await Task.Delay(10, timeout.Token);
        }
        else
        {
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node.MovingHandoffPublished += () => published.TrySetResult();
            var announcement = node.CaptureMovingAnnouncement(0, original,
                new MaintenanceNotification("MOVING", 1, 10, new("127.0.0.1", replacement.Port)));
            node.QueueDedicatedMovingHandoff(original, announcement, static () => true);
            await published.Task.WaitAsync(Limit);
        }

        // Both publication fences have completed before their continuity metric is emitted.
        await fenced.Task.WaitAsync(Limit);
        await Assert.That(original.IsConnected).IsTrue();
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await target.SendRawAsync(FakeRespServer.PongReply);
        using var pong = await pending.WaitAsync(Limit);
        await failed.Task.WaitAsync(Limit);
        // Even re-observing the retired socket must not re-arm its failure fence.
        lease.UseConnection(original);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("fresh")).IsEqualTo("value");
        var serving = lifetime ? target : replacement;
        await Assert.That(serving.ReceivedCommands.Count(command => command == "GET fresh")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectFencesContinuityOncePerObservedSocket(bool retry)
    {
        await using var target = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        using var lease = await client.Core.RedirectTargets!.AcquireAsync(new("127.0.0.1", target.Port), default);
        var connection = lease!.Connection;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.PendingCommandsFailing += () => failed.TrySetResult();
        var before = client.ClientSideCache!.GetStatistics().ContinuityFlushes;

        target.CloseConnections();
        await failed.Task.WaitAsync(Limit);
        // Re-observing the failed selection must not fence the same loss a second time.
        lease.UseConnection(connection);
        await Assert.That(client.ClientSideCache.GetStatistics().ContinuityFlushes - before).IsEqualTo(1L);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetainedTargetMovingRefreshesExpiredAcquisitionToken(bool circuit)
    {
        await using var replacement = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var target = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => TargetReply(command),
            SuppressReply = command => command == "PING",
        };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry: true) with
        {
            CommandTimeout = TimeSpan.FromSeconds(1), MaintenanceRelaxedTimeout = Limit,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CircuitBreaker = circuit ? new() : null, MaxInflightCommands = 2,
        });
        await Assert.That(await client.GetStringAsync("seed")).IsEqualTo("value");
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static activitySource => activitySource.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        using var lease = await client.Core.RedirectTargets!.AcquireAsync(new("127.0.0.1", target.Port), default);
        var original = lease!.Connection;
        await target.SendRawAsync(Bytes(">3\r\n+MIGRATING\r\n:1\r\n:10\r\n"));
        using var timeout = new CancellationTokenSource(Limit + Limit);
        while (!original.HasMaintenanceWindow) await Task.Delay(10, timeout.Token);
        var accepted = original.SendAsync(new RawCommand(RespCommands.Ping)).AsTask();
        while (!target.ReceivedCommands.Contains("PING")) await Task.Delay(10, timeout.Token);

        // A tracked GET needs two ring slots, so it waits behind the accepted PING.
        var pending = client.GetStringAsync("key", timeout.Token).AsTask();
        while (!source.ReceivedCommands.Contains("GET key")) await Task.Delay(10, timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), timeout.Token);
        await Assert.That(pending.IsCompleted).IsFalse();
        var node = original.Multiplexer!;
        var announcement = node.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, new("127.0.0.1", replacement.Port)));
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.MovingHandoffPublished += () => published.TrySetResult();
        node.QueueDedicatedMovingHandoff(original, announcement, static () => true);
        await published.Task.WaitAsync(Limit);
        await target.SendRawAsync(FakeRespServer.PongReply);
        using var reply = await accepted.WaitAsync(Limit);

        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Contains("GET key")).IsFalse();
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        var trace = activities.Single(activity => Equals(activity.GetTagItem("db.operation.name"), "GET"));
        await Assert.That(trace.GetTagItem("server.port")).IsEqualTo(replacement.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustedIdleTargetIsReplacedOnNextRead(bool retry)
    {
        var rejectHandshake = 0;
        await using var target = new FakeRespServer(8)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
                && Volatile.Read(ref rejectHandshake) != 0
                    ? Bytes("-LOADING dataset\r\n") : TargetReply(command),
        };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var exhausted = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != target.Port) return;
            changes.Enqueue(change);
            if (change.ReconnectExhausted) exhausted.TrySetResult(change);
        };

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        Volatile.Write(ref rejectHandshake, 1);
        target.CloseConnections();
        var terminal = await exhausted.Task.WaitAsync(Limit);

        await Assert.That(terminal.SourceState).IsEqualTo((RespireConnectionState?)RespireConnectionState.Disconnected);
        await Assert.That(terminal.ReconnectSource).IsEqualTo(RespireReconnectSource.Dedicated);
        await Assert.That(terminal.ReconnectAttempt).IsEqualTo(1);
        await Assert.That(terminal.State).IsEqualTo(RespireConnectionState.Connected);
        await Assert.That(changes.Any(change => change.SourceState == RespireConnectionState.Reconnecting)).IsTrue();
        await Assert.That(changes.All(change => change.State == RespireConnectionState.Connected)).IsTrue();
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);

        // The old owner has stopped retrying. A fresh acquisition must get its own budget.
        Volatile.Write(ref rejectHandshake, 0);
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.Core.RedirectTargets!.Count).IsEqualTo(1);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IdleTargetRecoveryPublishesDedicatedLifecycleWithoutChangingHealth(bool retry)
    {
        await using var target = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        var reconnecting = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != target.Port) return;
            if (change.SourceState == RespireConnectionState.Reconnecting) reconnecting.TrySetResult(change);
            if (change.SourceState == RespireConnectionState.Connected) connected.TrySetResult(change);
        };
        target.CloseConnections();
        var started = await reconnecting.Task.WaitAsync(Limit);
        var recovered = await connected.Task.WaitAsync(Limit);
        foreach (var change in new[] { started, recovered })
        {
            await Assert.That(change.ReconnectSource).IsEqualTo(RespireReconnectSource.Dedicated);
            await Assert.That(change.State).IsEqualTo(RespireConnectionState.Connected);
            await Assert.That(change.ReconnectExhausted).IsFalse();
        }
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    [Arguments(RespireMaintenanceNotificationMode.Disabled, false)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, false)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, false)]
    [Arguments(RespireMaintenanceNotificationMode.Disabled, true)]
    [Arguments(RespireMaintenanceNotificationMode.Auto, true)]
    [Arguments(RespireMaintenanceNotificationMode.Enabled, true)]
    public async Task RetainedTargetNegotiatesConfiguredMaintenanceNotifications(RespireMaintenanceNotificationMode mode, bool retry)
    {
        await using var target = new FakeRespServer(8) { ReplyOverride = (_, command) => TargetReply(command) };
        await using var source = Source(target.Port);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port, retry) with
        { MaintenanceNotifications = mode });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands.Contains("CLIENT MAINT_NOTIFICATIONS ON"))
            .IsEqualTo(mode != RespireMaintenanceNotificationMode.Disabled);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    private static RespireOptions Options(int port, bool retry) => new()
    {
        Endpoints = [new("127.0.0.1", port)], Connections = 1, EnableReplicaRedirects = true,
        ClientSideCache = new(), RetryPolicy = retry ? new() : null, CommandTimeout = Limit,
        ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
    };

    private static FakeRespServer Source(int targetPort) => new(8)
    {
        ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Bytes($"-REDIRECT 127.0.0.1:{targetPort}\r\n") : Handshake(command),
    };

    private static byte[] TargetReply(string command) => command.StartsWith("GET ", StringComparison.Ordinal)
        ? Bytes("$5\r\nvalue\r\n") : Handshake(command);
    private static byte[] Handshake(string command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
        ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n") : FakeRespServer.OkReply;
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
