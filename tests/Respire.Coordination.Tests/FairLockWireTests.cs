using System.Diagnostics;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class FairLockWireTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationBoundsUnreturnedLeaseCleanupWithoutCommandTimeout(bool lostAcquisitionReply)
    {
        var acquisitionSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET lease" or "HGET queue head" => "_\r\n"u8.ToArray(),
                var value when value.StartsWith($"EVALSHA {RespireFairLock.Acquire.Sha1} ", StringComparison.Ordinal)
                    => "*3\r\n:1\r\n$1\r\n1\r\n:0\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = command =>
            {
                if (command.StartsWith($"EVALSHA {RespireFairLock.Acquire.Sha1} ", StringComparison.Ordinal))
                {
                    acquisitionSeen.TrySetResult();
                    return lostAcquisitionReply;
                }
                // Suppress every cleanup reply, including the old unbounded DELEX path.
                return command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    || command.StartsWith("DELEX ", StringComparison.Ordinal);
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1, ClientSideCache = new(), CommandTimeout = null,
        });
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (!lostAcquisitionReply && activity.GetTagItem("db.operation.name") is "EVALSHA"
                    && activity.GetTagItem("server.port") is int port && port == server.Port)
                    cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);
        var pending = new RespireCoordination(client).CreateFairLock("lease", "counter", "queue")
            .AcquireAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        await acquisitionSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (lostAcquisitionReply) await cancellation.CancelAsync();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Any(command =>
            command.StartsWith($"EVALSHA {RespireFairLock.RemoveWaiter.Sha1} ", StringComparison.Ordinal)
            && command.EndsWith(" release", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(RespireReadFrom.Replica)]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    public async Task RejectsReplicaReadViewsBeforeSendingCommands(RespireReadFrom readFrom)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("unused.invalid", 6379)], ReplicaEndpoints = [new("unused.invalid", 6380)],
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
        });
        var fair = new RespireCoordination(client.WithReadFrom(readFrom)).CreateFairLock("lease", "counter", "queue");
        await Assert.That(async () => await fair.AcquireAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)))
            .ThrowsExactly<RespireConfigurationException>();
    }
}
