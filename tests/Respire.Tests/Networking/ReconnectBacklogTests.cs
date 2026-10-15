using System.Collections.Concurrent;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReconnectBacklogTests
{
    [Test]
    public async Task UnexpectedSelectionFailureOnlyFailsItsOwnWaiter()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var failSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Unexpected selection failure");
        var selections = 0;
        var queue = new RespireConnectionMultiplexer.ReconnectBacklog(client.Core.Multiplexer, 4,
            async token =>
            {
                if (Interlocked.Increment(ref selections) == 1)
                {
                    await failSelection.Task.WaitAsync(token);
                    throw failure;
                }
                return client.Core.Multiplexer.GetConnection();
            });
        var dispatches = new ConcurrentQueue<int>();
        ValueTask<int> Dispatch(int value) { dispatches.Enqueue(value); return new(value); }
        var first = queue.EnqueueAsync((_, _) => Dispatch(1), deadline.Token).AsTask();
        var second = queue.EnqueueAsync((_, _) => Dispatch(2), deadline.Token).AsTask();
        await Assert.That(queue.Count).IsEqualTo(2);
        failSelection.SetResult();
        var error = await Assert.That(async () => await first.WaitAsync(deadline.Token))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(await second.WaitAsync(deadline.Token)).IsEqualTo(2);
        await Assert.That(dispatches.ToArray()).IsEquivalentTo(new[] { 2 });
        await Assert.That(queue.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecoveryDrainsInOrderAndNeverReplaysADispatchedFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = new ConcurrentQueue<int>();
        var failure = new RespireConnectionException("accepted outcome is ambiguous");
        var first = fixture.Queue.EnqueueAsync<int>((_, _) =>
        {
            calls.Enqueue(1);
            throw failure;
        }, deadline.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(deadline.Token);
        var second = fixture.Queue.EnqueueAsync(async (connection, token) =>
        {
            calls.Enqueue(2);
            using var reply = await connection.SendAsync(new Cmd(new Verb(-1, "PING")), token);
            return 2;
        }, deadline.Token).AsTask();
        var third = fixture.Queue.EnqueueAsync((_, _) =>
        {
            calls.Enqueue(3);
            return new ValueTask<int>(3);
        }, deadline.Token).AsTask();
        await Assert.That(fixture.Queue.Count).IsEqualTo(3);
        await fixture.ReleaseAsync();
        var error = await Assert.That(async () => await first).ThrowsExactly<RespireConnectionException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(await second).IsEqualTo(2);
        await Assert.That(await third).IsEqualTo(3);
        await Assert.That(calls.ToArray()).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
        await Assert.That(fixture.Queue.Count).IsEqualTo(0);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "SELECT 1")).IsEqualTo(2);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "PING")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationRemovesHeadOrTailAndReleasesCapacity(bool cancelHead)
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var calls = new ConcurrentQueue<int>();
        ValueTask<int> Record(int value) { calls.Enqueue(value); return new(value); }
        var first = fixture.Queue.EnqueueAsync((_, _) => Record(1), cancelHead ? cancelled.Token : deadline.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(deadline.Token);
        var second = fixture.Queue.EnqueueAsync((_, _) => Record(2), cancelHead ? deadline.Token : cancelled.Token).AsTask();
        var overflow = await Assert.That(async () => await fixture.Queue.EnqueueAsync((_, _) => Record(99), deadline.Token))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(overflow!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", fixture.Server.Port));
        await cancelled.CancelAsync();
        var cancelledTask = cancelHead ? first : second;
        var cancellation = await Assert.That(async () => await cancelledTask).Throws<OperationCanceledException>();
        await Assert.That(cancellation!.CancellationToken).IsEqualTo(cancelled.Token);
        await Assert.That(fixture.Queue.Count).IsEqualTo(1);
        var third = fixture.Queue.EnqueueAsync((_, _) => Record(3), deadline.Token).AsTask();
        await fixture.ReleaseAsync();
        await (cancelHead ? second : first).WaitAsync(deadline.Token);
        await third.WaitAsync(deadline.Token);
        await Assert.That(calls.ToArray()).IsEquivalentTo(new[] { cancelHead ? 2 : 1, 3 }, CollectionOrdering.Matching);
        await Assert.That(fixture.Queue.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EachWaitHasItsOwnDeadlineAndDoesNotCancelSharedRecovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var expired = new CancellationTokenSource();
        var calls = 0;
        var first = fixture.Queue.EnqueueAsync((_, _) => { Interlocked.Increment(ref calls); return new ValueTask<int>(1); }, expired.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(deadline.Token);
        var second = fixture.Queue.EnqueueAsync((_, _) => new ValueTask<int>(2), deadline.Token).AsTask();
        expired.CancelAfter(TimeSpan.FromMilliseconds(20));
        await Assert.That(async () => await first.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await fixture.ReleaseAsync();
        await Assert.That(await second.WaitAsync(deadline.Token)).IsEqualTo(2);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(fixture.Server.ReceivedCommands.Count(command => command == "SELECT 1")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalAndRetirementFailPendingWork(bool retire)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        ValueTask<int> Dispatch(RespireConnection _, CancellationToken token) { Interlocked.Increment(ref calls); return new(1); }
        var first = fixture.Queue.EnqueueAsync(Dispatch, deadline.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(deadline.Token);
        var second = fixture.Queue.EnqueueAsync(Dispatch, deadline.Token).AsTask();
        if (retire) await fixture.Client.Core.Multiplexer.RetireAsync().WaitAsync(deadline.Token);
        else await fixture.Client.DisposeAsync();
        foreach (var pending in new[] { first, second })
        {
            if (retire)
                await Assert.That(async () => await pending.WaitAsync(deadline.Token)).ThrowsExactly<RespireConnectionRetiredException>();
            else
                await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<ObjectDisposedException>();
        }
        if (retire)
            await Assert.That(async () => await fixture.Queue.EnqueueAsync(Dispatch)).ThrowsExactly<RespireConnectionRetiredException>();
        else
            await Assert.That(async () => await fixture.Queue.EnqueueAsync(Dispatch)).Throws<ObjectDisposedException>();
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(fixture.Queue.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingDemandRetriesFailedReplacementAndPropagatesExhaustion(bool exhaust)
    {
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (connection, _) => connection == 1 || exhaust && connection > 0
                ? "-TRYAGAIN controlled replacement failure\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 2 },
        });
        var queue = client.Core.Multiplexer.GetReconnectBacklog(4);
        await client.Core.Multiplexer.GetConnection().DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        ValueTask<int> Dispatch(RespireConnection _, CancellationToken token) { Interlocked.Increment(ref calls); return new(1); }
        var first = queue.EnqueueAsync(Dispatch, deadline.Token).AsTask();
        var second = queue.EnqueueAsync(Dispatch, deadline.Token).AsTask();
        if (exhaust)
        {
            foreach (var pending in new[] { first, second })
            {
                var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).ThrowsExactly<RespireReconnectLimitException>();
                await Assert.That(error!.InnerException).IsNotNull();
            }
            await Assert.That(async () => await queue.EnqueueAsync(Dispatch, deadline.Token)).ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(calls).IsEqualTo(0);
        }
        else
        {
            await Task.WhenAll(first, second).WaitAsync(deadline.Token);
            await Assert.That(calls).IsEqualTo(2);
        }
        await Assert.That(queue.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "SELECT 1")).IsEqualTo(3);
    }

    [Test]
    public async Task AsyncDispatchKeepsLaterUnitsBehindItAndOnlyWaitingEntriesConsumeCapacity()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Queue.EnqueueAsync(async (_, token) =>
        {
            entered.SetResult();
            await finish.Task.WaitAsync(token);
            return 1;
        }, deadline.Token).AsTask();
        await fixture.Handshake.Task.WaitAsync(deadline.Token);
        await fixture.ReleaseAsync();
        await entered.Task.WaitAsync(deadline.Token);
        await Assert.That(fixture.Queue.Count).IsEqualTo(0);
        var calls = 0;
        var second = fixture.Queue.EnqueueAsync((_, _) => { Interlocked.Increment(ref calls); return new ValueTask<int>(2); }, deadline.Token).AsTask();
        await Assert.That(fixture.Queue.Count).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(0);
        finish.SetResult();
        await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        await Assert.That(calls).IsEqualTo(1);
        // An empty drain can restart on the same healthy connection.
        await Assert.That(await fixture.Queue.EnqueueAsync((_, _) => new ValueTask<int>(3), deadline.Token)).IsEqualTo(3);
    }

    [Test]
    public async Task PreCancelledAdmissionDoesNotConsumeCapacityOrStartRecovery()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.That(async () => await fixture.Queue.EnqueueAsync((_, _) => new ValueTask<int>(1), cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(fixture.Queue.Count).IsEqualTo(0);
        await Assert.That(fixture.Handshake.Task.IsCompleted).IsFalse();
    }

    private static RespireOptions Options(int port) => new()
    {
        Endpoints = { new RespireEndpoint("127.0.0.1", port) },
        Connections = 1, Protocol = RespProtocol.Resp2, Database = 1,
        ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = TimeSpan.FromSeconds(5),
    };

    private sealed class Fixture(FakeRespServer server, RespireClient client,
        RespireConnectionMultiplexer.ReconnectBacklog queue, TaskCompletionSource handshake) : IAsyncDisposable
    {
        internal FakeRespServer Server { get; } = server;
        internal RespireClient Client { get; } = client;
        internal RespireConnectionMultiplexer.ReconnectBacklog Queue { get; } = queue;
        internal TaskCompletionSource Handshake { get; } = handshake;

        internal static async Task<Fixture> CreateAsync(int capacity = 8)
        {
            var server = new FakeRespServer(4, FakeRespServer.OkReply);
            try
            {
                var client = await RespireClient.ConnectAsync(Options(server.Port));
                var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                server.SuppressReply = command =>
                {
                    if (command != "SELECT 1") return false;
                    handshake.TrySetResult();
                    return true;
                };
                var queue = client.Core.Multiplexer.GetReconnectBacklog(capacity);
                await client.Core.Multiplexer.GetConnection().DisposeAsync();
                return new(server, client, queue, handshake);
            }
            catch { await server.DisposeAsync(); throw; }
        }

        internal Task ReleaseAsync() => Server.SendRawAsync(FakeRespServer.OkReply, 1);
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
