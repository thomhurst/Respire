using System.Collections.Concurrent;
using System.Text;
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
