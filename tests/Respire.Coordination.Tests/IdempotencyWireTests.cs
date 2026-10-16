using System.Collections.Concurrent;
using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class IdempotencyWireTests
{
    [Test]
    public async Task InvalidAndCancelledCallsDoNotDispatch()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireIdempotency(client);
        var before = server.CommandsSeen;
        await Assert.That(() => new RespireIdempotency(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await store.TryBeginAsync("", TimeSpan.FromSeconds(1))).Throws<ArgumentException>();
        foreach (var ttl in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromTicks(9999) })
            await Assert.That(async () => await store.TryBeginAsync("key", ttl)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await store.CompleteAsync("key", "", default)).Throws<ArgumentException>();
        await Assert.That(async () => await store.AbandonAsync("key", null!)).Throws<ArgumentNullException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await store.TryBeginAsync("key", TimeSpan.FromSeconds(1), cancellationToken: canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await store.CompleteAsync("key", "owner", default, canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await store.AbandonAsync("key", "owner", canceled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(before);
    }

    [Test]
    [Arguments("*0\r\n")]
    [Arguments("*1\r\n:99\r\n")]
    [Arguments("*2\r\n:2\r\n$-1\r\n")]
    [Arguments(":1\r\n")]
    public async Task MalformedBeginRepliesFail(string response)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(response));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await new RespireIdempotency(client).TryBeginAsync("key", TimeSpan.FromSeconds(1)))
            .Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments("+OK\r\n")]
    [Arguments("$1\r\n0\r\n")]
    [Arguments("$-1\r\n")]
    [Arguments("*1\r\n:1\r\n")]
    [Arguments(":2\r\n")]
    public async Task MalformedTransitionRepliesFail(string response)
    {
        var reply = Encoding.ASCII.GetBytes(response);
        await using var server = new FakeRespServer(reply, reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var store = new RespireIdempotency(client);
        await Assert.That(async () => await store.CompleteAsync("key", "owner", default))
            .Throws<RespireProtocolException>();
        await Assert.That(async () => await store.AbandonAsync("key", "owner"))
            .Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HooksObserveEachLogicalOperationAndCanRejectBeforeDispatch(bool reject)
    {
        await using var server = new FakeRespServer("*1\r\n:0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray());
        var hook = new RecordingHook(reject);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
            CommandHook = hook, ThreadPoolMonitoring = false,
        });
        var before = server.CommandsSeen;
        var store = new RespireIdempotency(client);
        if (reject)
        {
            await Assert.That(async () => await store.TryBeginAsync("key", TimeSpan.FromSeconds(1))).Throws<InvalidOperationException>();
            await Assert.That(async () => await store.CompleteAsync("key", "owner", default)).Throws<InvalidOperationException>();
            await Assert.That(async () => await store.AbandonAsync("key", "owner")).Throws<InvalidOperationException>();
            await Assert.That(server.CommandsSeen).IsEqualTo(before);
        }
        else
        {
            var owner = await store.TryBeginAsync("key", TimeSpan.FromSeconds(1));
            await store.CompleteAsync("key", owner.OwnerToken!, default);
            await store.AbandonAsync("key", owner.OwnerToken!);
            await Assert.That(server.CommandsSeen - before).IsEqualTo(3);
        }
        await Assert.That(hook.Completed.Select(x => x.Command).SequenceEqual(
            new[] { "IDEMPOTENCY BEGIN", "IDEMPOTENCY COMPLETE", "IDEMPOTENCY ABANDON" })).IsTrue();
        await Assert.That(hook.Completed.All(x => x.Outcome ==
            (reject ? RespireCommandOutcome.Rejected : RespireCommandOutcome.Succeeded))).IsTrue();
    }

    private sealed class RecordingHook(bool reject) : RespireCommandHook
    {
        internal ConcurrentQueue<(string Command, RespireCommandOutcome Outcome)> Completed { get; } = new();
        public override object? OnExecuting(RespireCommandContext context)
            => reject ? throw new InvalidOperationException("Rejected") : null;
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
            => Completed.Enqueue((context.Command, outcome));
    }
}
