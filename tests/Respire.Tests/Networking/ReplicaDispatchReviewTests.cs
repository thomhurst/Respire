using System.Runtime.CompilerServices;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ReplicaDispatchReviewTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_entriesGate")]
    private static extern ref Lock EntriesGate(ReadEndpointRouter router);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readyReplica")]
    private static extern ref ReadEndpointRouter.ReadyReplica? ReadyPublication(ReadEndpointRouter router);

    [Test]
    [Arguments(RespireReadFrom.Replica, "string")]
    [Arguments(RespireReadFrom.Replica, "bytes")]
    [Arguments(RespireReadFrom.Replica, "typed")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "string")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "bytes")]
    [Arguments(RespireReadFrom.ReplicaPreferred, "typed")]
    public async Task BacklogStartingDuringReadySelectionPreventsDirectDispatch(RespireReadFrom policy, string surface)
    {
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
        await Assert.That(RespireTelemetry.IsOperationEnabled("STRLEN")).IsFalse();
        await using var primary = StandaloneReplicaReadyTests.Server();
        await using var replica = StandaloneReplicaReadyTests.Server();
#pragma warning disable RESP010 // This dispatch scenario owns and disposes its client to isolate backlog publication and hedge deadlines.
        await using var client = await RespireClient.ConnectAsync(StandaloneReplicaReadyTests.Options(primary, replica));
#pragma warning restore RESP010
        var router = client.Core.ReadRouter;
        var connection = await router.GetConnectionAsync(policy, default, default);
        var view = client.WithReadFrom(policy);
        _ = view.Strings;
        replica.SuppressReply = command => command.EndsWith(" later", StringComparison.Ordinal);
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeHead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? head = null;
        var worker = new Thread(() =>
        {
            try
            {
                Task read = surface switch
                {
                    "string" => view.GetStringAsync("later").AsTask(),
                    "bytes" => view.GetBytesAsync("later").AsTask(),
                    _ => view.Strings.LengthAsync("later").AsTask(),
                };
                started.TrySetResult(read);
            }
            catch (Exception error) { started.TrySetException(error); }
        }) { IsBackground = true };
        try
        {
            using (EntriesGate(router).EnterScope())
            {
                // Force publication capture to pause after the initial backlog check.
                Volatile.Write(ref ReadyPublication(router), null);
                worker.Start();
                if (!SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, Limit))
                    throw new TimeoutException("Ready selection did not reach the publication gate.");
                head = router.EnqueueAsync(policy, async (_, token) =>
                {
                    await resumeHead.Task.WaitAsync(token);
                    return 1;
                }, default, default).AsTask();
            }
            var pending = await started.Task.WaitAsync(Limit);
            await Assert.That(router.IsBacklogDraining).IsTrue();
            await Assert.That(connection.InspectForTests().Inflight.TryPeek(out _)).IsFalse();
            await Assert.That(pending.IsCompleted).IsFalse();
            resumeHead.TrySetResult();
            await head!.WaitAsync(Limit);
            using var limit = new CancellationTokenSource(Limit);
            while (!replica.ReceivedCommands.Any(command => command.EndsWith(" later", StringComparison.Ordinal)))
                await Task.Delay(1, limit.Token);
            await replica.SendRawAsync(surface == "typed" ? ":5\r\n"u8.ToArray() : "$5\r\nhello\r\n"u8.ToArray());
            await pending.WaitAsync(Limit);
            await Assert.That(replica.ReceivedCommands.Count(command => command.EndsWith(" later", StringComparison.Ordinal)))
                .IsEqualTo(1);
        }
        finally
        {
            resumeHead.TrySetResult();
            if (!worker.Join(Limit)) throw new TimeoutException("Ready selection worker did not stop.");
        }
    }

    [Test]
    [Arguments(RespireBacklogPolicy.Queue, false, true)]
    [Arguments(RespireBacklogPolicy.Queue, true, true)]
    [Arguments(RespireBacklogPolicy.FailFast, false, true)]
    [Arguments(RespireBacklogPolicy.FailFast, true, true)]
    [Arguments(RespireBacklogPolicy.Queue, false, false)]
    [Arguments(RespireBacklogPolicy.FailFast, false, false)]
    public async Task HedgeLegsShareOnePostAcquisitionDeadline(RespireBacklogPolicy policy, bool previouslySelected, bool timeout)
    {
        await using var primary = StandaloneReplicaReadyTests.Server();
        await using var replica = StandaloneReplicaReadyTests.Server();
        var options = StandaloneReplicaReadyTests.Options(primary, replica) with
        {
            BacklogPolicy = policy,
            CommandTimeout = timeout ? Limit : null,
            RetryPolicy = null,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(100), MaximumExtraLoadPercent = 100 },
        };
#pragma warning disable RESP010 // This dispatch scenario owns and disposes its client to isolate backlog publication and hedge deadlines.
        await using var client = await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010
        var router = client.Core.ReadRouter;
        // Prepare the physical sockets without recording successful dispatch selection.
        var selected = await router.GetReplicaFromEndpointsAsync(options.ReplicaEndpoints.ToArray(), default, default);
        await Assert.That(router.CanQueue(RespireReadFrom.ReplicaPreferred)).IsFalse();
        if (previouslySelected)
            await router.GetConnectionAsync(RespireReadFrom.ReplicaPreferred, default, default);
        primary.SuppressReply = command => command == "GET held";
        replica.SuppressReply = command => command == "GET held";
        using var caller = new CancellationTokenSource();
        var pending = client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("held", caller.Token).AsTask();
        try
        {
            using var limit = new CancellationTokenSource(Limit);
            while (!primary.ReceivedCommands.Contains("GET held")) await Task.Delay(1, limit.Token);
            await Assert.That(selected.Connection.InspectForTests().Inflight.TryPeek(out var original)).IsTrue();
            await Assert.That(client.Core.Multiplexer.GetConnection().InspectForTests().Inflight.TryPeek(out var hedge)).IsTrue();
            await Assert.That(original!.Deadline.IsSet).IsEqualTo(timeout);
            await Assert.That(hedge!.Deadline.RawValue).IsEqualTo(original.Deadline.RawValue);
        }
        finally
        {
            await caller.CancelAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        }
    }
}
