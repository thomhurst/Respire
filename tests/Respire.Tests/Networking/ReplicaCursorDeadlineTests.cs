using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaCursorDeadlineTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref SemaphoreSlim EntryGate(ReadEndpointRouter.Entry entry);

    [Test]
    [Arguments("raw", false, false)]
    [Arguments("typed", false, false)]
    [Arguments("fire-and-forget", false, false)]
    [Arguments("raw", true, false)]
    [Arguments("typed", true, false)]
    [Arguments("fire-and-forget", true, false)]
    [Arguments("raw", false, true)]
    [Arguments("typed", false, true)]
    [Arguments("fire-and-forget", false, true)]
    [Arguments("raw", true, true)]
    [Arguments("typed", true, true)]
    [Arguments("fire-and-forget", true, true)]
    public async Task PinnedAcquisitionPreservesCommandBudgetAndCallerCancellation(
        string surface, bool failFast, bool cancel)
    {
        await using var primary = new FakeRespServer(32, FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(32, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => ReplicaRole,
                _ when command.StartsWith("SCAN 0", StringComparison.Ordinal) => "*2\r\n$1\r\n7\r\n*1\r\n$5\r\nfirst\r\n"u8.ToArray(),
                _ when command.StartsWith("SCAN 7", StringComparison.Ordinal) => "*2\r\n$1\r\n0\r\n*1\r\n$4\r\nlast\r\n"u8.ToArray(),
                _ => null,
            },
        };
        var timeout = TimeSpan.FromMilliseconds(500);
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, ConnectTimeout = Limit,
            CommandTimeout = timeout, RetryPolicy = null, ThreadPoolMonitoring = false,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(10),
            BacklogPolicy = failFast ? RespireBacklogPolicy.FailFast : RespireBacklogPolicy.Queue,
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var router = client.Core.ReadRouter;
        // Prepare the socket outside the first page's command budget.
        var selection = await router.GetReplicaFromEndpointsAsync(options.ReplicaEndpoints.ToArray(), default, default);
        await using var view = client.WithReadFrom(RespireReadFrom.Replica);
        using var caller = new CancellationTokenSource();
        await using var scan = view.Keys.ScanAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        if (surface == "typed")
        {
            await Assert.That(await scan.MoveNextAsync()).IsTrue();
            await Assert.That(scan.Current).IsEqualTo("first");
        }
        else
        {
            using var first = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"]);
        }
        await Assert.That(router.CanQueue(RespireReadFrom.Replica)).IsEqualTo(!failFast);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (surface != "fire-and-forget" && !cancel)
            replica.SuppressReply = command =>
            {
                if (!command.StartsWith("SCAN 7", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            };
        var gate = EntryGate(selection.Replica!);
        await gate.WaitAsync();
        Task pending;
        long releasedAt = 0;
        try
        {
            // Require revalidation, making the pinned acquisition wait on this gate.
            router.RoleRevalidationInterval = TimeSpan.Zero;
            pending = ContinueAsync();
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(router.IsBacklogDraining).IsFalse();
            if (cancel)
            {
                await caller.CancelAsync();
                await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            }
            else
            {
                await Task.Delay(timeout + TimeSpan.FromMilliseconds(200));
                await Assert.That(pending.IsCompleted).IsFalse();
            }
            await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("SCAN 7", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            releasedAt = Environment.TickCount64;
            gate.Release();
        }
        if (cancel) return;
        if (surface != "fire-and-forget")
        {
            await received.Task.WaitAsync(Limit);
            try
            {
                await Assert.That(selection.Connection.InspectForTests().Inflight.TryPeek(out var reply)).IsTrue();
                // The response owns a full transport budget after acquisition, even if the
                // server could otherwise reply quickly enough to hide an expired deadline.
                await Assert.That(reply!.Deadline.Ticks).IsGreaterThanOrEqualTo(releasedAt + (long)timeout.TotalMilliseconds);
            }
            finally { await replica.SendRawAsync("*2\r\n$1\r\n0\r\n*1\r\n$4\r\nlast\r\n"u8.ToArray()); }
        }
        await pending.WaitAsync(Limit);
        // A round trip also drains the fire-and-forget reply before checking receipt.
        await view.GetStringAsync("barrier");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("SCAN 7", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();

        async Task ContinueAsync()
        {
            if (surface == "typed")
            {
                await Assert.That(await scan.MoveNextAsync()).IsTrue();
                await Assert.That(scan.Current).IsEqualTo("last");
            }
            else if (surface == "fire-and-forget")
                await view.ExecuteFireAndForgetAsync(RespireCommands.Key.SCAN, caller.Token, ["7"]);
            else
            {
                using var next = await view.ExecuteAsync(RespireCommands.Key.SCAN, caller.Token, ["7"]);
            }
        }
    }
}
