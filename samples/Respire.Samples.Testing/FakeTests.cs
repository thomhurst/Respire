using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Samples.Testing;

public class FakeTests
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task SharedClientScenarios(RespProtocol protocol)
    {
        await using var server = new RespireFakeServer();
        await SharedScenarios.RunAsync(server.CreateOptions() with { Protocol = protocol });
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ExpiryUsesControlledTime(RespProtocol protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = protocol });
        using var stored = await client.ExecuteAsync(RespireCommands.String.SET, "session", "active", "PX", 1000);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.GetStringAsync("session")).IsEqualTo("active");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.GetStringAsync("session")).IsNull();
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task GateSeparatesAcceptedWriteFromItsReply(RespProtocol protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = protocol, Connections = 1 };
        await using var client = await RespireClient.ConnectAsync(options);
        await using var observer = await RespireClient.ConnectAsync(options);
        var gate = new RespireFakeGate();
        using var fault = server.InjectFault("SET", RespireFakeFault.Pause(gate, afterExecution: true));
        var pending = client.SetAsync("accepted", "value").AsTask();
        try
        {
            await fault.Matched.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(await observer.GetStringAsync("accepted")).IsEqualTo("value");
            await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        }
        finally
        {
            // Release even if an assertion fails; never leave an owned command paused.
            gate.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Assert.That(await pending).IsTrue();
    }

    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ScopedRejectionResetsWithoutChangingData(RespProtocol protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = protocol });
        using (var fault = server.InjectFault("SET", RespireFakeFault.Loading(), occurrences: null))
        {
            var error = await Assert.That(async () => await client.SetAsync("key", "rejected")).Throws<RespireServerException>();
            await Assert.That(error!.Message).StartsWith("LOADING");
            await Assert.That(fault.MatchedCount).IsEqualTo(1);
            await Assert.That(fault.ExecutionCount).IsEqualTo(0);
            await Assert.That(await client.GetStringAsync("key")).IsNull();
        }
        await client.SetAsync("key", "after scope");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("after scope");
    }
}
