using Respire.Benchmarks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BatchReplyGateTests
{
    [Test]
    [Arguments(1, 32)]
    [Arguments(1, 100)]
    [Arguments(2, 32)]
    [Arguments(2, 100)]
    public async Task GateHoldsRealRepliesAndPreservesPendingOrderAndReuse(int connections, int count)
    {
        await using var server = new FakeRespServer(connections, ":42\r\n"u8.ToArray());
        var gate = new BatchReplyGate();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = connections,
            Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
            TestingStreamFactory = gate.ConnectAsync,
        });
        for (var iteration = 0; iteration < 3; iteration++)
        {
            using var batch = client.CreateBatch();
            var pending = new RespirePending<long>[count];
            for (var i = 0; i < count; i++) pending[i] = batch.Increment($"gate:{i}");
            var received = gate.Arm();
            var executing = batch.ExecuteAsync();
            try
            {
                await received.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(executing.IsCompleted).IsFalse();
            }
            finally { gate.Release(); }
            var result = await executing;
            await Assert.That(result.Count).IsEqualTo(count);
            for (var i = 0; i < count; i++) await Assert.That(pending[i].Result).IsEqualTo(42L);
        }
        var commands = server.ReceivedCommands.Where(command => command.StartsWith("INCR gate:")).ToArray();
        await Assert.That(commands.Length).IsEqualTo(3 * count);
        for (var i = 0; i < commands.Length; i++)
            await Assert.That(commands[i]).IsEqualTo($"INCR gate:{i % count}");
    }

    [Test]
    public async Task DisposalCancelsHeldReadAndFinishesBatch()
    {
        await using var server = new FakeRespServer(":42\r\n"u8.ToArray());
        var gate = new BatchReplyGate();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
            TestingStreamFactory = gate.ConnectAsync,
        });
        using var batch = client.CreateBatch();
        _ = batch.Increment("gate:dispose");
        var received = gate.Arm();
        var executing = batch.TryExecuteAsync();
        try
        {
            await received.WaitAsync(TimeSpan.FromSeconds(5));
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await executing.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(result.FailureCount).IsEqualTo(1);
        }
        finally { gate.Release(); }
    }
}
