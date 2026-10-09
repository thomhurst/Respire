using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class StandaloneBatchFlushTests
{
    private static object? s_allocationControl;

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrderedAdmissionReroutesOnlyWhenConnectionIsNotPinned(bool pinned)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 2, options: new RespireConnectionOptions { Protocol = RespProtocol.Resp2 });
        var retired = multiplexer.GetConnection();
        await retired.RetireAsync();
        if (pinned)
        {
            await Assert.That(async () => await retired.EnqueuePinnedAsync(new RawCommand(FakeRespServer.PingFrame),
                default, "PING", pinToConnection: true)).Throws<RespireConnectionRetiredException>();
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
        }
        else
        {
            var reply = await retired.EnqueuePinnedAsync(new RawCommand(FakeRespServer.PingFrame),
                default, "PING", pinToConnection: false);
            using var value = await reply;
            await Assert.That(value.AsString()).IsEqualTo("PONG");
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "PING" }, CollectionOrdering.Matching);
        }
    }

    [Test]
    [NotInParallel]
    public async Task StartingOneHundredOperationsDoesNotAllocateOneTaskPerOperation()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        // Fixed wall-clock warmup gives background tiered compilation time to finish.
        // Do not choose the stopping point from allocation samples.
        for (var warmup = 0; warmup < 128; warmup++)
        {
            using var warm = CreateBatch(client, 100);
            _ = MeasureStart(warm, warmup % 2 == 0, out var execution);
            await execution;
            await Task.Delay(25);
        }

        server.SuppressReply = _ => true;
        var replyBytes = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(":1\r\n", 100)));
        using var batch = CreateBatch(client, 100);
        var expectedCommands = server.CommandsSeen + 100;
        ValueTask<RespireBatchResult> pending = default;
        var bytes = AllocationMeasurement.WithoutConcurrentGc(() => MeasureStart(batch, false, out pending));
        await WaitForCommandsAsync(server, expectedCommands).WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync(replyBytes);
        await pending;

        using var controlBatch = CreateBatch(client, 100);
        expectedCommands += 100;
        var controlBytes = AllocationMeasurement.WithoutConcurrentGc(() => MeasureStart(controlBatch, true, out pending));
        await WaitForCommandsAsync(server, expectedCommands).WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync(replyBytes);
        await pending;
        Console.WriteLine($"Standalone batch start: {bytes} bytes; escaping allocation control: {controlBytes} bytes.");
        // Includes the execution state machine; excludes queued operations and response-thread work.
        await Assert.That(bytes).IsLessThan(8_000);
        await Assert.That(controlBytes).IsGreaterThanOrEqualTo(8_192);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureStart(RespireBatch batch, bool allocateControl, out ValueTask<RespireBatchResult> pending)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        pending = batch.TryExecuteAsync();
        if (allocateControl) s_allocationControl = new byte[8_192];
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static RespireBatch CreateBatch(RespireClient client, int count)
    {
        var batch = client.CreateBatch();
        for (var index = 0; index < count; index++) batch.Increment($"allocation:{index}");
        return batch;
    }

    [Test]
    [Arguments(1)]
    [Arguments(32)]
    [Arguments(100)]
    public async Task IdleBatchUsesOneSocketWrite(int count)
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        var writes = new ConcurrentQueue<int>();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.WriteCompletedForTesting = bytes => { writes.Enqueue(bytes); written.TrySetResult(); };
        using var batch = client.CreateBatch();
        var pending = new RespirePending<long>[count];
        for (var index = 0; index < count; index++) pending[index] = batch.Increment($"counter:{index}");

        // Explicit pool dispatch exercises the inline flush wake-up that split idle batches.
        await Task.Run(async () => await batch.ExecuteAsync());
        await written.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(writes.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            Enumerable.Range(0, count).Select(index => $"INCR counter:{index}"), CollectionOrdering.Matching);
        foreach (var result in pending) await Assert.That(result.Result).IsEqualTo(1);
    }

    [Test]
    public async Task BatchLargerThanRingPreservesQueueOrder()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var batch = client.CreateBatch();
        for (var index = 0; index < 100; index++) _ = batch.Increment($"counter:{index}");
        await batch.ExecuteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            Enumerable.Range(0, 100).Select(index => $"INCR counter:{index}"), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ErrorsAndOwnedRepliesRetainTheirOperation()
    {
        await using var server = new FakeRespServer(
            "$3\r\none\r\n"u8.ToArray(), "-ERR wrong type\r\n"u8.ToArray(),
            "$3\r\ntwo\r\n"u8.ToArray(), ":7\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var first = batch.GetBytes("first");
        var failed = batch.Increment("failed");
        var third = batch.GetBytes("third");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.Failures.Count).IsEqualTo(1);
        await Assert.That(result.Failures[0].Index).IsEqualTo(1);
        await Assert.That(failed.Error).IsTypeOf<RespireServerException>();
        await Assert.That(first.Result).IsEquivalentTo("one"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(third.Result).IsEquivalentTo("two"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(await client.IncrementAsync("after")).IsEqualTo(7);
        await Assert.That(first.Result).IsEquivalentTo("one"u8.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CancellationDrainsAcceptedRepliesBeforeNextCommand()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray())
        {
            SuppressReply = command => command != "INCR after",
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        using var batch = client.CreateBatch();
        var first = batch.Increment("first");
        var second = batch.Increment("second");
        var third = batch.Increment("third");
        var execution = batch.TryExecuteAsync(cancellation.Token);
        await WaitForCommandsAsync(server, 3).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await execution.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(result.Failures.Count).IsEqualTo(3);
        await server.SendRawAsync(":1\r\n:1\r\n:1\r\n"u8.ToArray());
        await Assert.That(await client.IncrementAsync("after")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            new[] { "INCR first", "INCR second", "INCR third", "INCR after" }, CollectionOrdering.Matching);
    }

    private static async Task WaitForCommandsAsync(FakeRespServer server, int count)
    {
        while (server.CommandsSeen < count) await Task.Delay(1);
    }
}
