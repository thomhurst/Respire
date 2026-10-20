using System.Diagnostics;
using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class FairLockWireTests
{
    [Test]
    [Arguments(10001L)]
    [Arguments(20001L)]
    public async Task MissingWaiterReportsServerClockEvidenceAndDoesNotRejoin(long serverTime)
    {
        var attempts = 0;
        var prefix = $"EVALSHA {RespireFairLock.Acquire.Sha1} ";
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET lease" or "HGET queue head" => "_\r\n"u8.ToArray(),
                var value when value.StartsWith(prefix, StringComparison.Ordinal) =>
                    Interlocked.Increment(ref attempts) == 1
                        ? "*5\r\n:0\r\n$0\r\n\r\n:1\r\n:10000\r\n:20000\r\n"u8.ToArray()
                        : Encoding.UTF8.GetBytes($"*7\r\n:-1\r\n$0\r\n\r\n:0\r\n:{serverTime}\r\n:0\r\n:-2\r\n:0\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)],
            Connections = 1, ClientSideCache = new(),
        });
        var fair = new RespireCoordination(client).CreateFairLock("lease", "counter", "queue");
        var error = await Assert.That(async () => await fair
            .AcquireAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Throws<TimeoutException>();
        await Assert.That(error!.Message).Contains("is no longer queued");
        await Assert.That(error.Message).Contains($"Redis TIME={serverTime}ms");
        await Assert.That(error.Message).Contains("previous TIME=10000ms, previous deadline=20000ms");
        await Assert.That(error.Message).Contains("elapsed since previous attempt=");
        await Assert.That(error.Message).Contains("queue PTTL=-2ms, queue fields=0");
        var acquisitions = server.ReceivedCommands
            .Where(command => command.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        await Assert.That(acquisitions.Length).IsEqualTo(2);
        await Assert.That(acquisitions[0].EndsWith(" join", StringComparison.Ordinal)).IsTrue();
        await Assert.That(acquisitions[1].EndsWith(" retry", StringComparison.Ordinal)).IsTrue();
    }

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
