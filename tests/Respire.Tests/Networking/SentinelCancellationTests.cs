using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelCancellationTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task CancellationClassificationRequiresOwnedToken(bool primary, bool callerCancelled, bool ownedToken)
    {
        await using var sentinel = new FakeRespServer(
            Encoding.ASCII.GetBytes("*2\r\n$9\r\n127.0.0.1\r\n$4\r\n6379\r\n"), "*0\r\n"u8.ToArray());
        using var caller = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        OperationCanceledException? original = null;
        CancellationTokenSource? primaryTimeout = null;
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", sentinel.Port)],
            SentinelPrimaryName = "mymaster",
            CommandTimeout = primary ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(200),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            TestingStreamFactory = primary ? null : async (_, _, token) =>
            {
                await FailAsync(token);
                throw new InvalidOperationException("The controlled cancellation did not throw.");
            },
        };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options, async (_, _, token) =>
        {
            await FailAsync(token);
            return 0;
        }, caller.Token, createConnectTimeout: (token, _) =>
            primaryTimeout = CancellationTokenSource.CreateLinkedTokenSource(token)).AsTask();

        if (callerCancelled)
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(ownedToken ? caller.Token : unrelated.Token);
            if (!ownedToken) await Assert.That(error).IsSameReferenceAs(original);
        }
        else if (ownedToken && !primary)
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("SENTINEL GET-MASTER-ADDR-BY-NAME");
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)))
                .Throws<RespireConnectionException>();
            if (ownedToken)
            {
                await Assert.That(error!.InnerException is RespireTimeoutException).IsTrue();
                await Assert.That(((RespireTimeoutException)error.InnerException!).CommandName).IsEqualTo("CONNECT");
            }
            else await Assert.That(error!.InnerException).IsSameReferenceAs(original);
        }

        async Task FailAsync(CancellationToken token)
        {
            if (callerCancelled) caller.Cancel();
            // Cancel the primary deadline only after discovery reaches the controlled callback.
            // A short ConnectTimeout also bounds Sentinel setup and can expire before this point.
            else if (primary) primaryTimeout!.Cancel();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            original = new OperationCanceledException(ownedToken ? token : unrelated.Token);
            throw original;
        }
    }
}
