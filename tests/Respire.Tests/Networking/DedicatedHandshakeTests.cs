using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DedicatedHandshakeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FencingHandshakePreservesNormalDeadlinesOnReuse(bool fencing)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING",
        };
        server.DelayReply(0, 250);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1, CommandTimeout = TimeSpan.FromMilliseconds(50) }, null);
        if (!fencing)
        {
            await Assert.That(async () => await pool.RentAsync(CancellationToken.None))
                .Throws<RespireTimeoutException>();
            return;
        }

        var control = await pool.RentAsync(CancellationToken.None, armHandshakeDeadline: false);
        pool.Return(control);
        var ordinary = await pool.RentAsync(CancellationToken.None);
        await Assert.That(ordinary).IsSameReferenceAs(control);
        await Assert.That(async () => await ordinary.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireTimeoutException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingFencingHandshakeCanBeCancelled(bool disposePool)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 1 }, null);
        using var caller = new CancellationTokenSource();
        var pending = pool.RentAsync(caller.Token, armHandshakeDeadline: false).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposePool)
            await pool.DisposeAsync();
        else
            caller.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
    }
}
