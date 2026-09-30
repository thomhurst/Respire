using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeTransactionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task QueuedWritesAndOwnedArgumentsWaitForExec(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Protocol = (RespProtocol)protocol };
        await using var observer = await RespireClient.ConnectAsync(options);
        await using var session = await TestRespSession.ConnectAsync(options);
        using (var multi = await session.CommandAsync("MULTI"))
            await Assert.That(multi.AsString()).IsEqualTo("OK");
        byte[] value = [0, 255, 128];
        using (var queued = await session.CommandBytesAsync("SET"u8.ToArray(), "key"u8.ToArray(), value))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        value[0] = 9;
        using (var overwritten = await session.CommandAsync("ECHO", new string('x', 8192)))
            await Assert.That(overwritten.AsString()).IsEqualTo("QUEUED");
        await Assert.That(await observer.ExistsAsync("key")).IsFalse();
        using var executed = await session.CommandAsync("EXEC");
        await Assert.That(executed.AsArray().Length).IsEqualTo(2);
        await Assert.That(executed.AsArray()[0].AsString()).IsEqualTo("OK");
        await Assert.That(executed.AsArray()[1].AsString().Length).IsEqualTo(8192);
        await Assert.That((await observer.GetBytesAsync("key"))!.AsSpan().SequenceEqual(new byte[] { 0, 255, 128 })).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExecDisconnectSeparatesAcceptanceWithoutReplay(bool afterExecution)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var observer = await RespireClient.ConnectAsync(server.CreateOptions());
        using var fault = server.InjectFault("EXEC", RespireFakeFault.Disconnect(afterExecution));
        await using var transaction = client.CreateTransaction();
        transaction.Increment("counter");
        transaction.Increment("counter");
        await Assert.That(async () => await transaction.CommitAsync().AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1 : 0);
        await Assert.That(await observer.GetStringAsync("counter")).IsEqualTo(afterExecution ? "2" : null);
    }

    [Test]
    public async Task WatchExpiryIsCheckedAtExecWithoutAnInterveningRead()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.SetAsync("expiring", "old", RespireExpiry.In(TimeSpan.FromSeconds(1)));
        await using var transaction = await client.CreateTransactionAsync(["expiring"]);
        transaction.Set("result", "must not execute");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await transaction.CommitAsync()).IsFalse();
        await Assert.That(await client.ExistsAsync("result")).IsFalse();
        // An already expired key is logically missing when WATCH starts.
        await using var next = await client.CreateTransactionAsync(["expiring"]);
        next.Set("result", "committed");
        await Assert.That(await next.CommitAsync()).IsTrue();
    }
}
