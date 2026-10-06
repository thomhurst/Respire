using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PrimaryReadRetirementTests
{
    [Test]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(false, true, false)]
    [Arguments(true, true, false)]
    public async Task ReportedTimeoutReselectsOnlyRetiredConnectAttempt(
        bool allowFallback, bool retirePool, bool connectTimeout)
    {
        var limit = TimeSpan.FromSeconds(5);
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var core = client.Core;
        var replacement = await core.GetDedicatedPoolAsync(default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new RespireTimeoutException(connectTimeout ? "CONNECT" : "SELECT", limit,
            new OperationCanceledException(),
            RespireTimeoutDiagnostics.Capture(connectTimeout ? RespireCommandStage.Connecting : RespireCommandStage.Unknown));
        await using var original = new DedicatedConnectionPool("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2,
                TestingStreamFactory = async (_, _, _) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(limit);
                    throw failure;
                },
            }, null);
        core.TestingDedicatedPoolOverride = original;
        using var caller = new CancellationTokenSource();
        var pending = core.ReadRouter.RentDedicatedConnectionAsync(
            allowFallback ? RespireReadFrom.PrimaryPreferred : RespireReadFrom.Primary, caller.Token).AsTask();
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(limit);
            if (retirePool) retirement = original.RetireAsync().AsTask();
            // Publish a healthy replacement at the same address before delivering the
            // already-selected timeout. Retirement must not exclude that address.
            core.TestingDedicatedPoolOverride = replacement;
            release.TrySetResult();
            if (retirePool && connectTimeout)
            {
                var lease = await pending.WaitAsync(limit);
                await Assert.That(lease.Pool).IsSameReferenceAs(replacement);
                await Assert.That(lease.Connection.IsConnected).IsTrue();
                await Assert.That(lease.IsReplica).IsFalse();
            }
            else
            {
                var error = await Assert.That(async () => await pending.WaitAsync(limit))
                    .ThrowsExactly<RespireTimeoutException>();
                await Assert.That(error).IsSameReferenceAs(failure);
            }
            await Assert.That(caller.IsCancellationRequested).IsFalse();
        }
        finally
        {
            release.TrySetResult();
            core.TestingDedicatedPoolOverride = null;
            try
            {
                var lease = await pending.WaitAsync(limit);
                lease.Pool.Return(lease.Connection);
            }
            catch (Exception) when (pending.IsCompleted) { }
            if (retirement is not null) await retirement.WaitAsync(limit);
        }
    }
}
